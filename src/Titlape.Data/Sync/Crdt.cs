using Titlape.Core;

namespace Titlape.Data.Sync;

/// <summary>
/// Hybrid Logical Clock timestamp: physical millis + logical counter + node id tie-breaker.
/// Gives a total order that tolerates clock skew between solar edge nodes that may be offline for days.
/// </summary>
public readonly record struct Hlc(long Millis, int Counter, string Node) : IComparable<Hlc>
{
    public static readonly Hlc Zero = new(0, 0, "");

    public int CompareTo(Hlc other)
    {
        var c = Millis.CompareTo(other.Millis);
        if (c != 0) return c;
        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(Node, other.Node);
    }

    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;
    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;
    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Millis}:{Counter}:{Node}";
}

public sealed class HlcClock
{
    private readonly IClock _clock;
    private readonly object _gate = new();
    private Hlc _last;
    public string NodeId { get; }

    public HlcClock(string nodeId, IClock? clock = null)
    {
        NodeId = nodeId;
        _clock = clock ?? SystemClock.Instance;
        _last = new Hlc(0, 0, nodeId);
    }

    public Hlc Now()
    {
        lock (_gate)
        {
            var pt = _clock.UtcNow.ToUnixTimeMilliseconds();
            _last = pt > _last.Millis ? new Hlc(pt, 0, NodeId) : new Hlc(_last.Millis, _last.Counter + 1, NodeId);
            return _last;
        }
    }

    /// <summary>Advance on receipt of a remote timestamp (HLC receive rule).</summary>
    public void Observe(Hlc remote)
    {
        lock (_gate)
        {
            var pt = _clock.UtcNow.ToUnixTimeMilliseconds();
            var max = Math.Max(pt, Math.Max(_last.Millis, remote.Millis));
            int counter;
            if (max == _last.Millis && max == remote.Millis) counter = Math.Max(_last.Counter, remote.Counter) + 1;
            else if (max == _last.Millis) counter = _last.Counter + 1;
            else if (max == remote.Millis) counter = remote.Counter + 1;
            else counter = 0;
            _last = new Hlc(max, counter, NodeId);
        }
    }
}

/// <summary>Last-Writer-Wins register entry.</summary>
public sealed record LwwEntry(string Value, Hlc Stamp, bool Deleted = false);

/// <summary>Positive-Negative counter (per-node increments; merge = per-node max).</summary>
public sealed class PnCounter
{
    public Dictionary<string, long> P { get; init; } = new();
    public Dictionary<string, long> N { get; init; } = new();
    public long Value => P.Values.Sum() - N.Values.Sum();

    public void Add(string node, long delta)
    {
        var d = delta >= 0 ? P : N;
        d[node] = d.GetValueOrDefault(node) + Math.Abs(delta);
    }

    public void Merge(PnCounter other)
    {
        foreach (var (k, v) in other.P) P[k] = Math.Max(P.GetValueOrDefault(k), v);
        foreach (var (k, v) in other.N) N[k] = Math.Max(N.GetValueOrDefault(k), v);
    }

    public PnCounter Clone() => new() { P = new(P), N = new(N) };
}

/// <summary>Observed-Remove set (add-wins). Each add carries a unique tag; removes tombstone observed tags.</summary>
public sealed class OrSet
{
    public Dictionary<string, HashSet<string>> Adds { get; init; } = new();
    public HashSet<string> Tombstones { get; init; } = new();

    public IEnumerable<string> Elements =>
        Adds.Where(kv => kv.Value.Any(t => !Tombstones.Contains(t))).Select(kv => kv.Key);

    public bool Contains(string e) => Adds.TryGetValue(e, out var tags) && tags.Any(t => !Tombstones.Contains(t));

    public void Add(string element, string uniqueTag)
    {
        if (!Adds.TryGetValue(element, out var tags)) Adds[element] = tags = new();
        tags.Add(uniqueTag);
    }

    public void Remove(string element)
    {
        if (Adds.TryGetValue(element, out var tags)) Tombstones.UnionWith(tags);
    }

    public void Merge(OrSet other)
    {
        foreach (var (e, tags) in other.Adds)
        {
            if (!Adds.TryGetValue(e, out var mine)) Adds[e] = mine = new();
            mine.UnionWith(tags);
        }
        Tombstones.UnionWith(other.Tombstones);
    }

    public OrSet Clone() => new()
    {
        Adds = Adds.ToDictionary(k => k.Key, v => new HashSet<string>(v.Value)),
        Tombstones = new(Tombstones)
    };
}

public sealed record MergeReport(int FieldsUpdated, int ConcurrentConflicts, int CountersMerged, int SetsMerged)
{
    public static MergeReport operator +(MergeReport a, MergeReport b) =>
        new(a.FieldsUpdated + b.FieldsUpdated, a.ConcurrentConflicts + b.ConcurrentConflicts,
            a.CountersMerged + b.CountersMerged, a.SetsMerged + b.SetsMerged);

