using System.Security.Cryptography;
using System.Text;
using Titlape.Core;

namespace Titlape.Models;

/// <summary>
/// Deterministic, zero-dependency generator that stands in for a quantized SLM when no GGUF model is
/// installed on an edge node (or when the battery is too low to run one). It realises the structured
/// "plan" that agents put in <see cref="LlmRequest.Slots"/> into natural tutoring language, so the full
/// agent → guardrail → teacher pipeline runs on any hardware, fully offline.
///
/// <para><see cref="LeakRate"/> deliberately injects answer-leaking or off-topic outputs so that the
/// Safety Agent and evaluation harness can be tested against a misbehaving model (RQ 1.2 / 2.2).</para>
/// </summary>
public sealed class OfflineTemplateEngine : InstrumentedModel
{
    private readonly Random _rng;

    public OfflineTemplateEngine(int seed = 7) => _rng = new Random(seed);

    public override string Name => "titlape-offline-template-v1";
    public override ExecutionSite Site => ExecutionSite.Edge;
    public override string Quantization => "n/a (rule-based)";
    public override bool IsAvailable => true;

    /// <summary>Probability of emitting an unsafe draft (fault injection for guardrail evaluation).</summary>
    public double LeakRate { get; set; }
    public double DriftRate { get; set; }
    public double HallucinationRate { get; set; }

    private int _injectedLeaks, _injectedDrift, _injectedHallucinations;
    public int InjectedLeaks => _injectedLeaks;
    public int InjectedDrift => _injectedDrift;
    public int InjectedHallucinations => _injectedHallucinations;

    /// <summary>Hash of the template pack — verified by the ModelRegistry like a model file.</summary>
    public static string TemplatePackSha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", Openers) + string.Join("|", Closers)))).ToLowerInvariant();

    public static IEnumerable<string> VettedPhrases => Openers.Concat(Closers)
        .Concat(new[] { "Excellent work!", "Well done!", "That's right — great thinking!", "Let's keep working on today's lesson together." });

    private static readonly string[] Openers =
    {
        "Good effort — let's look at this together.",
        "Nice try! Let's slow down and think.",
        "You're close to something. Let's check one step.",
        "Let's think about this one carefully.",
        "Thank you for trying. Let's reason it out."
    };

    private static readonly string[] Closers =
    {
        "What do you get when you try that?",
        "Can you try that step and tell me what you find?",
        "What would you write for that step?",
        "Try it and share your new answer."
    };

    protected override Task<string> GenerateCoreAsync(LlmRequest request, CancellationToken ct)
    {
        var rng = request.Seed is int s ? new Random(s) : _rng;
        string Slot(string k, string d = "") => request.Slots.TryGetValue(k, out var v) ? v : d;

        string text = request.Task switch
        {
            "socratic_hint" => Hint(rng, Slot("scaffold"), Slot("metaphor")),
            "socratic_praise" => $"{Pick(rng, "Excellent work!", "Well done!", "That's right — great thinking!")} {Slot("reinforcement")}".Trim(),
            "socratic_open" => $"Let's practise {Slot("skill_title")}. {Slot("item_prompt")} Take your time, and show me your thinking.",
            "teacher_summary" => Slot("summary_plan"),
            "localize" => Slot("text"),
            _ => Slot("text", "Let's keep working on today's lesson together.")
        };

        // Fault injection — emulate failure modes of a small quantised model.
        if (request.Task == "socratic_hint")
        {
            var roll = rng.NextDouble();
            if (roll < LeakRate && request.Slots.TryGetValue("reference_answer", out var ans))
            { text += $" The answer is {ans}."; Interlocked.Increment(ref _injectedLeaks); }
            else if (roll < LeakRate + DriftRate)
            {
                text += " By the way, did you know football was invented in England? Let me tell you about the World Cup.";
                Interlocked.Increment(ref _injectedDrift);
            }
            else if (roll < LeakRate + DriftRate + HallucinationRate)
            {
                int x = rng.Next(3, 10), y = rng.Next(3, 10);
                text += $" Remember that {x} + {y} = {x + y + 1}.";
                Interlocked.Increment(ref _injectedHallucinations);
            }
        }
        return Task.FromResult(text);
    }

    private static string Hint(Random rng, string scaffold, string metaphor)
    {
        var sb = new StringBuilder();
        sb.Append(Pick(rng, Openers)).Append(' ');
        if (!string.IsNullOrWhiteSpace(metaphor)) sb.Append(metaphor.TrimEnd('.')).Append(". ");
        sb.Append(scaffold.Trim());
        if (!scaffold.TrimEnd().EndsWith('?')) sb.Append(' ').Append(Pick(rng, Closers));
        return sb.ToString();
    }

    private static string Pick(Random rng, params string[] options) => options[rng.Next(options.Length)];
}
