using Titlape.Core;
using Titlape.Orchestration;

namespace Titlape.Evaluation;

/// <summary>
/// Populates a fresh demo deployment with realistic after-school activity by running simulated learners
/// through the real agent pipeline, so the Teacher Dashboard has mastery data, alerts, proposals and
/// traces to inspect. A small amount of model fault injection is enabled during seeding only, so blocked
/// drafts appear in traces and the safety loop can be inspected.
/// </summary>
public static class DemoSeeder
{
    public static async Task SeedAsync(TitlapePlatform p, int sessionsPerLearner = 3, int turnsPerSession = 12, int seed = 42)
    {
        var rng = new Random(seed);
        var (leak, drift, hall) = (p.OfflineEngine.LeakRate, p.OfflineEngine.DriftRate, p.OfflineEngine.HallucinationRate);
        p.OfflineEngine.LeakRate = 0.08; p.OfflineEngine.DriftRate = 0.03; p.OfflineEngine.HallucinationRate = 0.03;
        try
        {
            int i = 0;
            foreach (var student in p.Roster.Students)
            {
                var ability = 0.15 + rng.NextDouble() * 0.8;
                var learner = new SimulatedLearner(student.Id, seed + i++, ability) { InjectionRate = 0.005 };
                var principal = p.Tokens.Validate(p.Login(student.Id)).Value!;
                for (int s = 0; s < sessionsPerLearner; s++)
                {
                    var view = p.Orchestrator.StartSession(principal, seed: rng.Next());
                    for (int t = 0; t < turnsPerSession; t++)
                    {
                        var session = p.Orchestrator.Session(view.SessionId)!;
                        var item = session.CurrentItem!.Item;
                        var hints = session.HintsUsedOnItem;
                        var msg = learner.Respond(item, hints);
                        var r = await p.Orchestrator.SubmitAsync(principal, view.SessionId, msg, 6 + rng.NextDouble() * 35);
                        if (r.Action is "Praise" or "Hint") learner.Learn(item.SkillCode, hints > 0, r.Action == "Praise");
                    }
                }
            }
            p.Sync.SyncNow();
            p.Audit.Append("system", "demo.seeded", p.Options.EdgeNodeId, $"{p.Roster.Students.Count} learners × {sessionsPerLearner} sessions");
        }
        finally
        {
            p.OfflineEngine.LeakRate = leak; p.OfflineEngine.DriftRate = drift; p.OfflineEngine.HallucinationRate = hall;
        }
    }
}
