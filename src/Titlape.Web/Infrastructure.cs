using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Titlape.Core;
using Titlape.CrossCutting.Security;
using Titlape.Evaluation;
using Titlape.Orchestration;

namespace Titlape.Web;

public sealed record LoginRequest(string UserId);
public sealed record StartSessionRequest(LearnerLanguage? Language);
public sealed record LanguageRequest(LearnerLanguage Language);
public sealed record TurnRequest(string Text, double? ResponseSeconds);
public sealed record AlertDecisionRequest(AlertStatus Decision, string? Note);
public sealed record TrustRequest(int Score);
public sealed record ProposalDecisionRequest(bool Approve, List<string>? Sequence, string? Note);
public sealed record PathwayOverrideRequest(List<string> Sequence);
public sealed record ConsentRequest(string Purpose, bool Granted);
public sealed record ConnectivityRequest(bool Online);
public sealed record PowerRequest(double BatteryPercent, bool SolarCharging);

public static class HttpContextExtensions
{
    public static Principal Principal(this HttpContext ctx) =>
        ctx.Items["principal"] as Principal ?? throw new UnauthorizedAccessException();
}

/// <summary>
/// Zero-Trust endpoint filter: every API call must present a valid signed bearer token with an allowed
/// role; nothing is trusted because it comes from the local hub network.
/// </summary>
public sealed class RequireRole(params Role[] roles) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var platform = http.RequestServices.GetRequiredService<TitlapePlatform>();
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : http.Request.Query["access_token"].ToString();
        var result = platform.Tokens.Validate(token);
        if (!result.Ok) return Results.Problem(result.Error, statusCode: 401);
        if (!roles.Contains(result.Value!.Role))
        {
            platform.Audit.Append(result.Value.Subject, "access.denied", http.Request.Path, $"role {result.Value.Role} not permitted");
            return Results.Problem("This role may not use this endpoint.", statusCode: 403);
        }
        http.Items["principal"] = result.Value;
        try { return await next(ctx); }
        catch (UnauthorizedAccessException ex) { return Results.Problem(ex.Message, statusCode: 403); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: 404); }
        catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
    }
}

/// <summary>Fans out newly raised alerts to Teacher Dashboard SSE subscribers.</summary>
public sealed class AlertBroadcaster
{
    private readonly ConcurrentDictionary<Guid, (string ClassId, Channel<TeacherAlert> Channel)> _subs = new();

    public void Publish(TeacherAlert alert)
    {
        foreach (var (classId, ch) in _subs.Values)
            if (classId == alert.ClassId) ch.Writer.TryWrite(alert);
    }

    public async IAsyncEnumerable<TeacherAlert> Subscribe(string classId, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<TeacherAlert>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
        _subs[id] = (classId, ch);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TeacherAlert? next = null;
                try { next = await ch.Reader.ReadAsync(ct); }
                catch (OperationCanceledException) { yield break; }
                yield return next;
            }
        }
        finally { _subs.TryRemove(id, out _); }
    }
}

/// <summary>
/// Edge background duties: opportunistic delta-sync when the backhaul is up, simulated solar battery
/// drift, and periodic encrypted snapshots.
/// </summary>
public sealed class EdgeBackgroundService(TitlapePlatform platform, ILogger<EdgeBackgroundService> log, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Titlape:SyncIntervalSeconds", 60));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                var hour = DateTime.Now.Hour;
                platform.Power.SolarCharging = hour is >= 7 and < 18;
                platform.Power.BatteryPercent = Math.Clamp(platform.Power.BatteryPercent + (platform.Power.SolarCharging ? 0.5 : -0.4), 5, 100);
                var r = platform.Sync.SyncNow();
                if (r.Performed && r.WireBytes > 0) log.LogInformation("Synced {Bytes} bytes ({Docs} docs pushed)", r.WireBytes, r.DocsPushed);
                platform.SaveState();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogWarning(ex, "Edge background cycle failed"); }
        }
    }
}
