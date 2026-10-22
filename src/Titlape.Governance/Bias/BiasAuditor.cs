namespace Titlape.Governance.Bias;

public sealed record GroupRate(string Group, int N, double Rate);

public sealed record BiasFinding(
    string Metric,
    string Attribute,
    IReadOnlyList<GroupRate> Groups,
    double DisparityRatio,
    bool Flagged,
    string Interpretation);

/// <summary>
/// Bias auditor (Evidently-AI role, RQ 2.2). Compares outcome rates across demographic and dialect
/// cohorts using the four-fifths (80%) rule. "Adverse" metrics (e.g. guardrail blocks) are compared on
/// their complement so a ratio below 0.8 always means one cohort is treated worse.
/// </summary>
public sealed class BiasAuditor
{
    public double Threshold { get; init; } = 0.8;
    public int MinGroupSize { get; init; } = 5;

    public BiasFinding Audit<T>(string metric, string attribute, IEnumerable<T> records,
        Func<T, string> group, Func<T, bool> outcome, bool outcomeIsAdverse)
    {
        var groups = records.GroupBy(group)
            .Select(g => new GroupRate(g.Key, g.Count(), g.Count() == 0 ? 0 : g.Count(outcome) / (double)g.Count()))
            .OrderBy(g => g.Group)
            .ToList();
        var eligible = groups.Where(g => g.N >= MinGroupSize).ToList();
        if (eligible.Count < 2)
            return new BiasFinding(metric, attribute, groups, 1, false, "insufficient data for comparison");

        var favourable = eligible.Select(g => outcomeIsAdverse ? 1 - g.Rate : g.Rate).ToList();
        double max = favourable.Max(), min = favourable.Min();
        double ratio = max <= 0 ? 1 : min / max;
        bool flagged = ratio < Threshold;
        var worst = eligible[favourable.IndexOf(min)].Group;
        return new BiasFinding(metric, attribute, groups, Math.Round(ratio, 3), flagged,
            flagged ? $"'{worst}' cohort fares worse (ratio {ratio:0.00} < {Threshold:0.00}) — review rules and content for this cohort"
                    : $"within tolerance (ratio {ratio:0.00} ≥ {Threshold:0.00})");
    }
}
