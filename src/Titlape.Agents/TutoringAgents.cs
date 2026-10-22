using System.Diagnostics;
using Titlape.Context.Curriculum;
using Titlape.Context.Memory;
using Titlape.Context.Rag;
using Titlape.Core;
using Titlape.CrossCutting.Security;
using Titlape.Governance.Guardrails;
using Titlape.Governance.Tracing;
using Titlape.Models;

namespace Titlape.Agents;

/// <summary>Services available to Layer 5 agents (each use is checked against the agent's tool grants).</summary>
public sealed class AgentServices
{
    public required StudentMemory Memory { get; init; }
    public required PathwayService Pathways { get; init; }
    public required CurriculumRagEngine Rag { get; init; }
    public required ItemBank Items { get; init; }
    public required GuardrailEngine Guardrails { get; init; }
    public required ModelRouter Router { get; init; }
    public required CotTracer Tracer { get; init; }
    public required AgentToolPolicy ToolPolicy { get; init; }
    public IReadOnlyList<GlossaryTerm> Glossary { get; init; } = CurriculumPack.Glossary().ToList();
    public int MaxReflections { get; init; } = 2;
    public int MisconceptionProposalThreshold { get; init; } = 3;
    public int ErrorStreakProposalThreshold { get; init; } = 4;
}

/// <summary>
/// Diagnostic Agent — grades the learner's answer, identifies the misconception signature, updates the
/// BKT mastery estimate and, when evidence accumulates, proposes (never applies) a pathway change.
/// </summary>
public sealed class DiagnosticAgent(AgentServices svc)
{
    public const string Name = "diagnostic";

    public Task RunAsync(TutorState s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        svc.ToolPolicy.Demand(Name, "student_memory.write");
        var session = s.Session;
        var gen = session.CurrentItem ?? throw new InvalidOperationException("No active item.");
        var check = AnswerChecker.Check(gen.Item, s.RedactedInput);
        s.Check = check;

        if (check.Outcome == AnswerOutcome.Unparseable)
        {
            s.Action = TutorAction.Hint;
            // "I don't understand" / "tell me the answer" moves one rung up the scaffold ladder.
            session.HintLevel = session.HintsUsedOnItem == 0 ? Math.Max(1, session.HintLevel) : Math.Min(3, session.HintLevel + 1);
            var why = s.InputVerdict?.Has("answer_seeking") == true
                ? "The learner asked for the answer. Policy: never give it — offer the next scaffold instead."
                : "The learner wrote a comment or question rather than an answer, so nothing was graded.";
            svc.Tracer.Step(s.Trace, Name, "grade", why, new Dictionary<string, string> { ["input_kind"] = "non-answer" }, sw.Elapsed.TotalMilliseconds);
            return Task.CompletedTask;
        }

        session.AttemptsOnItem++;
        bool correct = check.Outcome == AnswerOutcome.Correct;
        var misconception = check.MisconceptionCode ?? (correct ? null : "unknown");
        s.Misconception = misconception;
        var update = svc.Memory.Observe(session.Pseudonym, gen.Item.SkillCode, correct, session.HintsUsedOnItem,
            misconception == "unknown" ? null : misconception);
        s.MasteryUpdate = update;

        string plain;
        if (correct)
        {
            s.Action = TutorAction.Praise;
            s.AdvanceToNextItem = true;
            plain = $"Answer {check.Normalized} is correct after {session.HintsUsedOnItem} hint(s). " +
                    $"Mastery estimate for {gen.Item.SkillCode} moved from {update.Before.PKnown:P0} to {update.After.PKnown:P0}" +
                    (session.HintsUsedOnItem > 0 ? " (hinted answers count as weaker evidence)." : ".");
        }
        else
        {
            s.Action = TutorAction.Hint;
            session.HintLevel = Math.Min(3, session.HintLevel + 1);
            var mcName = CurriculumPack.MisconceptionNames.GetValueOrDefault(misconception!, misconception!);
            plain = $"Answer {check.Normalized} is incorrect. " +
                    (check.MisconceptionCode is not null
                        ? $"It matches the known error pattern '{mcName}'. "
                        : "It does not match a known error pattern. ") +
                    $"Mastery estimate fell from {update.Before.PKnown:P0} to {update.After.PKnown:P0}. Moving to hint level {session.HintLevel}.";
            if (check.MisconceptionCode is not null)
                s.Signals.Add(new AgentSignal("misconception", mcName, new Dictionary<string, string>
                {
                    ["skill"] = gen.Item.SkillCode, ["code"] = check.MisconceptionCode, ["answer"] = check.Normalized
                }));
            MaybePropose(s, update.After, gen.Item.SkillCode, check.MisconceptionCode);
        }

        svc.Tracer.Step(s.Trace, Name, "grade", plain, new Dictionary<string, string>
        {
            ["outcome"] = check.Outcome.ToString(),
            ["misconception"] = misconception ?? "-",
            ["p_known_before"] = update.Before.PKnown.ToString("0.000"),
            ["p_known_after"] = update.After.PKnown.ToString("0.000"),
            ["error_streak"] = update.After.CurrentErrorStreak.ToString()
        }, sw.Elapsed.TotalMilliseconds);
        return Task.CompletedTask;
    }

