using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Titlape.Core;

namespace Titlape.CrossCutting.Security;

/// <summary>
/// Authenticated identity (OIDC-style claims). Every call across every layer carries one —
/// Zero Trust: no implicit trust based on network location.
/// </summary>
public sealed record Principal(
    string Subject,
    Role Role,
    string SchoolId,
    IReadOnlyList<string> ClassIds,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt)
{
    public bool HasScope(string scope) => Scopes.Contains(scope) || Scopes.Contains("*");

    public static Principal SystemAgent(string agentName, string schoolId) =>
        new($"agent:{agentName}", Role.Agent, schoolId, Array.Empty<string>(), new[] { "agent" }, DateTimeOffset.MaxValue);
}

/// <summary>
/// Issues and validates compact signed tokens (JWT-compatible structure, HMAC-SHA256).
/// In production this is replaced by an OAuth2/OIDC provider; the validation contract is identical.
/// </summary>
public sealed class TokenService
{
    private readonly byte[] _key;
    private readonly IClock _clock;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TokenService(byte[] signingKey, IClock? clock = null)
    {
        if (signingKey.Length < 32) throw new ArgumentException("Signing key must be at least 256 bits.");
        _key = signingKey;
        _clock = clock ?? SystemClock.Instance;
    }

    public string Issue(string subject, Role role, string schoolId, IEnumerable<string>? classIds = null,
        IEnumerable<string>? scopes = null, TimeSpan? lifetime = null)
    {
        var claims = new TokenClaims(subject, role.ToString(), schoolId,
            (classIds ?? Array.Empty<string>()).ToArray(),
            (scopes ?? DefaultScopes(role)).ToArray(),
            _clock.UtcNow.Add(lifetime ?? TimeSpan.FromHours(8)).ToUnixTimeSeconds());
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(claims, Json));
        var sig = B64(Sign($"{header}.{body}"));
        return $"{header}.{body}.{sig}";
    }

    public Result<Principal> Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return Result<Principal>.Fail("missing token");
        var parts = token.Split('.');
        if (parts.Length != 3) return Result<Principal>.Fail("malformed token");
        var expected = Sign($"{parts[0]}.{parts[1]}");
        byte[] actual;
        try { actual = UnB64(parts[2]); } catch { return Result<Principal>.Fail("malformed signature"); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            return Result<Principal>.Fail("invalid signature");
        TokenClaims? c;
        try { c = JsonSerializer.Deserialize<TokenClaims>(UnB64(parts[1]), Json); }
        catch { return Result<Principal>.Fail("malformed claims"); }
        if (c is null) return Result<Principal>.Fail("malformed claims");
        var exp = DateTimeOffset.FromUnixTimeSeconds(c.Exp);
        if (exp < _clock.UtcNow) return Result<Principal>.Fail("token expired");
        if (!Enum.TryParse<Role>(c.Role, out var role)) return Result<Principal>.Fail("unknown role");
        return Result<Principal>.Success(new Principal(c.Sub, role, c.School, c.Classes, c.Scopes, exp));
    }

    private static IEnumerable<string> DefaultScopes(Role role) => role switch
    {
        Role.Student => new[] { "tutor:session", "self:read" },
        Role.Teacher => new[] { "class:read", "class:decide", "trace:read" },
        Role.Admin => new[] { "*" },
        Role.Guardian => new[] { "consent:write", "child:read" },
        _ => new[] { "agent" }
    };

    private byte[] Sign(string data) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data));
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private sealed record TokenClaims(string Sub, string Role, string School, string[] Classes, string[] Scopes, long Exp);
}

/// <summary>Attributes describing the resource an action targets (for ABAC).</summary>
public sealed record ResourceAttributes(
    string Kind,
    string SchoolId,
    string? ClassId = null,
    string? StudentId = null,
    bool ContainsPii = false);

public sealed record AccessDecision(bool Allowed, string Reason);

