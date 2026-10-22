using System.Text.Json;
using Titlape.Core;
using Titlape.CrossCutting.Communication;

namespace Titlape.Data.Sync;

/// <summary>
/// An offline-first embedded document store (RxDB/CouchDB role). One replica runs on each solar edge
/// node; the cloud runs another. All writes are local and immediate; replication is background delta-sync.
/// </summary>
public sealed class ReplicaStore
{
    private readonly Dictionary<string, CrdtDocument> _docs = new();
    private readonly object _gate = new();
    public HlcClock Clock { get; }
    public string NodeId => Clock.NodeId;

    public ReplicaStore(string nodeId, IClock? clock = null) => Clock = new HlcClock(nodeId, clock);

    public int Count { get { lock (_gate) return _docs.Count; } }

    public CrdtDocument? Get(string collection, string id)
    {
        lock (_gate) return _docs.GetValueOrDefault($"{collection}/{id}");
    }

    public IReadOnlyList<CrdtDocument> Query(string collection)
    {
        lock (_gate) return _docs.Values.Where(d => d.Collection == collection).ToList();
    }

    public void Put(string collection, string id, string field, string value)
    {
        lock (_gate)
        {
            var d = GetOrCreate(collection, id);
            var stamp = Clock.Now();
            d.Fields[field] = new LwwEntry(value, stamp);
            d.Touch("f:" + field, stamp);
        }
    }

    public void PutJson<T>(string collection, string id, string field, T value) =>
        Put(collection, id, field, JsonSerializer.Serialize(value));

    public void Delete(string collection, string id, string field)
    {
        lock (_gate)
        {
            var d = GetOrCreate(collection, id);
            var stamp = Clock.Now();
            d.Fields[field] = new LwwEntry("", stamp, Deleted: true);
            d.Touch("f:" + field, stamp);
        }
    }

    public void Increment(string collection, string id, string counter, long delta = 1)
    {
        lock (_gate)
        {
            var d = GetOrCreate(collection, id);
            if (!d.Counters.TryGetValue(counter, out var c)) d.Counters[counter] = c = new PnCounter();
            c.Add(NodeId, delta);
            d.Touch("c:" + counter, Clock.Now());
        }
    }

    public void AddToSet(string collection, string id, string set, string element)
    {
        lock (_gate)
        {
            var d = GetOrCreate(collection, id);
            if (!d.Sets.TryGetValue(set, out var s)) d.Sets[set] = s = new OrSet();
            var stamp = Clock.Now();
            s.Add(element, $"{NodeId}:{stamp.Millis}:{stamp.Counter}");
            d.Touch("s:" + set, stamp);
        }
    }

    public void RemoveFromSet(string collection, string id, string set, string element)
    {
        lock (_gate)
        {
            // Removing from a set this replica has never seen is a no-op (observed-remove semantics).
            if (_docs.TryGetValue($"{collection}/{id}", out var d) && d.Sets.TryGetValue(set, out var s))
            { s.Remove(element); d.Touch("s:" + set, Clock.Now()); }
        }
    }

    /// <summary>Changes made (or relayed) by this replica after the watermark.</summary>
    public (List<CrdtDocument> Docs, Hlc Watermark) DeltaSince(Hlc since, string? excludeOriginNode = null)
    {
        lock (_gate)
        {
            var list = _docs.Values.Select(d => d.DeltaSince(since, excludeOriginNode)).Where(d => d is not null).Cast<CrdtDocument>().ToList();
            var wm = _docs.Values.Select(d => d.LastLocalChange).DefaultIfEmpty(since).Max();
            return (list, wm > since ? wm : since);
        }
    }

    public MergeReport Apply(IEnumerable<CrdtDocument> delta)
    {
        lock (_gate)
        {
            var report = MergeReport.Empty;
            foreach (var incoming in delta)
            {
                foreach (var f in incoming.Fields.Values) Clock.Observe(f.Stamp);
                var local = GetOrCreate(incoming.Collection, incoming.Id);
                report += local.Merge(incoming, Clock.Now());
            }
            return report;
        }
    }

    /// <summary>Convergence check used in tests and RQ 4.2 evaluation.</summary>
    public string Fingerprint()
    {
        lock (_gate)
        {
            var parts = _docs.Values.Where(d => d.Fields.Count + d.Counters.Count + d.Sets.Count > 0).OrderBy(d => d.Key).Select(d =>
                d.Key + "|" + string.Join(",", d.Fields.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value.Value}{(f.Value.Deleted ? "~" : "")}")) +
                "|" + string.Join(",", d.Counters.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value.Value}")) +
                "|" + string.Join(",", d.Sets.OrderBy(s => s.Key).Select(s => $"{s.Key}=[{string.Join(";", s.Value.Elements.OrderBy(x => x))}]")));
            return string.Join("\n", parts);
        }
    }

