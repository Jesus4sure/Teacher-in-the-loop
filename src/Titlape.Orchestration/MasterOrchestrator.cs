using System.Collections.Concurrent;
using System.Diagnostics;
using Titlape.Agents;
using Titlape.Agents.Graph;
using Titlape.Context.Curriculum;
using Titlape.Context.Memory;
using Titlape.Core;
using Titlape.CrossCutting;
using Titlape.CrossCutting.Security;
using Titlape.Data.Telemetry;
using Titlape.Governance.Audit;
using Titlape.Governance.Guardrails;
using Titlape.Governance.Tracing;

namespace Titlape.Orchestration;

public sealed record SessionView(
    string SessionId,
    string Pseudonym,
    string SkillCode,
    string SkillTitle,
    string ItemPrompt,
    string TutorMessage,
    LearnerLanguage Language,
    int ItemsCompleted,
    double Mastery);

public sealed record TurnResult(
    string SessionId,
    string Response,
    string Action,
    string TraceId,
    string? Misconception,
    double? MasteryBefore,
    double? MasteryAfter,
    string ModelUsed,
    string RoutingReason,
    int BlockedDrafts,
    bool UsedSafeFallback,
    bool NewItem,
    string ItemPrompt,
    string SkillCode,
    int ItemsCompleted,
    IReadOnlyList<Checkpoint> Checkpoints,
    double LatencyMs);

/// <summary>
/// Master Orchestrator (Layer 6). Owns tutoring sessions, enforces consent and access policy, runs the
/// Layer 5 agent graph for each learner turn, follows only teacher-approved pathways, records anonymised
/// telemetry, and converts agent signals into teacher alerts.
/// </summary>
public sealed class MasterOrchestrator
{
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();
    private readonly AgentServices _svc;
    private readonly StateGraph<TutorState> _graph;
    private readonly Roster _roster;
    private readonly ConsentRegistry _consent;
    private readonly AbacPolicyEngine _abac;
    private readonly InteractionLog _log;
    private readonly AlertService _alerts;
    private readonly AuditLog _audit;
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    public MasterOrchestrator(AgentServices svc, Roster roster, ConsentRegistry consent, AbacPolicyEngine abac,
        InteractionLog log, AlertService alerts, AuditLog audit)
    {
        _svc = svc; _roster = roster; _consent = consent; _abac = abac; _log = log; _alerts = alerts; _audit = audit;
        _graph = TutoringGraph.Build(svc, RespondAsync);
    }

    public IReadOnlyCollection<string> GraphNodes => _graph.Nodes;
    public int ActiveSessions => _sessions.Count;

    public static IReadOnlyList<string> DefaultPathway => CurriculumPack.Skills.Select(s => s.Code).ToList();

    public SessionView StartSession(Principal p, LearnerLanguage? language = null, int? seed = null)
    {
        var student = _roster.Student(p.Subject) ?? throw new UnauthorizedAccessException("Unknown learner.");
        var decision = _abac.Evaluate(p, "tutor", new ResourceAttributes("session", student.SchoolId, student.ClassId, student.Id));
        if (!decision.Allowed) { _audit.Append(p.Subject, "access.denied", "session", decision.Reason); throw new UnauthorizedAccessException(decision.Reason); }
        if (!_consent.HasConsent(student.Id))
            throw new InvalidOperationException("Guardian consent for tutoring has not been recorded. Please ask your teacher.");

        var path = _svc.Pathways.GetOrCreate(student.PseudonymId, DefaultPathway);
        var session = new SessionContext
        {
            SessionId = Guid.NewGuid().ToString("N")[..16],
            Pseudonym = student.PseudonymId,
            ClassId = student.ClassId,
            SchoolId = student.SchoolId,
            Language = language ?? student.HomeLanguage,
            Consent = _consent.HasConsent(student.Id, ConsentRegistry.Transcript),
            Seed = seed ?? Random.Shared.Next()
        };
        _sessions[session.SessionId] = session;
        var opening = NextItem(session, path);
        session.Transcript.Add(new TutorTurn(session.SessionId, 0, "tutor", opening, DateTimeOffset.UtcNow));
        _audit.Append(session.Pseudonym, "session.start", session.SessionId, $"language={session.Language}");
        return View(session, opening);
    }

