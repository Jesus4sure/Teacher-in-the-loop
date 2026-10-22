namespace Titlape.Data.Vector;

public sealed record VectorRecord(string Id, float[] Vector, string Text, IReadOnlyDictionary<string, string> Metadata);

public sealed record VectorHit(VectorRecord Record, double Score);

/// <summary>
/// Edge vector index (pgvector role on the cloud; in-memory on solar edge nodes). Vectors are
/// L2-normalised at insert so cosine similarity is a dot product. Only PII-redacted text is accepted.
/// </summary>
public sealed class VectorStore
{
    private readonly List<VectorRecord> _records = new();
    private readonly ReaderWriterLockSlim _lock = new();

    public int Count { get { _lock.EnterReadLock(); try { return _records.Count; } finally { _lock.ExitReadLock(); } } }

    public void Upsert(VectorRecord record)
    {
        var normalized = record with { Vector = Normalize(record.Vector) };
        _lock.EnterWriteLock();
        try
        {
            var idx = _records.FindIndex(r => r.Id == record.Id);
            if (idx >= 0) _records[idx] = normalized; else _records.Add(normalized);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public IReadOnlyList<VectorHit> Search(float[] query, int k, Func<VectorRecord, bool>? filter = null)
    {
        var q = Normalize(query);
        _lock.EnterReadLock();
        try
        {
            return _records.Where(r => filter?.Invoke(r) ?? true)
                .Select(r => new VectorHit(r, Dot(q, r.Vector)))
                .OrderByDescending(h => h.Score)
                .Take(k)
                .ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<VectorRecord> All()
    {
        _lock.EnterReadLock();
        try { return _records.ToList(); } finally { _lock.ExitReadLock(); }
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try { _records.Clear(); } finally { _lock.ExitWriteLock(); }
    }

    private static float[] Normalize(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n == 0) return v.ToArray();
        return v.Select(x => (float)(x / n)).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }
}
