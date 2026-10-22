using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Titlape.Governance.Audit;

public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset At,
    string Actor,
    string Action,
    string Resource,
    string Detail,
    string PreviousHash,
    string Hash);

public sealed record AuditVerification(bool Intact, long Entries, long? FirstBrokenSequence, string Detail);

/// <summary>
/// Tamper-evident audit log (Layer 3 security pillar). Each entry is chained to the previous one with
/// an HMAC-SHA256, so editing, deleting or reordering any record breaks verification from that point.
/// Records every teacher decision, agent proposal, guardrail block and data-access denial.
/// </summary>
public sealed class AuditLog
{
    private readonly byte[] _key;
    private readonly List<AuditEntry> _entries = new();
    private readonly object _gate = new();
    private readonly string? _path;
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public AuditLog(byte[] hmacKey, string? appendOnlyFile = null)
    {
        _key = hmacKey;
        _path = appendOnlyFile;
        if (_path is not null && File.Exists(_path))
            foreach (var line in File.ReadLines(_path).Where(l => l.Length > 0))
                _entries.Add(JsonSerializer.Deserialize<AuditEntry>(line)!);
    }

    public AuditEntry Append(string actor, string action, string resource, string detail)
    {
        lock (_gate)
        {
            var prev = _entries.Count == 0 ? Genesis : _entries[^1].Hash;
            var seq = _entries.Count + 1L;
            var at = DateTimeOffset.UtcNow;
            var entry = new AuditEntry(seq, at, actor, action, resource, detail, prev, ComputeHash(seq, at, actor, action, resource, detail, prev));
            _entries.Add(entry);
            if (_path is not null) File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n");
            return entry;
        }
    }

    public IReadOnlyList<AuditEntry> Entries(int take = 200)
    {
        lock (_gate) return _entries.AsEnumerable().Reverse().Take(take).ToList();
    }

    public long Count { get { lock (_gate) return _entries.Count; } }

    public AuditVerification Verify()
    {
        lock (_gate)
        {
            var prev = Genesis;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Sequence != i + 1)
                    return new(false, _entries.Count, e.Sequence, $"sequence gap at position {i + 1}");
                if (e.PreviousHash != prev)
                    return new(false, _entries.Count, e.Sequence, "chain link broken (entry removed or reordered)");
                var expected = ComputeHash(e.Sequence, e.At, e.Actor, e.Action, e.Resource, e.Detail, e.PreviousHash);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(e.Hash)))
                    return new(false, _entries.Count, e.Sequence, "entry contents were modified");
                prev = e.Hash;
            }
            return new(true, _entries.Count, null, "all entries verified");
        }
    }

    /// <summary>Test hook: simulates an attacker editing a stored record in place.</summary>
    internal void TamperForTest(int index, string newDetail)
    {
        lock (_gate) _entries[index] = _entries[index] with { Detail = newDetail };
    }

    private string ComputeHash(long seq, DateTimeOffset at, string actor, string action, string resource, string detail, string prev)
    {
        var payload = $"{seq}|{at.ToUnixTimeMilliseconds()}|{actor}|{action}|{resource}|{detail}|{prev}";
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