    public SessionContext? Session(string id) => _sessions.GetValueOrDefault(id);

    public void SetLanguage(string sessionId, LearnerLanguage language)
    {
        if (_sessions.TryGetValue(sessionId, out var s)) s.Language = language;
    }

    public async Task<TurnResult> SubmitAsync(Principal p, string sessionId, string text, double? responseSeconds = null, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) throw new KeyNotFoundException("Session not found.");
        var student = _roster.ByPseudonym(session.Pseudonym)!;
        if (p.Role == Role.Student && p.Subject != student.Id) throw new UnauthorizedAccessException("Not your session.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Empty message.");
        if (text.Length > 500) text = text[..500];

        // Edge hardware is single-tenant per hub: serialise turns to bound memory/CPU on low-power nodes.
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var act = TitlapeTelemetry.Source.StartActivity("orchestrator.turn", ActivityKind.Server);
            var sw = Stopwatch.StartNew();
            var trace = _svc.Tracer.Begin(session.Pseudonym, session.SessionId);
            trace.SkillCode = session.CurrentItem?.Item.SkillCode;
            var state = new TutorState { Session = session, Trace = trace, LearnerInput = text };
            var responseTime = responseSeconds ?? (DateTimeOffset.UtcNow - session.ItemShownAt).TotalSeconds;
            state.PlanSlots["response_seconds"] = responseTime.ToString("0.0");

            var checkpoints = await _graph.RunAsync(state, ct).ConfigureAwait(false);
            sw.Stop();
            trace.TotalMs = sw.Elapsed.TotalMilliseconds;