    public ReplicaSnapshot Snapshot()
    {
        lock (_gate) return new ReplicaSnapshot(NodeId, _docs.Values.Select(d => SyncCodec.EncodeDoc(d, includeLocal: true)).ToList());
    }

    public void Restore(ReplicaSnapshot snapshot)
    {
        lock (_gate)
        {
            _docs.Clear();
            foreach (var b in snapshot.Documents)
            {
                var d = SyncCodec.DecodeDoc(b);
                _docs[d.Key] = d;
                foreach (var f in d.Fields.Values) Clock.Observe(f.Stamp);
            }
        }
    }

    private CrdtDocument GetOrCreate(string collection, string id)
    {
        var key = $"{collection}/{id}";
        if (!_docs.TryGetValue(key, out var d)) _docs[key] = d = new CrdtDocument { Collection = collection, Id = id };
        return d;
    }
}

public sealed record ReplicaSnapshot(string NodeId, List<byte[]> Documents);

/// <summary>Protobuf-wire encoding of CRDT deltas (compact binary transport, RQ 5.2).</summary>
public static class SyncCodec
{
    // message Delta { repeated Doc docs = 1; string from_node = 2; }
    // message Doc { string collection = 1; string id = 2; repeated Field fields = 3; repeated Counter counters = 4; repeated Set sets = 5; repeated Seen seen = 6; }
    // message Field { string name = 1; string value = 2; Stamp stamp = 3; bool deleted = 4; }
    // message Stamp { sint64 millis = 1; sint32 counter = 2; string node = 3; }
    // message Counter { string name = 1; repeated NodeCount p = 2; repeated NodeCount n = 3; }
    // message NodeCount { string node = 1; sint64 value = 2; }
    // message Set { string name = 1; repeated Element adds = 2; repeated string tombstones = 3; }
    // message Element { string value = 1; repeated string tags = 2; }

    public static byte[] EncodeDelta(string fromNode, IEnumerable<CrdtDocument> docs)
    {
        var w = new ProtoWriter();
        foreach (var d in docs) w.Bytes(1, EncodeDoc(d, includeLocal: false));
        w.String(2, fromNode);
        return w.ToArray();
    }

    public static (string FromNode, List<CrdtDocument> Docs) DecodeDelta(ReadOnlySpan<byte> data)
    {
        var r = new ProtoReader(data);
        var docs = new List<CrdtDocument>(); string from = "";
        while (r.TryReadTag(out var f, out var wt))
        {
            if (f == 1) docs.Add(DecodeDoc(r.ReadBytes()));
            else if (f == 2) from = r.ReadString();
            else r.Skip(wt);
        }
        return (from, docs);
    }

    public static byte[] EncodeDoc(CrdtDocument d, bool includeLocal)
    {
        var w = new ProtoWriter().String(1, d.Collection).String(2, d.Id);
        foreach (var (name, e) in d.Fields)
            w.Bytes(3, new ProtoWriter().String(1, name).String(2, e.Value).Bytes(3, Stamp(e.Stamp)).Bool(4, e.Deleted).ToArray());
        foreach (var (name, c) in d.Counters)
        {
            var cw = new ProtoWriter().String(1, name);
            foreach (var (n, v) in c.P) cw.Bytes(2, new ProtoWriter().String(1, n).Int64(2, v).ToArray());
            foreach (var (n, v) in c.N) cw.Bytes(3, new ProtoWriter().String(1, n).Int64(2, v).ToArray());
            w.Bytes(4, cw.ToArray());
        }
        foreach (var (name, s) in d.Sets)
        {
            var sw = new ProtoWriter().String(1, name);
            foreach (var (el, tags) in s.Adds)
            {
                var ew = new ProtoWriter().String(1, el);
                foreach (var t in tags) ew.String(2, t);
                sw.Bytes(2, ew.ToArray());
            }
            foreach (var t in s.Tombstones) sw.String(3, t);
            w.Bytes(5, sw.ToArray());
        }
        if (includeLocal)
            foreach (var (k, h) in d.LocalSeen) w.Bytes(6, new ProtoWriter().String(1, k).Bytes(2, Stamp(h)).ToArray());
        return w.ToArray();
    }

