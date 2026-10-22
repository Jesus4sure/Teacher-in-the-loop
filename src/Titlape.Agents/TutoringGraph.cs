using Titlape.Agents.Graph;
using Titlape.Governance.Guardrails;

namespace Titlape.Agents;

/// <summary>
/// Wires the four specialised micro-agents into the multi-agent reflection graph:
///
/// <code>
///   input_guard ─┬─► escalate ───────────────────────────────────────────────┐
///                ├─► refuse ─────────────────────────────────────────────────┤
///                └─► diagnose ─► socratic_plan ─► localize_pre ─► generate   │
///                                   ▲                               │        │
///                                   │                         localize_post  │
///                                   │                               │        │
///                                   └──── (blocked, retry) ◄── safety_check  │
///                                                    (exhausted) │     │ ok  │
///                                                  safe_fallback ┘     ▼     ▼
///                                                               respond ──► END
/// </code>
/// </summary>
public static class TutoringGraph
{
    public static StateGraph<TutorState> Build(AgentServices svc, Func<TutorState, CancellationToken, Task> respond)
    {
        var diagnostic = new DiagnosticAgent(svc);
        var socratic = new SocraticAgent(svc);
        var localization = new LocalizationAgent(svc);
        var safety = new SafetyAgent(svc);

        var g = new StateGraph<TutorState> { MaxSteps = 10 + (svc.MaxReflections + 1) * 5 }
            .AddNode("input_guard", (s, ct) =>
            {
                var v = svc.Guardrails.CheckInput(s.LearnerInput);
                s.InputVerdict = v;
                s.RedactedInput = v.Text;
                svc.Tracer.Step(s.Trace, SafetyAgent.Name, "input_rails",
                    v.Violations.Count == 0 ? "Learner message passed input rails." : $"Input rails: {v.Explain()}.",
                    new Dictionary<string, string> { ["violations"] = v.Explain() });
                return Task.CompletedTask;
            })
            .AddConditionalEdge("input_guard", s =>
                s.InputVerdict!.Escalate ? "escalate" : !s.InputVerdict.Allowed ? "refuse" : "diagnose")
            .AddNode("escalate", safety.EscalateAsync).AddEdge("escalate", "respond")
            .AddNode("refuse", safety.RefuseAsync).AddEdge("refuse", "respond")
            .AddNode("diagnose", diagnostic.RunAsync).AddEdge("diagnose", "socratic_plan")
            .AddNode("socratic_plan", socratic.PlanAsync).AddEdge("socratic_plan", "localize_pre")
            .AddNode("localize_pre", localization.PreGenerateAsync).AddEdge("localize_pre", "generate")
            .AddNode("generate", socratic.GenerateAsync).AddEdge("generate", "localize_post")
            .AddNode("localize_post", localization.PostGenerateAsync).AddEdge("localize_post", "safety_check")
            .AddNode("safety_check", safety.CheckAsync).AddConditionalEdge("safety_check", safety.Route)
            .AddNode("safe_fallback", safety.SafeFallbackAsync).AddEdge("safe_fallback", "respond")
            .AddNode("respond", respond).AddEdge("respond", StateGraph<TutorState>.End)
            .SetEntryPoint("input_guard");
        g.Validate();
        return g;
    }
}