    private string? NextIndependentSkill(TutorState s, LearningPathway path, string skill) =>
        path.SkillSequence.Skip(path.CurrentIndex + 1).FirstOrDefault(c =>
            !CurriculumPack.Skill(c).Prerequisites.Contains(skill) &&
            CurriculumPack.Skill(c).Prerequisites.All(p => svc.Memory.Get(s.Session.Pseudonym, p).Mastered || p == skill) &&
            !svc.Memory.Get(s.Session.Pseudonym, c).Mastered);

    private void MaybePropose(TutorState s, SkillMastery m, string skill, string? code)
    {
        var counts = svc.Memory.Misconceptions(s.Session.Pseudonym);
        bool recurring = code is not null && counts.GetValueOrDefault(code) >= svc.MisconceptionProposalThreshold;
        bool streak = m.CurrentErrorStreak >= svc.ErrorStreakProposalThreshold;
        if (!recurring && !streak) return;

        svc.ToolPolicy.Demand("orchestrator", "pathway.propose");
        var path = svc.Pathways.Get(s.Session.Pseudonym);
        var weakPrereqs = CurriculumPack.Skill(skill).Prerequisites
            .Where(p => !svc.Memory.Get(s.Session.Pseudonym, p).Mastered).ToList();
        var reason = recurring
            ? $"'{CurriculumPack.MisconceptionNames[code!]}' has occurred {counts[code!]} times on {skill}"
            : $"{m.CurrentErrorStreak} consecutive errors on {skill}";
        if (weakPrereqs.Count > 0 && path is not null)
        {
            var remaining = path.SkillSequence.Skip(path.CurrentIndex).ToList();
            var seq = weakPrereqs.Concat(remaining.Where(r => !weakPrereqs.Contains(r))).ToList();
            var proposal = svc.Pathways.Propose(s.Session.Pseudonym, Name,
                $"{reason}. Prerequisite(s) {string.Join(", ", weakPrereqs)} are not yet mastered; revisit them first.", seq);
            s.Signals.Add(new AgentSignal("pathway_proposal", reason, new Dictionary<string, string>
            {
                ["proposal_id"] = proposal.Id, ["skill"] = skill
            }));
            svc.Tracer.Step(s.Trace, Name, "propose",
                $"Proposed (not applied) a pathway change for teacher review: revisit {string.Join(", ", weakPrereqs)} before {skill}.",
                new Dictionary<string, string> { ["proposal_id"] = proposal.Id });
        }
        else if (path is not null && path.CurrentSkill == skill && NextIndependentSkill(s, path, skill) is { } next)
        {
            // No weak prerequisite to revisit: propose interleaving — pause this skill for a teacher-led
            // reteach and continue with an independent strand meanwhile.
            var seq = path.SkillSequence.ToList();
            seq.Remove(next);
            seq.Insert(path.CurrentIndex, next);
            var proposal = svc.Pathways.Propose(s.Session.Pseudonym, Name,
                $"{reason}. Prerequisites are mastered, so pause {skill} for a short teacher-led reteach and continue with {next} meanwhile.", seq.Skip(path.CurrentIndex).ToList());
            s.Signals.Add(new AgentSignal("pathway_proposal", reason, new Dictionary<string, string> { ["proposal_id"] = proposal.Id, ["skill"] = skill }));
            svc.Tracer.Step(s.Trace, Name, "propose",
                $"Proposed (not applied) interleaving {next} while the teacher reteaches {skill}.", new Dictionary<string, string> { ["proposal_id"] = proposal.Id });
        }
        else
        {
            s.Signals.Add(new AgentSignal("reteach", reason, new Dictionary<string, string> { ["skill"] = skill }));
            svc.Tracer.Step(s.Trace, Name, "propose",
                $"{reason}; prerequisites are mastered, so a short teacher-led reteach is recommended.", null);
        }
    }
}