    public static readonly MergeReport Empty = new(0, 0, 0, 0);
}

/// <summary>
/// A JSON-like document composed of CRDTs (Automerge-style): LWW fields, PN-counters and OR-sets.
/// Merge is commutative, associative and idempotent, so replicas converge regardless of sync order.
/// <see cref="LocalSeen"/> records, per element, the local HLC at which this replica last changed it,
/// enabling minimal delta-sync (and relaying of other nodes' changes via the cloud).
/// </summary>
public sealed class CrdtDocument
{
    public required string Collection { get; init; }
    public required string Id { get; init; }
    public Dictionary<string, LwwEntry> Fields { get; init; } = new();
    public Dictionary<string, PnCounter> Counters { get; init; } = new();
    public Dictionary<string, OrSet> Sets { get; init; } = new();
    public Dictionary<string, Hlc> LocalSeen { get; init; } = new();

    public string Key => $"{Collection}/{Id}";

    public string? Get(string field) =>
        Fields.TryGetValue(field, out var e) && !e.Deleted ? e.Value : null;

    public long Counter(string name) => Counters.TryGetValue(name, out var c) ? c.Value : 0;

    public IEnumerable<string> Set(string name) => Sets.TryGetValue(name, out var s) ? s.Elements : Enumerable.Empty<string>();

    public Hlc LastLocalChange => LocalSeen.Values.DefaultIfEmpty(Hlc.Zero).Max();

    internal void Touch(string elementKey, Hlc localStamp) => LocalSeen[elementKey] = localStamp;

    public MergeReport Merge(CrdtDocument other, Hlc localNow)
    {
        int updated = 0, conflicts = 0, counters = 0, sets = 0;
        foreach (var (k, theirs) in other.Fields)
        {
            if (!Fields.TryGetValue(k, out var mine))
            {
                Fields[k] = theirs; Touch("f:" + k, localNow); updated++; continue;
            }
            if (mine.Stamp.CompareTo(theirs.Stamp) == 0) continue;
            // Concurrent writes from different nodes with different values = a resolved conflict.
            if (mine.Stamp.Node != theirs.Stamp.Node && mine.Value != theirs.Value) conflicts++;
            if (theirs.Stamp > mine.Stamp) { Fields[k] = theirs; Touch("f:" + k, localNow); updated++; }
        }
        foreach (var (k, c) in other.Counters)
        {
            if (!Counters.TryGetValue(k, out var mine)) Counters[k] = mine = new PnCounter();
            var before = mine.Clone();
            mine.Merge(c);
            if (!SameCounter(before, mine)) { Touch("c:" + k, localNow); counters++; }
        }
        foreach (var (k, s) in other.Sets)
        {
            if (!Sets.TryGetValue(k, out var mine)) Sets[k] = mine = new OrSet();
            int beforeAdds = mine.Adds.Sum(a => a.Value.Count), beforeTomb = mine.Tombstones.Count;
            mine.Merge(s);
            if (mine.Adds.Sum(a => a.Value.Count) != beforeAdds || mine.Tombstones.Count != beforeTomb)
            { Touch("s:" + k, localNow); sets++; }
        }
        return new MergeReport(updated, conflicts, counters, sets);
    }

    /// <summary>
    /// Delta containing only elements this replica changed after <paramref name="since"/>. Fields that
    /// originated on <paramref name="excludeOriginNode"/> are skipped (the peer already has them).
    /// </summary>
    public CrdtDocument? DeltaSince(Hlc since, string? excludeOriginNode = null)
    {
        bool Changed(string key) => LocalSeen.TryGetValue(key, out var h) && h > since;
        var fields = Fields.Where(f => Changed("f:" + f.Key) && f.Value.Stamp.Node != excludeOriginNode).ToDictionary(k => k.Key, v => v.Value);
        var counters = Counters.Where(c => Changed("c:" + c.Key)).ToDictionary(k => k.Key, v => v.Value.Clone());
        var sets = Sets.Where(s => Changed("s:" + s.Key)).ToDictionary(k => k.Key, v => v.Value.Clone());
        if (fields.Count == 0 && counters.Count == 0 && sets.Count == 0) return null;
        return new CrdtDocument { Collection = Collection, Id = Id, Fields = fields, Counters = counters, Sets = sets };
    }

    private static bool SameCounter(PnCounter a, PnCounter b) =>
        a.P.Count == b.P.Count && a.N.Count == b.N.Count &&
        a.P.All(kv => b.P.GetValueOrDefault(kv.Key) == kv.Value) &&
        a.N.All(kv => b.N.GetValueOrDefault(kv.Key) == kv.Value);
}