/// <summary>
/// Attribute-Based Access Control with strict tenant (school) isolation — Layer 4 security
/// in the refactored matrix, applied at every service boundary.
/// </summary>
public sealed class AbacPolicyEngine
{
    public AccessDecision Evaluate(Principal p, string action, ResourceAttributes r)
    {
        if (p.Role == Role.Admin && p.HasScope("*"))
            return new(true, "admin");

        // Tenant isolation: never cross school boundaries.
        if (!string.Equals(p.SchoolId, r.SchoolId, StringComparison.Ordinal))
            return new(false, "tenant isolation: principal school does not match resource school");

        // Raw PII is only ever visible to the student's own teacher, never to agents.
        if (r.ContainsPii && p.Role is Role.Agent)
            return new(false, "agents may not access raw PII");

        return p.Role switch
        {
            Role.Teacher when r.ClassId is not null && !p.ClassIds.Contains(r.ClassId)
                => new(false, "teacher is not assigned to this class"),
            Role.Teacher when action.StartsWith("decide", StringComparison.Ordinal) && !p.HasScope("class:decide")
                => new(false, "missing class:decide scope"),
            Role.Teacher => new(true, "teacher of class"),
            Role.Student when r.StudentId is not null && r.StudentId != p.Subject
                => new(false, "students may only access their own data"),
            Role.Student when action is "tutor" && !p.HasScope("tutor:session")
                => new(false, "missing tutor:session scope"),
            Role.Student => new(true, "own data"),
            Role.Guardian when r.StudentId is not null && p.ClassIds.Contains(r.StudentId)
                => new(true, "guardian of student"),
            Role.Agent => new(true, "scope-bounded agent within tenant"),
            _ => new(false, "no matching policy")
        };
    }
}

/// <summary>
/// Scope-bounded agent execution (Layer 5 security): each agent may only invoke the tools it is
/// explicitly granted. Denials are surfaced, never silently ignored.
/// </summary>
public sealed class AgentToolPolicy
{
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.OrdinalIgnoreCase);

    public AgentToolPolicy Grant(string agent, params string[] tools)
    {
        if (!_grants.TryGetValue(agent, out var set)) _grants[agent] = set = new(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools) set.Add(t);
        return this;
    }

    public bool IsAllowed(string agent, string tool) =>
        _grants.TryGetValue(agent, out var set) && set.Contains(tool);

    public void Demand(string agent, string tool)
    {
        if (!IsAllowed(agent, tool))
            throw new UnauthorizedAccessException($"Agent '{agent}' is not permitted to use tool '{tool}'.");
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Snapshot() =>
        _grants.ToDictionary(k => k.Key, v => (IReadOnlyCollection<string>)v.Value.ToArray());

    public static AgentToolPolicy Default() => new AgentToolPolicy()
        .Grant("diagnostic", "student_memory.read", "student_memory.write", "item_bank.read", "curriculum.search")
        .Grant("socratic", "curriculum.search", "item_bank.read", "llm.generate")
        .Grant("localization", "glossary.read", "llm.generate")
        .Grant("safety", "guardrails.check", "curriculum.search")
        .Grant("orchestrator", "alerts.write", "pathway.propose", "student_memory.read");
}

/// <summary>
/// Guardian consent records (Human Consent Controls, Layer 6 security). Consent is purpose-specific:
/// <c>tutoring</c> is required to start a session; <c>transcript</c> (optional) allows redacted dialogue
/// excerpts to be retained for teacher review. Without it, dialogue is zero-retention.
/// </summary>
public sealed class ConsentRegistry
{
    public const string Tutoring = "tutoring";
    public const string Transcript = "transcript";
    private readonly Dictionary<string, ConsentRecord> _records = new();
    private readonly object _gate = new();

    public void Record(string studentId, string guardianRef, bool granted, string purpose)
    {
        lock (_gate) _records[$"{studentId}|{purpose}"] = new ConsentRecord(studentId, guardianRef, granted, purpose, DateTimeOffset.UtcNow);
    }

    public bool HasConsent(string studentId, string purpose = Tutoring)
    {
        lock (_gate) return _records.TryGetValue($"{studentId}|{purpose}", out var r) && r.Granted;
    }

    public IReadOnlyList<ConsentRecord> For(string studentId)
    {
        lock (_gate) return _records.Values.Where(r => r.StudentId == studentId).ToList();
    }
}

public sealed record ConsentRecord(string StudentId, string GuardianRef, bool Granted, string Purpose, DateTimeOffset At);
