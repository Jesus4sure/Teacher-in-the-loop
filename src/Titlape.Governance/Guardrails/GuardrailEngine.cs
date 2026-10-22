using System.Text.Json;
using System.Text.RegularExpressions;
using Titlape.Core;
using Titlape.CrossCutting;
using Titlape.Data.Privacy;

namespace Titlape.Governance.Guardrails;

public enum RailSeverity { Info, Flag, Block, Escalate }

public sealed record GuardrailViolation(string Rule, RailSeverity Severity, string Detail);

public sealed record GuardrailVerdict(bool Allowed, IReadOnlyList<GuardrailViolation> Violations, string Text)
{
    public bool Has(string rule) => Violations.Any(v => v.Rule == rule);
    public bool Escalate => Violations.Any(v => v.Severity == RailSeverity.Escalate);
    public string Explain() => Violations.Count == 0 ? "passed all rails" :
        string.Join("; ", Violations.Select(v => $"{v.Rule}: {v.Detail}"));
}

/// <summary>Context needed by output rails to judge a tutor draft.</summary>
public sealed record OutputContext(string? ReferenceAnswer, string? SkillCode, bool LearnerAlreadySolved, LearnerLanguage Language);

/// <summary>
/// Guardrail policy — the editable equivalent of NeMo Guardrails Colang flows. Stored as JSON so the
/// "retarget safety rules" refinement trigger (hallucination rate &gt; 1%) is a configuration change.
/// </summary>
public sealed class GuardrailPolicy
{
    public List<string> PromptInjectionPatterns { get; set; } = new()
    {
        @"ignore (all |any |the )?(previous|prior|above|earlier) (instructions|rules|prompts?)",
        @"(disregard|forget) (your|all|the) (rules|instructions|guidelines)",
        @"you are (now|no longer) ",
        @"(reveal|show|print|repeat) (me )?(your|the) (system )?(prompt|instructions)",
        @"\b(jailbreak|developer mode|dan mode)\b",
        @"pretend (to be|you are)",
        @"act as (an?|my) (?!student|pupil)",
        @"</?(system|assistant)>",
    };

    public List<string> AnswerSeekingPatterns { get; set; } = new()
    {
        @"(just |please )?(tell|give|show) me the (final )?answer",
        @"what('s| is) the answer",
        @"\banswer (it|this) for me\b",
        @"do it for me",
    };

    public List<string> ToxicTerms { get; set; } = new()
    {
        "stupid", "idiot", "dumb", "useless", "fool", "shut up", "hate you", "ugly", "worthless", "nonsense child"
    };

    /// <summary>Disclosures that must reach a human immediately (safeguarding of minors).</summary>
    public List<string> SafeguardingPatterns { get; set; } = new()
    {
        @"\b(he|she|they|someone|somebody|my \w+) (hits?|beats?|hurts?|touche[sd]?) me\b",
        @"\b(want|going) to (die|kill myself)\b",
        @"\bhurt myself\b",
        @"\bscared to go home\b",
        @"\b(no|haven't had any|have not eaten) food\b",
        @"\babus(e|ed|ing)\b",
    };

    public double OffCurriculumMinCoverage { get; set; } = 0.5;
    public int OffCurriculumMinContentWords { get; set; } = 3;
    public int MaxOutputChars { get; set; } = 700;

    public static GuardrailPolicy Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new GuardrailPolicy();
        return JsonSerializer.Deserialize<GuardrailPolicy>(File.ReadAllText(path)) ?? new GuardrailPolicy();
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// Layer 3 runtime guardrails. Input rails screen what learners type (prompt injection, toxicity,
/// safeguarding disclosures, PII). Output rails validate every tutor draft before it is displayed
/// (answer leakage, arithmetic hallucination, off-curriculum drift, toxicity, PII).
/// </summary>
public sealed class GuardrailEngine
{
    private readonly GuardrailPolicy _policy;
    private readonly ICurriculumGrounding _grounding;
    private readonly PiiGateway _pii;
    private readonly Regex[] _injection, _answerSeeking, _safeguarding;