    public static CrdtDocument DecodeDoc(ReadOnlySpan<byte> data)
    {
        var r = new ProtoReader(data);
        string coll = "", id = "";
        var fields = new Dictionary<string, LwwEntry>();
        var counters = new Dictionary<string, PnCounter>();
        var sets = new Dictionary<string, OrSet>();
        var seen = new Dictionary<string, Hlc>();
        while (r.TryReadTag(out var f, out var wt))
        {
            switch (f)
            {
                case 1: coll = r.ReadString(); break;
                case 2: id = r.ReadString(); break;
                case 3:
                {
                    var fr = new ProtoReader(r.ReadBytes());
                    string name = "", value = ""; Hlc st = Hlc.Zero; bool del = false;
                    while (fr.TryReadTag(out var ff, out var fwt))
                    {
                        if (ff == 1) name = fr.ReadString();
                        else if (ff == 2) value = fr.ReadString();
                        else if (ff == 3) st = ReadStamp(fr.ReadBytes());
                        else if (ff == 4) del = fr.ReadBool();
                        else fr.Skip(fwt);
                    }
                    fields[name] = new LwwEntry(value, st, del);
                    break;
                }
                case 4:
                {
                    var cr = new ProtoReader(r.ReadBytes());
                    string name = ""; var c = new PnCounter();
                    while (cr.TryReadTag(out var cf, out var cwt))
                    {
                        if (cf == 1) name = cr.ReadString();
                        else if (cf is 2 or 3)
                        {
                            var (n, v) = ReadNodeCount(cr.ReadBytes());
                            (cf == 2 ? c.P : c.N)[n] = v;
                        }
                        else cr.Skip(cwt);
                    }
                    counters[name] = c;
                    break;
                }
                case 5:
                {
                    var sr = new ProtoReader(r.ReadBytes());
                    string name = ""; var s = new OrSet();
                    while (sr.TryReadTag(out var sf, out var swt))
                    {
                        if (sf == 1) name = sr.ReadString();
                        else if (sf == 2)
                        {
                            var er = new ProtoReader(sr.ReadBytes());
                            string el = ""; var tags = new List<string>();
                            while (er.TryReadTag(out var ef, out var ewt))
                            {
                                if (ef == 1) el = er.ReadString();
                                else if (ef == 2) tags.Add(er.ReadString());
                                else er.Skip(ewt);
                            }
                            foreach (var t in tags) s.Add(el, t);
                        }
                        else if (sf == 3) s.Tombstones.Add(sr.ReadString());
                        else sr.Skip(swt);
                    }
                    sets[name] = s;
                    break;
                }
                case 6:
                {
                    var lr = new ProtoReader(r.ReadBytes());
                    string k = ""; Hlc h = Hlc.Zero;
                    while (lr.TryReadTag(out var lf, out var lwt))
                    {
                        if (lf == 1) k = lr.ReadString();
                        else if (lf == 2) h = ReadStamp(lr.ReadBytes());
                        else lr.Skip(lwt);
                    }
                    seen[k] = h;
                    break;
                }
                default: r.Skip(wt); break;
            }
        }
        return new CrdtDocument { Collection = coll, Id = id, Fields = fields, Counters = counters, Sets = sets, LocalSeen = seen };
    }

    /// <summary>Equivalent REST/JSON payload, used as the baseline for payload-reduction measurements.</summary>
    public static byte[] EncodeDeltaAsJson(string fromNode, IEnumerable<CrdtDocument> docs) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            fromNode,
            docs = docs.Select(d => new
            {
                collection = d.Collection,
                id = d.Id,
                fields = d.Fields.ToDictionary(k => k.Key, v => new
                {
                    value = v.Value.Value,
                    stamp = new { millis = v.Value.Stamp.Millis, counter = v.Value.Stamp.Counter, node = v.Value.Stamp.Node },
                    deleted = v.Value.Deleted
                }),
                counters = d.Counters.ToDictionary(k => k.Key, v => new { p = v.Value.P, n = v.Value.N }),
                sets = d.Sets.ToDictionary(k => k.Key, v => new { adds = v.Value.Adds, tombstones = v.Value.Tombstones })
            })
        });

    private static byte[] Stamp(Hlc h) => new ProtoWriter().Int64(1, h.Millis).Int32(2, h.Counter).String(3, h.Node).ToArray();

    private static Hlc ReadStamp(ReadOnlySpan<byte> b)
    {
        var r = new ProtoReader(b); long m = 0; int c = 0; string n = "";
        while (r.TryReadTag(out var f, out var wt))
        {
            if (f == 1) m = r.ReadSInt64(); else if (f == 2) c = r.ReadSInt32(); else if (f == 3) n = r.ReadString(); else r.Skip(wt);
        }
        return new Hlc(m, c, n);
    }

    private static (string, long) ReadNodeCount(ReadOnlySpan<byte> b)
    {
        var r = new ProtoReader(b); string n = ""; long v = 0;
        while (r.TryReadTag(out var f, out var wt))
        {
            if (f == 1) n = r.ReadString(); else if (f == 2) v = r.ReadSInt64(); else r.Skip(wt);
        }
        return (n, v);
    }
}
