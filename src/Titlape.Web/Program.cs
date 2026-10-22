using System.Text.Json;
using System.Text.Json.Serialization;
using Titlape.Context.Curriculum;
using Titlape.Core;
using Titlape.CrossCutting.Security;
using Titlape.Evaluation;
using Titlape.Orchestration;
using Titlape.Web;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Titlape").Get<TitlapeOptions>() ?? new TitlapeOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TitlapePlatform>(_ => new TitlapePlatform(options));
builder.Services.AddSingleton<AlertBroadcaster>();
builder.Services.AddHostedService<EdgeBackgroundService>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

var app = builder.Build();
var platform = app.Services.GetRequiredService<TitlapePlatform>();
var broadcaster = app.Services.GetRequiredService<AlertBroadcaster>();
platform.Alerts.Raised += broadcaster.Publish;

if (builder.Configuration.GetValue("Titlape:SeedDemoData", true))
{
    app.Logger.LogInformation("Seeding demo activity through the agent pipeline…");
    await DemoSeeder.SeedAsync(platform);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (ctx, next) =>
{
    // Defence-in-depth headers for the PWA.
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";
    await next();
});

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

// ---------------------------------------------------------------- Auth (Layer 6 security)
app.MapGet("/api/demo/users", (TitlapePlatform p) => new
{
    students = p.Roster.Students.OrderBy(s => s.SchoolId).ThenBy(s => s.FullName).Select(s => new
    {
        id = s.Id, name = s.FullName, school = p.Roster.School(s.SchoolId).Name, language = s.HomeLanguage.ToString()
    }),
    teachers = p.Roster.Teachers.Select(t => new { id = t.Id, name = t.Name, school = p.Roster.School(t.SchoolId).Name, classes = t.ClassIds }),
    admin = new { id = "admin", name = "Hub Administrator" }
});

app.MapPost("/api/auth/login", (LoginRequest req, TitlapePlatform p) =>
{
    try
    {
        var token = p.Login(req.UserId);
        var principal = p.Tokens.Validate(token).Value!;
        p.Audit.Append(principal.Subject, "auth.login", principal.Role.ToString(), principal.SchoolId);
        return Results.Ok(new { token, role = principal.Role.ToString(), subject = principal.Subject, school = principal.SchoolId, classes = principal.ClassIds });
    }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
});

// ---------------------------------------------------------------- Student PWA
var student = app.MapGroup("/api/student").AddEndpointFilter(new RequireRole(Role.Student));

student.MapPost("/sessions", (StartSessionRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    try { return Results.Ok(p.Orchestrator.StartSession(ctx.Principal(), req.Language)); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 403); }
});

student.MapPut("/sessions/{id}/language", (string id, LanguageRequest req, TitlapePlatform p) =>
{
    p.Orchestrator.SetLanguage(id, req.Language);
    return Results.NoContent();
});

student.MapPost("/sessions/{id}/turns", async (string id, TurnRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    var r = await p.Orchestrator.SubmitAsync(ctx.Principal(), id, req.Text, req.ResponseSeconds, ctx.RequestAborted);
    return Results.Ok(StudentTurn(r, p));
});