    private static readonly Regex Equation = new(
        @"(?<![\d/.])(\d+(?:\s*[+\-−×x*]\s*\d+)+)\s*=\s*(\d+)(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex Fraction = new(@"(?<![\d/])(\d+)\s*/\s*(\d+)(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex Integer = new(@"(?<![\d/.,])\d{1,3}(?:,\d{3})+(?![\d/])|(?<![\d/.,])\d+(?![\d/]|,\d{3})", RegexOptions.Compiled);

    public GuardrailEngine(GuardrailPolicy policy, ICurriculumGrounding grounding, PiiGateway pii)
    {
        _policy = policy; _grounding = grounding; _pii = pii;
        static Regex[] C(IEnumerable<string> p) => p.Select(x => new Regex(x, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();
        _injection = C(policy.PromptInjectionPatterns);
        _answerSeeking = C(policy.AnswerSeekingPatterns);
        _safeguarding = C(policy.SafeguardingPatterns);
    }

    public GuardrailPolicy Policy => _policy;

    public GuardrailVerdict CheckInput(string text)
    {
        using var act = TitlapeTelemetry.Source.StartActivity("guardrail.input");
        act?.SetTag(TitlapeTelemetry.SpanKind, "GUARDRAIL");
        var v = new List<GuardrailViolation>();

        if (_safeguarding.Any(r => r.IsMatch(text)))
            v.Add(new("safeguarding", RailSeverity.Escalate, "possible safeguarding disclosure — route to teacher immediately"));
        if (_injection.FirstOrDefault(r => r.IsMatch(text)) is { } inj)
            v.Add(new("prompt_injection", RailSeverity.Block, $"matched pattern /{inj}/"));
        if (_answerSeeking.Any(r => r.IsMatch(text)))
            v.Add(new("answer_seeking", RailSeverity.Info, "learner asked for the answer — respond with a scaffold"));
        if (Toxic(text) is { } tox)
            v.Add(new("toxicity", RailSeverity.Flag, $"contains '{tox}'"));

        var red = _pii.Redact(text);
        if (red.HadPii)
            v.Add(new("pii", RailSeverity.Info, $"redacted {string.Join(", ", red.Entities.Select(e => e.Type).Distinct())}"));

        Record(v);
        return new GuardrailVerdict(!v.Any(x => x.Severity is RailSeverity.Block), v, red.Text);
    }

    public GuardrailVerdict CheckOutput(string draft, OutputContext ctx)
    {
        using var act = TitlapeTelemetry.Source.StartActivity("guardrail.output");
        act?.SetTag(TitlapeTelemetry.SpanKind, "GUARDRAIL");
        var v = new List<GuardrailViolation>();

        if (!ctx.LearnerAlreadySolved && ctx.ReferenceAnswer is { } ans && LeaksAnswer(draft, ans))
            v.Add(new("answer_leakage", RailSeverity.Block, "draft reveals the reference answer before the learner solved it"));

        foreach (var claim in FalseArithmetic(draft))
            v.Add(new("hallucination", RailSeverity.Block, $"false arithmetic claim '{claim}'"));

        foreach (var sentence in Sentences(draft))
        {
            var cov = _grounding.VocabularyCoverage(sentence, out var unknown);
            if (unknown.Count >= _policy.OffCurriculumMinContentWords && cov < _policy.OffCurriculumMinCoverage)
            {
                v.Add(new("off_curriculum", RailSeverity.Block,
                    $"sentence outside syllabus vocabulary (coverage {cov:P0}; unknown: {string.Join(", ", unknown.Take(5))})"));
                break;
            }
        }

        if (Toxic(draft) is { } tox) v.Add(new("toxicity", RailSeverity.Block, $"contains '{tox}'"));
        var pii = _pii.Analyze(draft);
        if (pii.Count > 0) v.Add(new("pii", RailSeverity.Block, $"draft contains {string.Join(", ", pii.Select(p => p.Type).Distinct())}"));
        if (draft.Length > _policy.MaxOutputChars) v.Add(new("length", RailSeverity.Block, $"{draft.Length} chars exceeds {_policy.MaxOutputChars}"));

        Record(v);
        return new GuardrailVerdict(!v.Any(x => x.Severity is RailSeverity.Block or RailSeverity.Escalate), v, draft);
    }

    /// <summary>True if the draft states the answer (any equivalent fraction counts).</summary>
    public static bool LeaksAnswer(string draft, string answer)
    {
        var a = answer.Trim();
        var frac = Fraction.Match(a);
        if (frac.Success && frac.Length == a.Length)
        {
            long an = long.Parse(frac.Groups[1].Value), ad = long.Parse(frac.Groups[2].Value);
            return Fraction.Matches(draft).Any(m =>
                long.Parse(m.Groups[1].Value) * ad == an * long.Parse(m.Groups[2].Value));
        }
        var target = a.Replace(",", "");
        return Integer.Matches(draft).Any(m => m.Value.Replace(",", "") == target);
    }

    /// <summary>Finds arithmetic statements ("4 + 3 + 1 = 8") whose stated result is wrong.</summary>
    public static IEnumerable<string> FalseArithmetic(string text)
    {
        foreach (Match m in Equation.Matches(text))
        {
            var lhs = m.Groups[1].Value;
            var stated = long.Parse(m.Groups[2].Value);
            var value = Evaluate(lhs);
            if (value is not null && value.Value != stated) yield return m.Value;
        }
    }

    private static long? Evaluate(string expr)
    {
        var tokens = Regex.Matches(expr, @"\d+|[+\-−×x*]").Select(t => t.Value).ToList();
        // First pass: multiplication.
        var terms = new List<long>(); var ops = new List<char>();
        long current = long.Parse(tokens[0]);
        for (int i = 1; i + 1 < tokens.Count; i += 2)
        {
            var op = tokens[i]; var n = long.Parse(tokens[i + 1]);
            if (op is "×" or "x" or "*") current *= n;
            else { terms.Add(current); ops.Add(op is "+" ? '+' : '-'); current = n; }
        }
        terms.Add(current);
        long result = terms[0];
        for (int i = 0; i < ops.Count; i++) result = ops[i] == '+' ? result + terms[i + 1] : result - terms[i + 1];
        return result;
    }

    private string? Toxic(string text) =>
        _policy.ToxicTerms.FirstOrDefault(t => Regex.IsMatch(text, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase));

    private static IEnumerable<string> Sentences(string text) =>
        Regex.Split(text, @"(?<=[.!?])\s+").Where(s => s.Trim().Length > 0);

    private static void Record(List<GuardrailViolation> v)
    {
        foreach (var x in v.Where(x => x.Severity >= RailSeverity.Block))
            TitlapeTelemetry.GuardrailBlocks.Add(1, new KeyValuePair<string, object?>("rule", x.Rule));
    }
}