/// <summary>
/// Socratic Tutoring Agent — plans a graduated scaffold grounded in retrieved syllabus content and asks
/// the routed language model to voice it. It holds the reference answer only so the Safety Agent can
/// verify it is never revealed.
/// </summary>
public sealed class SocraticAgent(AgentServices svc)
{
    public const string Name = "socratic";

    public const string SystemPrompt =
        "You are TITLAPE, a patient after-school maths tutor for primary pupils in Sub-Saharan Africa, working under the " +
        "supervision of their class teacher. Rules: (1) Never state, confirm or hint at the final answer to the current " +
        "question. (2) Ask exactly one guiding question that moves the pupil one step forward. (3) Use only the syllabus " +
        "content provided. (4) Use short, simple, kind sentences (max 3 sentences). (5) Do not ask for or repeat personal " +
        "information. (6) If a metaphor is provided, use it briefly.";

    public static IReadOnlyDictionary<string, string> NudgeTexts => Nudges;

    private static readonly Dictionary<string, string> Nudges = new()
    {
        ["digit-not-value"] = "Remember, a digit's value depends on its place.",
        ["wrong-place"] = "Count the places carefully from the right.",
        ["no-regroup"] = "Check the ones column again — was the total more than 9?",
        ["column-concatenation"] = "Each place can hold only one digit.",
        ["smaller-from-larger"] = "We must always take the bottom number away from the top number.",
        ["borrow-without-decrement"] = "When we borrow a ten, the tens digit becomes one less.",
        ["multiplication-carry-forgotten"] = "Don't forget the tens that come from multiplying the ones.",
        ["partial-product"] = "Remember to multiply the tens part too.",
        ["additive-reasoning"] = "Equivalent fractions come from multiplying, not adding.",
        ["added-denominators"] = "The size of the parts does not change when we add them."
    };

    public Task PlanAsync(TutorState s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var gen = s.Session.CurrentItem!;
        s.PlanSlots.Clear();
        s.PlanSlots["skill_title"] = CurriculumPack.Skill(gen.Item.SkillCode).Title;
        s.PlanSlots["item_prompt"] = gen.Item.Prompt;

        if (s.Action == TutorAction.Praise)
        {
            s.PlanTask = "socratic_praise";
            s.PlanSlots["reinforcement"] = gen.Reinforcement;
            svc.Tracer.Step(s.Trace, Name, "plan", "Planned praise plus a one-line reinforcement of the method.", null, sw.Elapsed.TotalMilliseconds);
            return Task.CompletedTask;
        }

        svc.ToolPolicy.Demand(Name, "curriculum.search");
        int level = Math.Clamp(s.Session.HintLevel, 1, 3);
        if (s.ReflectionFeedback.Any(f => f.StartsWith("answer_leakage"))) level = Math.Max(1, level - s.Reflections);

        var query = gen.Item.Prompt + " " + (s.Misconception is { } mc && CurriculumPack.MisconceptionNames.TryGetValue(mc, out var n) ? n : "");
        s.Retrieved = svc.Rag.Retrieve(query, 3, gen.Item.SkillCode);
        var nudge = s.Misconception is not null && Nudges.TryGetValue(s.Misconception, out var nd) ? nd + " " : "";

        s.PlanTask = "socratic_hint";
        s.PlanSlots["scaffold"] = nudge + gen.HintLadder[level - 1];
        s.PlanSlots["hint_level"] = level.ToString();
        s.PlanSlots["syllabus_context"] = string.Join(" ", s.Retrieved.Where(r => r.Chunk.Kind != "metaphor").Select(r => r.Chunk.Text));
        s.PlanSlots["reference_answer"] = gen.Item.CorrectAnswer;
        if (s.Misconception is not null) s.PlanSlots["diagnosed_misconception"] = s.Misconception;
        if (s.ReflectionFeedback.Count > 0) s.PlanSlots["revision_instructions"] = string.Join(" ", s.ReflectionFeedback);

        svc.Tracer.Step(s.Trace, Name, "plan",
            $"Chose hint level {level} of 3{(nudge.Length > 0 ? " with a targeted nudge for the diagnosed error" : "")}, grounded in " +
            $"{s.Retrieved.Count} syllabus passage(s): {string.Join(", ", s.Retrieved.Select(r => r.Chunk.Id))}." +
            (s.Reflections > 0 ? $" This is revision #{s.Reflections} after a safety rejection." : ""),
            new Dictionary<string, string> { ["hint_level"] = level.ToString(), ["retrieved"] = string.Join(",", s.Retrieved.Select(r => r.Chunk.Id)) },
            sw.Elapsed.TotalMilliseconds);
        return Task.CompletedTask;
    }

