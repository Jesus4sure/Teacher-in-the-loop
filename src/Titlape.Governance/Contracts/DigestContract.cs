using System.Text.Json;
using System.Text.RegularExpressions;

namespace Titlape.Governance.Contracts;

/// <summary>Structured teacher digest produced from agent traces (DSPy output-signature role).</summary>
public sealed record TeacherDigest(
    string Headline,
    string SkillCode,
    int LearnersAffected,
    IReadOnlyList<string> LearnerIds,
    IReadOnlyList<string> EvidenceTraceIds,
    string RecommendedAction,
    double Confidence);

/// <summary>The ground-truth facts a digest must agree with.</summary>
public sealed record DigestFacts(string SkillCode, IReadOnlySet<string> LearnerIds, IReadOnlySet<string> KnownTraceIds, IReadOnlySet<long> AllowedNumbers);

public sealed record ContractResult(bool Valid, IReadOnlyList<string> Errors, string Json);

/// <summary>
/// Output contract that guarantees factual dashboard summaries: every field is type-checked and every
/// claim (counts, learner IDs, evidence traces, numbers in the headline) must be backed by the facts
/// extracted from agent traces. Invalid digests are rejected rather than shown to teachers.
/// </summary>
public static class DigestContract
{
    public static ContractResult Validate(TeacherDigest d, DigestFacts facts)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Headline) || d.Headline.Length > 160) errors.Add("headline must be 1–160 characters");
        if (d.SkillCode != facts.SkillCode) errors.Add($"skill '{d.SkillCode}' does not match evidence skill '{facts.SkillCode}'");
        if (d.LearnersAffected != d.LearnerIds.Count) errors.Add("learnersAffected does not equal number of learner ids");
        foreach (var id in d.LearnerIds.Where(id => !facts.LearnerIds.Contains(id))) errors.Add($"learner '{id}' not present in evidence");
        if (d.EvidenceTraceIds.Count == 0) errors.Add("at least one evidence trace is required");
        foreach (var t in d.EvidenceTraceIds.Where(t => !facts.KnownTraceIds.Contains(t))) errors.Add($"trace '{t}' does not exist");
        foreach (Match m in Regex.Matches(d.Headline, @"\d+"))
            if (!facts.AllowedNumbers.Contains(long.Parse(m.Value))) errors.Add($"headline number {m.Value} is not supported by evidence");
        if (d.Confidence is < 0 or > 1) errors.Add("confidence must be within [0,1]");
        if (string.IsNullOrWhiteSpace(d.RecommendedAction)) errors.Add("recommendedAction is required");
        return new ContractResult(errors.Count == 0, errors, JsonSerializer.Serialize(d));
    }
}
