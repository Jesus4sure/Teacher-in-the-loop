using Titlape.Agents.Graph;
using Titlape.Context.Curriculum;
using Titlape.Context.Memory;
using Titlape.Context.Rag;
using Titlape.Core;
using Titlape.Governance.Guardrails;
using Titlape.Governance.Tracing;

namespace Titlape.Agents;

public enum TutorAction { None, Open, Hint, Praise, Refuse, Escalate }

/// <summary>Live tutoring-session context (identity is a pseudonym only).</summary>
public sealed class SessionContext
{
    public required string SessionId { get; init; }
    public required string Pseudonym { get; init; }
    public required string ClassId { get; init; }
    public required string SchoolId { get; init; }
    public LearnerLanguage Language { get; set; } = LearnerLanguage.English;
    public bool Consent { get; init; }
    public GeneratedItem? CurrentItem { get; set; }
    public int HintLevel { get; set; }
    public int AttemptsOnItem { get; set; }
    public int HintsUsedOnItem { get; set; }
    public bool MetaphorUsedOnItem { get; set; }
    public DateTimeOffset ItemShownAt { get; set; } = DateTimeOffset.UtcNow;
    public int ItemsCompleted { get; set; }
    public List<TutorTurn> Transcript { get; } = new();
    public int Seed { get; set; } = Environment.TickCount;
}

/// <summary>Events emitted by agents for the Master Orchestrator to act on (alerts, proposals).</summary>
public sealed record AgentSignal(string Kind, string Detail, IReadOnlyDictionary<string, string> Data);

/// <summary>Mutable state flowing through the tutoring graph for one learner turn.</summary>
public sealed class TutorState : ICheckpointable
{
    public required SessionContext Session { get; init; }
    public required AgentTrace Trace { get; init; }
    public string LearnerInput { get; set; } = "";
    public string RedactedInput { get; set; } = "";
    public GuardrailVerdict? InputVerdict { get; set; }

    public AnswerCheck? Check { get; set; }
    public (SkillMastery Before, SkillMastery After)? MasteryUpdate { get; set; }
    public string? Misconception { get; set; }

    public TutorAction Action { get; set; }
    public IReadOnlyList<RetrievedChunk> Retrieved { get; set; } = Array.Empty<RetrievedChunk>();
    public Dictionary<string, string> PlanSlots { get; } = new();
    public string PlanTask { get; set; } = "";
    public string Draft { get; set; } = "";
    public string ModelUsed { get; set; } = "";
    public string RoutingReason { get; set; } = "";
    public GuardrailVerdict? OutputVerdict { get; set; }
    public int Reflections { get; set; }
    public List<string> ReflectionFeedback { get; } = new();
    public bool UsedSafeFallback { get; set; }
    public string FinalResponse { get; set; } = "";
    public bool AdvanceToNextItem { get; set; }
    public List<AgentSignal> Signals { get; } = new();
    public int BlockedDrafts { get; set; }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>
        {
            ["action"] = Action.ToString(),
            ["hint_level"] = Session.HintLevel.ToString(),
            ["attempts_on_item"] = Session.AttemptsOnItem.ToString(),
            ["reflections"] = Reflections.ToString()
        };
        if (Session.CurrentItem is { } i) d["item"] = i.Item.Id;
        if (Check is not null) d["answer_check"] = Check.Outcome.ToString();
        if (Misconception is not null) d["misconception"] = Misconception;
        if (MasteryUpdate is { } m) d["p_known"] = $"{m.Before.PKnown:0.00}→{m.After.PKnown:0.00}";
        if (OutputVerdict is not null) d["output_rails"] = OutputVerdict.Allowed ? "pass" : OutputVerdict.Explain();
        if (Retrieved.Count > 0) d["retrieved"] = string.Join(",", Retrieved.Select(r => r.Chunk.Id));
        return d;
    }
}