    public async Task GenerateAsync(TutorState s, CancellationToken ct)
    {
        svc.ToolPolicy.Demand(Name, "llm.generate");
        var route = svc.Router.Route(s.PlanTask);
        var req = new LlmRequest
        {
            Task = s.PlanTask,
            SystemPrompt = SystemPrompt,
            Slots = new Dictionary<string, string>(s.PlanSlots),
            Messages = s.Session.Transcript.TakeLast(6)
                .Select(t => new ChatMessage(t.Speaker == "learner" ? "user" : "assistant", t.Text))
                .Append(new ChatMessage("user", s.RedactedInput)).ToList(),
            Seed = s.Session.Seed + s.Reflections * 7919 + s.Session.Transcript.Count
        };
        LlmResponse resp;
        try { resp = await route.Model.GenerateAsync(req, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            route = new RoutingDecision(svc.Router.Fallback, $"{route.Model.Name} failed ({ex.GetType().Name}) — offline template engine");
            resp = await route.Model.GenerateAsync(req, ct).ConfigureAwait(false);
        }
        s.Draft = resp.Text;
        s.ModelUsed = resp.Model;
        s.RoutingReason = route.Reason;
        svc.Tracer.Step(s.Trace, Name, "generate",
            $"Draft written by {resp.Model} on the {resp.Site.ToString().ToLowerInvariant()} ({route.Reason}); not yet shown to the learner.",
            new Dictionary<string, string>
            {
                ["model"] = resp.Model, ["site"] = resp.Site.ToString(), ["tokens"] = resp.CompletionTokens.ToString(),
                ["tokens_per_sec"] = resp.TokensPerSecond.ToString("0.0"), ["latency_ms"] = resp.Elapsed.TotalMilliseconds.ToString("0.0")
            }, resp.Elapsed.TotalMilliseconds);
    }
}

/// <summary>
/// Localization Agent — supplies culturally resonant metaphors from the curated pack and appends
/// code-switching bridge terms in the learner's home language (Epistemic Justice, RAI principle 2).
/// </summary>
public sealed class LocalizationAgent(AgentServices svc)
{
    public const string Name = "localization";

    public Task PreGenerateAsync(TutorState s, CancellationToken ct)
    {
        if (s.Action != TutorAction.Hint || s.Session.MetaphorUsedOnItem) return Task.CompletedTask;
        if (s.ReflectionFeedback.Any(f => f.StartsWith("off_curriculum"))) return Task.CompletedTask;
        svc.ToolPolicy.Demand(Name, "glossary.read");
        var gen = s.Session.CurrentItem!;
        var meta = svc.Rag.Retrieve(gen.Item.Prompt, 1, gen.Item.SkillCode, s.Session.Language, "metaphor").FirstOrDefault()
                   ?? svc.Rag.Retrieve(gen.Item.Prompt, 1, gen.Item.SkillCode, LearnerLanguage.English, "metaphor").FirstOrDefault();
        if (meta is null) return Task.CompletedTask;
        s.PlanSlots["metaphor"] = meta.Chunk.Text;
        svc.Tracer.Step(s.Trace, Name, "metaphor",
            $"Added a {meta.Chunk.Language} cultural metaphor from the curated pack ({meta.Chunk.Id}).",
            new Dictionary<string, string> { ["chunk"] = meta.Chunk.Id, ["language"] = s.Session.Language.ToString() });
        return Task.CompletedTask;
    }

