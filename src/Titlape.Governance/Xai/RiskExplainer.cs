using Titlape.Core;

namespace Titlape.Governance.Xai;

/// <summary>Named feature with a plain-language description used in teacher-facing explanations.</summary>
public sealed record RiskFeature(string Name, string Label, double Weight, Func<double, string> Describe);

/// <summary>
/// Interpretable learning-risk model (logistic over normalised features). Deliberately simple so the
/// Shapley attributions are exact and stable — teachers see why a learner was flagged, not a black box.
/// </summary>
public sealed class LearningRiskModel
{
    public double Bias { get; init; } = -3.2;

    public IReadOnlyList<RiskFeature> Features { get; } = new[]
    {
        new RiskFeature("mastery_gap", "Mastery gap", 3.2,
            v => $"estimated mastery of the current skill is about {(1 - v):P0}"),
        new RiskFeature("error_streak", "Consecutive errors", 2.2,
            v => $"{Math.Round(v * 5)} wrong answers in a row on the current skill"),
        new RiskFeature("hint_reliance", "Hint reliance", 1.4,
            v => $"uses about {v * 3:0.#} hints per question"),
        new RiskFeature("misconception_recurrence", "Repeated misconception", 1.6,
            v => $"the same misconception appeared {Math.Round(v * 5)} times"),
        new RiskFeature("response_time", "Slow responses", 0.6,
            v => $"takes about {v * 60:0} seconds per answer"),
        new RiskFeature("session_gap", "Missed sessions", 0.8,
            v => $"{v * 7:0} days since last after-school session"),
    };

    public double Predict(IReadOnlyList<double> x)
    {
        double z = Bias;
        for (int i = 0; i < Features.Count; i++) z += Features[i].Weight * x[i];
        return 1.0 / (1.0 + Math.Exp(-z));
    }
}

/// <summary>
/// Exact Shapley-value attribution (the quantity SHAP approximates). With ≤ 12 features the 2^n
/// coalitions are enumerated exactly, using interventional (baseline-substitution) value functions.
/// Satisfies efficiency: Σ φᵢ = f(x) − f(baseline).
/// </summary>
public static class ShapleyExplainer
{
    public static double[] Explain(Func<IReadOnlyList<double>, double> model, IReadOnlyList<double> x, IReadOnlyList<double> baseline)
    {
        int n = x.Count;
        if (n > 12) throw new ArgumentException("Exact Shapley limited to 12 features.");
        var cache = new Dictionary<int, double>();
        double Value(int mask)
        {
            if (cache.TryGetValue(mask, out var v)) return v;
            var input = new double[n];
            for (int i = 0; i < n; i++) input[i] = (mask & (1 << i)) != 0 ? x[i] : baseline[i];
            return cache[mask] = model(input);
        }

        var fact = new double[n + 1];
        fact[0] = 1;
        for (int i = 1; i <= n; i++) fact[i] = fact[i - 1] * i;

        var phi = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int mask = 0; mask < 1 << n; mask++)
            {
                if ((mask & (1 << i)) != 0) continue;
                int s = System.Numerics.BitOperations.PopCount((uint)mask);
                double w = fact[s] * fact[n - s - 1] / fact[n];
                phi[i] += w * (Value(mask | (1 << i)) - Value(mask));
            }
        }
        return phi;
    }

    public static List<FeatureAttribution> ExplainRisk(LearningRiskModel model, IReadOnlyList<double> x, IReadOnlyList<double> baseline)
    {
        var phi = Explain(model.Predict, x, baseline);
        return model.Features.Select((f, i) => new FeatureAttribution(
                f.Label, Math.Round(x[i], 3), Math.Round(phi[i], 4),
                $"{f.Describe(x[i])} ({(phi[i] >= 0 ? "raises" : "lowers")} risk by {Math.Abs(phi[i]) * 100:0.#} points vs. class average)"))
            .OrderByDescending(a => Math.Abs(a.Contribution))
            .ToList();
    }
}
