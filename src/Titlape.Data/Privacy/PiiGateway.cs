using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Titlape.Data.Privacy;

public sealed record PiiEntity(string Type, int Start, int Length, string Replacement, double Confidence);

public sealed record RedactionResult(string Text, IReadOnlyList<PiiEntity> Entities)
{
    public bool HadPii => Entities.Count > 0;
}

/// <summary>A pluggable recognizer, mirroring Microsoft Presidio's recognizer registry.</summary>
public interface IPiiRecognizer
{
    string EntityType { get; }
    IEnumerable<(int Start, int Length, double Confidence)> Analyze(string text);
}

public sealed class RegexRecognizer : IPiiRecognizer
{
    private readonly Regex _regex;
    private readonly double _confidence;
    private readonly Func<Match, bool>? _validator;
    private readonly int _group;

    public RegexRecognizer(string type, string pattern, double confidence, Func<Match, bool>? validator = null,
        RegexOptions options = RegexOptions.None, int group = 0)
    {
        EntityType = type;
        _regex = new Regex(pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        _confidence = confidence;
        _validator = validator;
        _group = group;
    }

    public string EntityType { get; }

    public IEnumerable<(int, int, double)> Analyze(string text)
    {
        foreach (Match m in _regex.Matches(text))
        {
            if (_validator is not null && !_validator(m)) continue;
            var g = m.Groups[_group];
            yield return (g.Index, g.Length, _confidence);
        }
    }
}

/// <summary>Deny-list recognizer seeded from the class roster (names known to the edge node).</summary>
public sealed class RosterNameRecognizer : IPiiRecognizer
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    public string EntityType => "PERSON";

    public void AddName(string fullName)
    {
        foreach (var part in fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part.Length >= 3) _names.Add(part);
    }

    public IEnumerable<(int, int, double)> Analyze(string text)
    {
        foreach (Match m in Regex.Matches(text, @"\p{L}[\p{L}\p{M}'’-]+"))
            if (_names.Contains(m.Value)) yield return (m.Index, m.Length, 0.95);
    }
}

/// <summary>
/// Layer 1 PII gateway (Presidio-equivalent): analyzes and anonymizes minors' PII before any text is
/// vector-indexed, logged, or transmitted to the cloud. Patterns cover NDPA (Nigeria), POPIA
/// (South Africa) and East African identifiers.
/// </summary>
public sealed class PiiGateway
{
    private readonly List<IPiiRecognizer> _recognizers = new();
    public RosterNameRecognizer Roster { get; } = new();
    public double ScoreThreshold { get; set; } = 0.5;

    public PiiGateway()
    {
        _recognizers.Add(Roster);
        _recognizers.Add(new RegexRecognizer("EMAIL_ADDRESS", @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", 0.95));
        // Nigerian (+234 / 0[789]0x), South African (+27), Kenyan (+254), Ethiopian (+251) mobile numbers
        _recognizers.Add(new RegexRecognizer("PHONE_NUMBER",
            @"(?<!\d)(?:\+?(?:234|27|254|251|255|256|233)[\s-]?\d{2,3}[\s-]?\d{3}[\s-]?\d{3,4}|0[789][01]\d[\s-]?\d{3}[\s-]?\d{4})(?!\d)", 0.9));
        // South African ID (13 digits, YYMMDD prefix) — validated with the Luhn checksum
        _recognizers.Add(new RegexRecognizer("ZA_ID_NUMBER", @"(?<!\d)\d{13}(?!\d)", 0.85, m => Luhn(m.Value)));
        // Nigerian NIN (11 digits) / BVN
        _recognizers.Add(new RegexRecognizer("NG_NIN", @"(?<!\d)\d{11}(?!\d)", 0.7));
        _recognizers.Add(new RegexRecognizer("DATE_OF_BIRTH",
            @"\b(?:born|dob|birthday|date of birth)\b[^0-9]{0,20}(\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}|\d{1,2}\s+\p{L}+\s+\d{4})",
            0.85, options: RegexOptions.IgnoreCase, group: 1));
        _recognizers.Add(new RegexRecognizer("LOCATION",
            @"\b\d{1,5}\s+(?:[A-Z][a-z]+\s){1,3}(?:Street|St|Road|Rd|Avenue|Ave|Close|Crescent|Way|Lane)\b", 0.8));
        // "my name is X" / "I am called X" / "jina langu ni X" (Swahili) / "orúkọ mi ni X" (Yoruba)
        _recognizers.Add(new RegexRecognizer("PERSON",
            @"(?:my name is|i am called|call me|jina langu ni|orúkọ mi ni|oruko mi ni|sunana)\s+(\p{Lu}[\p{L}'’-]+(?:\s+\p{Lu}[\p{L}'’-]+)?)",
            0.8, options: RegexOptions.IgnoreCase, group: 1));
    }

    public PiiGateway AddRecognizer(IPiiRecognizer r) { _recognizers.Add(r); return this; }

    public IReadOnlyList<PiiEntity> Analyze(string text)
    {
        var found = new List<PiiEntity>();
        foreach (var r in _recognizers)
            foreach (var (s, l, c) in r.Analyze(text))
                if (c >= ScoreThreshold && l > 0)
                    found.Add(new PiiEntity(r.EntityType, s, l, $"<{r.EntityType}>", c));

        // Resolve overlaps: keep highest confidence, then longest span.
        var ordered = found.OrderByDescending(e => e.Confidence).ThenByDescending(e => e.Length).ToList();
        var kept = new List<PiiEntity>();
        foreach (var e in ordered)
            if (!kept.Any(k => e.Start < k.Start + k.Length && k.Start < e.Start + e.Length))
                kept.Add(e);
        return kept.OrderBy(e => e.Start).ToList();
    }

    public RedactionResult Redact(string text)
    {
        var entities = Analyze(text);
        if (entities.Count == 0) return new RedactionResult(text, entities);
        var sb = new StringBuilder(text.Length);
        int cursor = 0;
        foreach (var e in entities)
        {
            sb.Append(text, cursor, e.Start - cursor).Append(e.Replacement);
            cursor = e.Start + e.Length;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return new RedactionResult(sb.ToString(), entities);
    }

    private static bool Luhn(string digits)
    {
        int sum = 0; bool alt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alt) { n *= 2; if (n > 9) n -= 9; }
            sum += n; alt = !alt;
        }
        return sum % 10 == 0;
    }
}

/// <summary>
/// Keyed pseudonymization: stable, non-reversible learner IDs (HMAC-SHA256 with a per-school secret
/// that never leaves the edge node). Cloud analytics only ever see pseudonyms.
/// </summary>
public sealed class Pseudonymizer
{
    private readonly byte[] _secret;
    public Pseudonymizer(byte[] secret) => _secret = secret;

    public string Pseudonym(string realId)
    {
        var h = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(realId));
        return "L-" + Convert.ToHexString(h, 0, 5).ToLowerInvariant();
    }
}