    public Task PostGenerateAsync(TutorState s, CancellationToken ct)
    {
        var lang = s.Session.Language;
        if (s.Action == TutorAction.Praise && CurriculumPack.Encouragement.TryGetValue(lang, out var enc) && lang != LearnerLanguage.English)
            s.Draft = $"{enc} {s.Draft}";
        if (lang == LearnerLanguage.English) return Task.CompletedTask;

        var lower = s.Draft.ToLowerInvariant();
        var bridges = svc.Glossary.Where(g => g.Language == lang && g.English != "market" &&
                                              System.Text.RegularExpressions.Regex.IsMatch(lower, $@"\b{g.English}\b"))
            .Take(2).Select(g => $"{g.English} = {g.Local}").ToList();
        if (bridges.Count > 0)
        {
            s.Draft += $" [{lang}: {string.Join("; ", bridges)}]";
            svc.Tracer.Step(s.Trace, Name, "code_switch", $"Added {lang} bridge terms: {string.Join("; ", bridges)}.", null);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Safety Agent — independent validator. Runs every output rail on the draft before any learner sees
/// it and drives the reflection loop (revise up to N times, then fall back to a vetted template).
/// </summary>
public sealed class SafetyAgent(AgentServices svc)
{
    public const string Name = "safety";

    public Task CheckAsync(TutorState s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        svc.ToolPolicy.Demand(Name, "guardrails.check");
        var gen = s.Session.CurrentItem!;
        var verdict = svc.Guardrails.CheckOutput(s.Draft,
            new OutputContext(gen.Item.CorrectAnswer, gen.Item.SkillCode, s.Action == TutorAction.Praise, s.Session.Language));
        s.OutputVerdict = verdict;
        if (!verdict.Allowed)
        {
            s.BlockedDrafts++;
            s.Reflections++;
            s.ReflectionFeedback.Clear();
            s.ReflectionFeedback.AddRange(verdict.Violations.Select(v => $"{v.Rule}: {v.Detail}"));
        }
        svc.Tracer.Step(s.Trace, Name, "validate",
            verdict.Allowed ? "Draft passed all safety rails (no answer leak, arithmetic verified, on-syllabus, no PII)."
                            : $"Draft BLOCKED before reaching the learner — {verdict.Explain()}.",
            new Dictionary<string, string> { ["allowed"] = verdict.Allowed.ToString(), ["violations"] = verdict.Explain() },
            sw.Elapsed.TotalMilliseconds);
        return Task.CompletedTask;
    }

    public string Route(TutorState s) =>
        s.OutputVerdict!.Allowed ? "respond" : s.Reflections <= svc.MaxReflections ? "socratic_plan" : "safe_fallback";

    public Task SafeFallbackAsync(TutorState s, CancellationToken ct)
    {
        var gen = s.Session.CurrentItem!;
        var candidates = new[]
        {
            "Let's try again step by step. " + gen.HintLadder[0],
            "Let's read the question again carefully. What is the very first step?"
        };
        foreach (var c in candidates)
        {
            var v = svc.Guardrails.CheckOutput(c, new OutputContext(gen.Item.CorrectAnswer, gen.Item.SkillCode, false, s.Session.Language));
            if (!v.Allowed) continue;
            s.Draft = c; s.OutputVerdict = v; s.UsedSafeFallback = true;
            break;
        }
        svc.Tracer.Step(s.Trace, Name, "fallback",
            $"Model drafts failed {s.Reflections} time(s); used a vetted curriculum template instead.", null, 0);
        return Task.CompletedTask;
    }

    public Task EscalateAsync(TutorState s, CancellationToken ct)
    {
        s.Action = TutorAction.Escalate;
        s.Draft = "Thank you for telling me. That is important, and you deserve help. I have asked your teacher to talk with you. " +
                  "You can also speak to a trusted adult today.";
        s.Signals.Add(new AgentSignal("safeguarding", "Possible safeguarding disclosure in tutoring session",
            new Dictionary<string, string> { ["session"] = s.Session.SessionId }));
        svc.Tracer.Step(s.Trace, Name, "escalate",
            "Input matched a safeguarding pattern. Tutoring paused and the teacher was alerted immediately (human review required).", null);
        return Task.CompletedTask;
    }

    public Task RefuseAsync(TutorState s, CancellationToken ct)
    {
        s.Action = TutorAction.Refuse;
        s.Draft = $"I'm here to help with your maths practice. Let's get back to our question: {s.Session.CurrentItem!.Item.Prompt}";
        svc.Tracer.Step(s.Trace, Name, "refuse",
            $"Input blocked by the {string.Join(", ", s.InputVerdict!.Violations.Where(v => v.Severity >= RailSeverity.Block).Select(v => v.Rule))} rail; redirected to the lesson.", null);
        return Task.CompletedTask;
    }
}
