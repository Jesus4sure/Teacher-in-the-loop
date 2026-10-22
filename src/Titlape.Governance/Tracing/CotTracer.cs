using System.Collections.Concurrent;
using System.Diagnostics;
using Titlape.CrossCutting;

namespace Titlape.Governance.Tracing;

/// <summary>One reasoning step of an agent, written in plain language for teachers.</summary>
public sealed record TraceStep(
    int Index,
    string Agent,
    string Step,
    string PlainLanguage,
    IReadOnlyDictionary<string, string> Data,
    double DurationMs,
    DateTimeOffset At);

/// <summary>Transparent Chain-of-Thought execution log for one tutoring turn (XAI, RAI principle 4).</summary>
public sealed class AgentTrace
{
    public required string TraceId { get; init; }
    public required string LearnerPseudonym { get; init; }
    public required string SessionId { get; init; }
    public string? SkillCode { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<TraceStep> Steps { get; } = new();
    public string? Outcome { get; set; }
    public double TotalMs { get; set; }
}

/// <summary>
/// Records agent reasoning both as OpenTelemetry spans (OpenInference attributes, exportable to Arize
/// Phoenix) and as a plain-language log the Teacher Dashboard renders. Only redacted text is recorded.
/// </summary>
public sealed class CotTracer
{
    private readonly ConcurrentDictionary<string, AgentTrace> _traces = new();
    private readonly ConcurrentQueue<string> _order = new();
    public int Capacity { get; init; } = 5000;

    public AgentTrace Begin(string learnerPseudonym, string sessionId)
    {
        var id = Activity.Current?.TraceId.ToString() is { Length: > 0 } t && !_traces.ContainsKey(t)
            ? t : ActivityTraceId.CreateRandom().ToString();
        var trace = new AgentTrace { TraceId = id, LearnerPseudonym = learnerPseudonym, SessionId = sessionId };
        _traces[id] = trace;
        _order.Enqueue(id);
        while (_order.Count > Capacity && _order.TryDequeue(out var old)) _traces.TryRemove(old, out _);
        return trace;
    }

    public TraceStep Step(AgentTrace trace, string agent, string step, string plainLanguage,
        IDictionary<string, string>? data = null, double durationMs = 0)
    {
        var s = new TraceStep(trace.Steps.Count + 1, agent, step, plainLanguage,
            new Dictionary<string, string>(data ?? new Dictionary<string, string>()), Math.Round(durationMs, 2), DateTimeOffset.UtcNow);
        lock (trace.Steps) trace.Steps.Add(s);
        Activity.Current?.AddEvent(new ActivityEvent($"{agent}.{step}", tags: new ActivityTagsCollection
        {
            [TitlapeTelemetry.AgentName] = agent,
            ["titlape.plain_language"] = plainLanguage
        }));
        return s;
    }

    public AgentTrace? Get(string traceId) => _traces.GetValueOrDefault(traceId);

    public IReadOnlyList<AgentTrace> Recent(int n = 50, Func<AgentTrace, bool>? filter = null) =>
        _traces.Values.Where(filter ?? (_ => true)).OrderByDescending(t => t.StartedAt).Take(n).ToList();
}