            ProcessSignals(state, student);
            return new TurnResult(sessionId, state.FinalResponse, state.Action.ToString(), trace.TraceId, state.Misconception,
                state.MasteryUpdate?.Before.PKnown, state.MasteryUpdate?.After.PKnown, state.ModelUsed, state.RoutingReason,
                state.BlockedDrafts, state.UsedSafeFallback, state.AdvanceToNextItem, session.CurrentItem!.Item.Prompt,
                session.CurrentItem.Item.SkillCode, session.ItemsCompleted, checkpoints, sw.Elapsed.TotalMilliseconds);
        }
        finally { _turnGate.Release(); }
    }

    private Task RespondAsync(TutorState s, CancellationToken ct)
    {
        var session = s.Session;
        var gen = session.CurrentItem!;
        var final = s.Draft;

        if (s.Check is { Outcome: not AnswerOutcome.Unparseable } check && s.Action is TutorAction.Praise or TutorAction.Hint)
        {
            double rt = double.TryParse(s.PlanSlots.GetValueOrDefault("response_seconds"), out var r) ? r : 0;
            _log.Record(new InteractionEvent(Guid.NewGuid().ToString("N"), session.Pseudonym, session.ClassId, gen.Item.SkillCode,
                gen.Item.Id, check.Outcome == AnswerOutcome.Correct, session.HintsUsedOnItem, Math.Round(rt, 1),
                s.Misconception, session.Language.ToString(), DateTimeOffset.UtcNow));
        }

        if (s.Action == TutorAction.Hint)
        {
            session.HintsUsedOnItem++;
            if (s.PlanSlots.ContainsKey("metaphor")) session.MetaphorUsedOnItem = true;
        }

        if (s.Action == TutorAction.Praise && s.AdvanceToNextItem)
        {
            session.ItemsCompleted++;
            var path = _svc.Pathways.Get(session.Pseudonym)!;
            var mastery = _svc.Memory.Get(session.Pseudonym, gen.Item.SkillCode);
            if (mastery.Mastered && path.CurrentSkill == gen.Item.SkillCode)
            {
                path.CurrentIndex++;
                _svc.Tracer.Step(s.Trace, "orchestrator", "advance",
                    $"Skill {gen.Item.SkillCode} mastered ({mastery.PKnown:P0}); moving to the next skill in the teacher-approved pathway.", null);
                final += " You have mastered this skill!";
            }
            final += "\n\nNext question: " + NextItem(session, path, announce: false);
        }

        s.FinalResponse = final.Trim();
        // Only the PII-redacted learner text is kept, and only for the life of the session.
        session.Transcript.Add(new TutorTurn(session.SessionId, session.Transcript.Count, "learner", s.RedactedInput, DateTimeOffset.UtcNow, s.Trace.TraceId));
        session.Transcript.Add(new TutorTurn(session.SessionId, session.Transcript.Count, "tutor", s.FinalResponse, DateTimeOffset.UtcNow, s.Trace.TraceId));
        _log.StoreTranscript(session.Pseudonym, session.SessionId, session.Transcript.Count, s.RedactedInput + " || " + s.FinalResponse, session.Consent);
        s.Trace.Outcome = $"{s.Action}{(s.UsedSafeFallback ? " (safe fallback)" : "")}";
        _svc.Tracer.Step(s.Trace, "orchestrator", "respond",
            $"Validated response delivered to the learner ({s.Action.ToString().ToLowerInvariant()}).", null);
        return Task.CompletedTask;
    }

    private void ProcessSignals(TutorState s, Student student)
    {
        var classId = s.Session.ClassId;
        string? proposalId = null;
        foreach (var sig in s.Signals)
        {
            switch (sig.Kind)
            {
                case "safeguarding":
                    _alerts.Safeguarding(classId, s.Session.Pseudonym, s.Trace.TraceId);
                    _audit.Append("safety", "safeguarding.escalated", s.Session.SessionId, "teacher alerted");
                    break;
                case "misconception":
                    _alerts.Misconception(classId, sig.Data["skill"], sig.Data["code"], s.Session.Pseudonym, s.Trace.TraceId,
                        _roster.InClass(classId).Count());
                    break;
                case "pathway_proposal":
                    proposalId = sig.Data["proposal_id"];
                    _audit.Append(DiagnosticAgent.Name, "pathway.proposed", proposalId, sig.Detail);
                    break;
            }
        }
        if (s.InputVerdict is { } iv)
            foreach (var v in iv.Violations.Where(v => v.Rule is "prompt_injection" or "toxicity"))
                _alerts.InputFlag(classId, s.Session.Pseudonym, v.Rule, s.Trace.TraceId);
        if (s.BlockedDrafts > 0)
            _audit.Append(SafetyAgent.Name, "draft.blocked", s.Trace.TraceId, string.Join(" | ", s.ReflectionFeedback));

        if (s.MasteryUpdate is not null)
        {
            var path = _svc.Pathways.Get(s.Session.Pseudonym)!;
            var classmates = _roster.InClass(classId).Select(x => x.PseudonymId).ToList();
            var ra = _alerts.AssessRisk(s.Session.Pseudonym, classId, classmates, path.SkillSequence, path.CurrentSkill);
            _alerts.MaybeRiskAlert(ra, classId, s.Trace.TraceId, proposalId, s.Session.CurrentItem?.Item.SkillCode);
        }
    }

    private string NextItem(SessionContext session, LearningPathway path, bool announce = true)
    {
        var skill = path.CurrentSkill
                    ?? path.SkillSequence.OrderBy(sk => _svc.Memory.Get(session.Pseudonym, sk).PKnown).First();
        var rng = new Random(session.Seed + session.ItemsCompleted * 31 + session.Transcript.Count);
        session.CurrentItem = _svc.Items.Generate(skill, rng);
        session.HintLevel = 0;
        session.AttemptsOnItem = 0;
        session.HintsUsedOnItem = 0;
        session.MetaphorUsedOnItem = false;
        session.ItemShownAt = DateTimeOffset.UtcNow;
        return announce
            ? $"Hello! Let's practise {CurriculumPack.Skill(skill).Title.ToLowerInvariant()}. {session.CurrentItem.Item.Prompt} Take your time and show me your thinking."
            : session.CurrentItem.Item.Prompt;
    }

    private SessionView View(SessionContext s, string message)
    {
        var item = s.CurrentItem!.Item;
        return new SessionView(s.SessionId, s.Pseudonym, item.SkillCode, CurriculumPack.Skill(item.SkillCode).Title, item.Prompt,
            message, s.Language, s.ItemsCompleted, _svc.Memory.Get(s.Pseudonym, item.SkillCode).PKnown);
    }
}