// Server-Sent Events: the response is validated by the Safety Agent BEFORE the first token is streamed.
student.MapPost("/sessions/{id}/turns/stream", async (string id, TurnRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    var r = await p.Orchestrator.SubmitAsync(ctx.Principal(), id, req.Text, req.ResponseSeconds, ctx.RequestAborted);
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    foreach (var chunk in Titlape.Models.InstrumentedModel.ChunkWords(r.Response))
    {
        await ctx.Response.WriteAsync($"event: token\ndata: {JsonSerializer.Serialize(chunk)}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        await Task.Delay(18, ctx.RequestAborted);
    }
    await ctx.Response.WriteAsync($"event: done\ndata: {JsonSerializer.Serialize(StudentTurn(r, p), json)}\n\n", ctx.RequestAborted);
});

student.MapGet("/progress", (HttpContext ctx, TitlapePlatform p) =>
{
    var s = p.Roster.Student(ctx.Principal().Subject)!;
    return Results.Ok(new
    {
        name = s.FullName.Split(' ')[0],
        language = s.HomeLanguage,
        skills = CurriculumPack.Skills.Select(k => new { code = k.Code, title = k.Title, mastery = p.Memory.Get(s.PseudonymId, k.Code) }),
        mastered = p.Memory.MasteredSkills(s.PseudonymId),
        pathway = p.Pathways.Get(s.PseudonymId)
    });
});

// ---------------------------------------------------------------- Teacher Dashboard
var teacher = app.MapGroup("/api/teacher").AddEndpointFilter(new RequireRole(Role.Teacher, Role.Admin));

teacher.MapGet("/classes/{classId}/overview", (string classId, HttpContext ctx, TitlapePlatform p) =>
{
    var cls = p.Roster.Class(classId);
    if (cls is null) return Results.NotFound();
    if (!Authorize(ctx, p, "read", new ResourceAttributes("class", cls.SchoolId, classId, ContainsPii: true))) return Denied();
    var pupils = p.Roster.InClass(classId).ToList();
    var pseudonyms = pupils.Select(s => s.PseudonymId).ToList();
    var rows = pupils.Select(s =>
    {
        var path = p.Pathways.Get(s.PseudonymId)!;
        var risk = p.Alerts.AssessRisk(s.PseudonymId, classId, pseudonyms, path.SkillSequence, path.CurrentSkill);
        return new
        {
            studentId = s.Id, pseudonym = s.PseudonymId, name = s.FullName, language = s.HomeLanguage, gender = s.Gender,
            consentTutoring = p.Consent.HasConsent(s.Id), consentTranscript = p.Consent.HasConsent(s.Id, ConsentRegistry.Transcript),
            mastery = CurriculumPack.Skills.ToDictionary(k => k.Code, k => p.Memory.Get(s.PseudonymId, k.Code)),
            misconceptions = p.Memory.Misconceptions(s.PseudonymId),
            pathway = path,
            risk = Math.Round(risk.Risk, 3),
            attributions = risk.Attributions
        };
    }).ToList();
    var names = pupils.ToDictionary(s => s.PseudonymId, s => s.FullName);
    return Results.Ok(new
    {
        classroom = cls,
        school = p.Roster.School(cls.SchoolId),
        skills = CurriculumPack.Skills,
        misconceptionNames = CurriculumPack.MisconceptionNames,
        students = rows,
        alerts = p.Alerts.ForClass(classId).Take(40),
        proposals = p.Pathways.Proposals(pr => pseudonyms.Contains(pr.StudentId)).Take(30),
        names,
        metrics = p.Alerts.Metrics(classId),
        trust = Trust(p)
    });
});

teacher.MapGet("/classes/{classId}/traces", (string classId, int? n, HttpContext ctx, TitlapePlatform p) =>
{
    var cls = p.Roster.Class(classId);
    if (cls is null) return Results.NotFound();
    if (!Authorize(ctx, p, "read", new ResourceAttributes("trace", cls.SchoolId, classId))) return Denied();
    var pseudonyms = p.Roster.InClass(classId).Select(s => s.PseudonymId).ToHashSet();
    return Results.Ok(p.Tracer.Recent(n ?? 40, t => pseudonyms.Contains(t.LearnerPseudonym)).Select(t => new
    {
        t.TraceId, t.LearnerPseudonym, t.SkillCode, t.StartedAt, t.Outcome, t.TotalMs, steps = t.Steps.Count,
        blocked = t.Steps.Count(s => s.Step == "validate" && s.Data.GetValueOrDefault("allowed") == "False")
    }));
});

teacher.MapGet("/traces/{traceId}", (string traceId, HttpContext ctx, TitlapePlatform p) =>
{
    var t = p.Tracer.Get(traceId);
    if (t is null) return Results.NotFound();
    var s = p.Roster.ByPseudonym(t.LearnerPseudonym)!;
    if (!Authorize(ctx, p, "read", new ResourceAttributes("trace", s.SchoolId, s.ClassId))) return Denied();
    return Results.Ok(t);
});

teacher.MapPost("/alerts/{id}/decision", (string id, AlertDecisionRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    var a = p.Alerts.Get(id);
    if (a is null) return Results.NotFound();
    var cls = p.Roster.Class(a.ClassId)!;
    if (!Authorize(ctx, p, "decide", new ResourceAttributes("alert", cls.SchoolId, cls.Id))) return Denied();
    return Results.Ok(p.Alerts.Decide(id, ctx.Principal().Subject, req.Decision, req.Note));
});

teacher.MapPost("/alerts/{id}/trust", (string id, TrustRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    var a = p.Alerts.Get(id);
    if (a is null) return Results.NotFound();
    var cls = p.Roster.Class(a.ClassId)!;
    if (!Authorize(ctx, p, "read", new ResourceAttributes("alert", cls.SchoolId, cls.Id))) return Denied();
    p.Alerts.RecordTrust(id, ctx.Principal().Subject, req.Score);
    return Results.Ok(Trust(p));
});

teacher.MapPost("/proposals/{id}/decision", (string id, ProposalDecisionRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    var prop = p.Pathways.GetProposal(id);
    if (prop is null) return Results.NotFound();
    var s = p.Roster.ByPseudonym(prop.StudentId)!;
    if (!Authorize(ctx, p, "decide", new ResourceAttributes("pathway", s.SchoolId, s.ClassId))) return Denied();
    var who = ctx.Principal().Subject;
    var decided = p.Pathways.Decide(id, who, req.Approve, req.Sequence, req.Note);
    p.Audit.Append(who, $"pathway.{decided.Status.ToString().ToLowerInvariant()}", id,
        $"{string.Join(">", p.Pathways.Get(prop.StudentId)!.SkillSequence)} {req.Note}");
    foreach (var a in p.Alerts.ForClass(s.ClassId).Where(a => a.ProposalId == id && a.Status == AlertStatus.Pending))
        p.Alerts.Decide(a.Id, who, decided.Status, req.Note);
    return Results.Ok(decided);
});

teacher.MapPut("/students/{pseudonym}/pathway", (string pseudonym, PathwayOverrideRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    var s = p.Roster.ByPseudonym(pseudonym);
    if (s is null) return Results.NotFound();
    if (!Authorize(ctx, p, "decide", new ResourceAttributes("pathway", s.SchoolId, s.ClassId))) return Denied();
    if (req.Sequence.Count == 0 || req.Sequence.Any(c => CurriculumPack.Skills.All(k => k.Code != c))) return Results.BadRequest("Unknown skill code.");
    var path = p.Pathways.Override(pseudonym, ctx.Principal().Subject, req.Sequence);
    p.Audit.Append(ctx.Principal().Subject, "pathway.override", pseudonym, string.Join(">", req.Sequence));
    return Results.Ok(path);
});

teacher.MapPost("/students/{studentId}/consent", (string studentId, ConsentRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    var s = p.Roster.Student(studentId);
    if (s is null) return Results.NotFound();
    if (!Authorize(ctx, p, "decide", new ResourceAttributes("consent", s.SchoolId, s.ClassId, ContainsPii: true))) return Denied();
    p.Consent.Record(studentId, $"recorded-by:{ctx.Principal().Subject}", req.Granted, req.Purpose);
    p.Audit.Append(ctx.Principal().Subject, "consent.recorded", studentId, $"{req.Purpose}={req.Granted}");
    return Results.Ok(p.Consent.For(studentId));
});

// Live alert stream (SSE) for the dashboard.
teacher.MapGet("/classes/{classId}/live", async (string classId, HttpContext ctx, TitlapePlatform p, AlertBroadcaster b) =>
{
    var cls = p.Roster.Class(classId);
    if (cls is null || !Authorize(ctx, p, "read", new ResourceAttributes("class", cls.SchoolId, classId))) { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await foreach (var alert in b.Subscribe(classId, ctx.RequestAborted))
    {
        await ctx.Response.WriteAsync($"event: alert\ndata: {JsonSerializer.Serialize(alert, json)}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
});

// ---------------------------------------------------------------- Edge node operations
var ops = app.MapGroup("/api/ops").AddEndpointFilter(new RequireRole(Role.Admin, Role.Teacher));
var opsAdmin = app.MapGroup("/api/ops").AddEndpointFilter(new RequireRole(Role.Admin));

ops.MapGet("/status", (TitlapePlatform p) =>
{
    var route = p.Router.Route("socratic_hint");
    return Results.Ok(new
    {
        node = p.Options.EdgeNodeId,
        online = p.Connectivity.IsOnline,
        backhaul = p.Connectivity.BackhaulKind,
        power = new { p.Power.BatteryPercent, p.Power.SolarCharging, p.Power.LowPowerMode },
        routing = new { model = route.Model.Name, site = route.Model.Site, route.Reason },
        engines = p.Router.AllEngines.Select(e => new
        {
            e.Name, e.Site, e.Quantization, e.IsAvailable, verified = p.Models.IsVerified(e.Name),
            stats = e is Titlape.Models.InstrumentedModel im ? (object)Stats(im) : null
        }),
        catalogue = p.Models.Manifests,
        verifications = p.Models.Verifications,
        replica = new { edgeDocs = p.EdgeReplica.Count, cloudDocs = p.CloudReplica.Count },
        sync = p.Sync.History.TakeLast(15).Reverse(),
        bus = new { p.Bus.MessagesPublished, p.Bus.BytesPublished },
        audit = new { entries = p.Audit.Count, verification = p.Audit.Verify() },
        graph = p.Orchestrator.GraphNodes,
        toolGrants = p.ToolPolicy.Snapshot(),
        guardrails = new
        {
            p.Guardrails.Policy.OffCurriculumMinCoverage,
            injectionRules = p.Guardrails.Policy.PromptInjectionPatterns.Count,
            safeguardingRules = p.Guardrails.Policy.SafeguardingPatterns.Count
        },
        rag = new { chunks = p.Rag.ChunkCount, pack = CurriculumPack.PackId, piiRedactionsAtIndex = p.Rag.PiiRedactionsAtIndex },
        sessions = p.Orchestrator.ActiveSessions
    });

    static object Stats(Titlape.Models.InstrumentedModel m)
    {
        var s = m.Stats.Summary();
        return new { s.MeanTokensPerSecond, s.P50LatencyMs, s.P95LatencyMs, s.Samples, m.Stats.TotalCalls };
    }
});

ops.MapGet("/audit", (int? n, TitlapePlatform p) => Results.Ok(new { verification = p.Audit.Verify(), entries = p.Audit.Entries(n ?? 100) }));

ops.MapGet("/bias", (TitlapePlatform p) =>
{
    var events = p.Interactions.Events();
    var byGender = events.Select(e => (G: p.Roster.ByPseudonym(e.Pseudonym)?.Gender ?? "?", e.Correct)).ToList();
    var traces = p.Tracer.Recent(5000);
    var blocks = traces.Select(t => (Lang: p.Roster.ByPseudonym(t.LearnerPseudonym)?.HomeLanguage.ToString() ?? "?",
        Blocked: t.Steps.Any(s => s.Step == "validate" && s.Data.GetValueOrDefault("allowed") == "False"))).ToList();
    return Results.Ok(new[]
    {
        p.Bias.Audit("correct-answer rate", "home language", events, e => e.Language, e => e.Correct, outcomeIsAdverse: false),
        p.Bias.Audit("correct-answer rate", "gender", byGender, x => x.G, x => x.Correct, outcomeIsAdverse: false),
        p.Bias.Audit("draft block rate", "home language", blocks, x => x.Lang, x => x.Blocked, outcomeIsAdverse: true),
    });
});

opsAdmin.MapPost("/connectivity", (ConnectivityRequest req, HttpContext ctx, TitlapePlatform p) =>
{
    p.Connectivity.Set(req.Online);
    p.Audit.Append(ctx.Principal().Subject, "ops.connectivity", p.Options.EdgeNodeId, req.Online ? "online" : "offline");
    return Results.Ok(new { online = p.Connectivity.IsOnline });
});

opsAdmin.MapPost("/power", (PowerRequest req, TitlapePlatform p) =>
{
    p.Power.BatteryPercent = Math.Clamp(req.BatteryPercent, 0, 100);
    p.Power.SolarCharging = req.SolarCharging;
    return Results.Ok(p.Power);
});

opsAdmin.MapPost("/sync", (TitlapePlatform p) => Results.Ok(p.Sync.SyncNow()));

opsAdmin.MapPost("/evaluation", async (EvaluationConfig? cfg, HttpContext ctx) =>
{
    var report = await EvaluationRunner.RunAsync(cfg ?? new EvaluationConfig(), ctx.RequestAborted);
    return Results.Json(report, json);
});

app.MapFallbackToFile("index.html");
app.Run();

static bool Authorize(HttpContext ctx, TitlapePlatform p, string action, ResourceAttributes r)
{
    var principal = ctx.Principal();
    var d = p.Abac.Evaluate(principal, action, r);
    if (!d.Allowed) p.Audit.Append(principal.Subject, "access.denied", $"{r.Kind}:{r.ClassId ?? r.SchoolId}", d.Reason);
    return d.Allowed;
}

static IResult Denied() => Results.Problem("Access denied by policy (tenant or class scope).", statusCode: 403);

static object Trust(TitlapePlatform p)
{
    var (mean, n) = p.Alerts.TrustScore();
    return new { mean = Math.Round(mean, 2), n };
}

static object StudentTurn(TurnResult r, TitlapePlatform p) => new
{
    r.SessionId, r.Response, r.Action, r.NewItem, r.ItemPrompt, r.SkillCode,
    skillTitle = CurriculumPack.Skill(r.SkillCode).Title, r.ItemsCompleted, r.MasteryAfter, r.LatencyMs
};

public partial class Program;
