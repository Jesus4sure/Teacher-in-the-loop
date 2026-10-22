using System.Diagnostics;
using Titlape.CrossCutting;

namespace Titlape.Agents.Graph;

public interface ICheckpointable
{
    /// <summary>Compact, redacted view of state saved after each node for teacher inspection.</summary>
    IReadOnlyDictionary<string, string> Snapshot();
}

public sealed record Checkpoint(int Step, string Node, string? NextNode, DateTimeOffset At, double DurationMs, IReadOnlyDictionary<string, string> State);

/// <summary>
/// Minimal stateful agent graph (the LangGraph pattern): named async nodes, static and conditional
/// edges, a step budget to stop runaway loops, and a checkpoint after every node so teachers can inspect
/// exactly how a tutoring decision was reached.
/// </summary>
public sealed class StateGraph<TState> where TState : ICheckpointable
{
    public const string End = "__end__";
    private readonly Dictionary<string, Func<TState, CancellationToken, Task>> _nodes = new();
    private readonly Dictionary<string, Func<TState, string>> _edges = new();
    public string? EntryPoint { get; private set; }
    public int MaxSteps { get; init; } = 25;

    public StateGraph<TState> AddNode(string name, Func<TState, CancellationToken, Task> fn)
    {
        if (name == End) throw new ArgumentException("reserved node name");
        _nodes[name] = fn; return this;
    }

    public StateGraph<TState> AddEdge(string from, string to) { _edges[from] = _ => to; return this; }

    public StateGraph<TState> AddConditionalEdge(string from, Func<TState, string> router) { _edges[from] = router; return this; }

    public StateGraph<TState> SetEntryPoint(string node) { EntryPoint = node; return this; }

    public IReadOnlyCollection<string> Nodes => _nodes.Keys;

    public void Validate()
    {
        if (EntryPoint is null || !_nodes.ContainsKey(EntryPoint)) throw new InvalidOperationException("Entry point missing.");
        foreach (var n in _nodes.Keys)
            if (!_edges.ContainsKey(n)) throw new InvalidOperationException($"Node '{n}' has no outgoing edge.");
    }

    public async Task<IReadOnlyList<Checkpoint>> RunAsync(TState state, CancellationToken ct = default)
    {
        Validate();
        var checkpoints = new List<Checkpoint>();
        var current = EntryPoint!;
        for (int step = 1; current != End; step++)
        {
            if (step > MaxSteps) throw new InvalidOperationException($"Agent graph exceeded {MaxSteps} steps (possible reflection loop).");
            ct.ThrowIfCancellationRequested();
            using var act = TitlapeTelemetry.Source.StartActivity($"agent.{current}");
            act?.SetTag(TitlapeTelemetry.SpanKind, "AGENT");
            act?.SetTag(TitlapeTelemetry.AgentName, current);
            var sw = Stopwatch.StartNew();
            await _nodes[current](state, ct).ConfigureAwait(false);
            sw.Stop();
            var next = _edges[current](state);
            if (next != End && !_nodes.ContainsKey(next)) throw new InvalidOperationException($"Unknown node '{next}'.");
            checkpoints.Add(new Checkpoint(step, current, next, DateTimeOffset.UtcNow, sw.Elapsed.TotalMilliseconds, state.Snapshot()));
            current = next;
        }
        return checkpoints;
    }
}
