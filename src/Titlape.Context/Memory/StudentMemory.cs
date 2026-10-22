using System.Text.Json;
using Titlape.Core;
using Titlape.Data.Sync;

namespace Titlape.Context.Memory;

/// <summary>Bayesian Knowledge Tracing parameters for one skill.</summary>
public sealed record BktParameters(double PInit = 0.15, double PTransit = 0.1, double PSlip = 0.12, double PGuess = 0.15);

public sealed record SkillMastery(
    string SkillCode,
    double PKnown,
    int Attempts,
    int Correct,
    int CurrentErrorStreak,
    int HintsUsed,
    string? LastMisconception,
    DateTimeOffset UpdatedAt)
{
    public bool Mastered => PKnown >= StudentMemory.MasteryThreshold && Attempts >= 4;
}

/// <summary>
/// Student Memory service (Layer 4). Maintains a per-skill mastery estimate using Bayesian Knowledge
/// Tracing — an interpretable model whose every update can be shown to a teacher. Keyed only by learner
/// pseudonym, and persisted in the CRDT replica so it survives multi-day outages and syncs later.
/// </summary>
public sealed class StudentMemory
{
    public const double MasteryThreshold = 0.85;
    private readonly ReplicaStore _store;
    private readonly Func<string, BktParameters> _params;
    private const string Collection = "mastery";

    public StudentMemory(ReplicaStore store, Func<string, BktParameters>? parameters = null)
    {
        _store = store;
        _params = parameters ?? (_ => new BktParameters());
    }

    public SkillMastery Get(string pseudonym, string skill)
    {
        var json = _store.Get(Collection, pseudonym)?.Get(skill);
        return json is null
            ? new SkillMastery(skill, _params(skill).PInit, 0, 0, 0, 0, null, DateTimeOffset.MinValue)
            : JsonSerializer.Deserialize<SkillMastery>(json)!;
    }

    public IReadOnlyList<SkillMastery> All(string pseudonym, IEnumerable<string> skills) =>
        skills.Select(s => Get(pseudonym, s)).ToList();

    /// <summary>
    /// BKT update. Scaffolded (hinted) correct answers are weaker evidence of mastery, modelled by
    /// raising the effective guess probability with each hint used.
    /// </summary>
    public (SkillMastery Before, SkillMastery After) Observe(string pseudonym, string skill, bool correct, int hintsUsed, string? misconception)
    {
        var before = Get(pseudonym, skill);
        var p = _params(skill);
        double guess = Math.Min(0.65, p.PGuess + 0.2 * hintsUsed);
        double prior = before.PKnown;
        double posterior = correct
            ? prior * (1 - p.PSlip) / (prior * (1 - p.PSlip) + (1 - prior) * guess)
            : prior * p.PSlip / (prior * p.PSlip + (1 - prior) * (1 - guess));
        double next = posterior + (1 - posterior) * p.PTransit;
        var after = before with
        {
            PKnown = Math.Round(next, 4),
            Attempts = before.Attempts + 1,
            Correct = before.Correct + (correct ? 1 : 0),
            CurrentErrorStreak = correct ? 0 : before.CurrentErrorStreak + 1,
            HintsUsed = before.HintsUsed + hintsUsed,
            LastMisconception = misconception ?? (correct ? null : before.LastMisconception),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _store.PutJson(Collection, pseudonym, skill, after);
        if (misconception is not null) _store.Increment("misconceptions", pseudonym, misconception);
        if (after.Mastered) _store.AddToSet("achievements", pseudonym, "mastered", skill);
        return (before, after);
    }

    public IReadOnlyDictionary<string, long> Misconceptions(string pseudonym)
    {
        var d = _store.Get("misconceptions", pseudonym);
        return d is null ? new Dictionary<string, long>() : d.Counters.ToDictionary(k => k.Key, v => v.Value.Value);
    }

    public IReadOnlyList<string> MasteredSkills(string pseudonym) =>
        _store.Get("achievements", pseudonym)?.Set("mastered").ToList() ?? new List<string>();
}

/// <summary>Teacher-approved learning pathways and agent proposals (Human Sovereignty, RAI principle 1).</summary>
public sealed class PathwayService
{
    private readonly Dictionary<string, LearningPathway> _pathways = new();
    private readonly Dictionary<string, PathwayProposal> _proposals = new();
    private readonly object _gate = new();

    public LearningPathway GetOrCreate(string studentId, IEnumerable<string> defaultSequence)
    {
        lock (_gate)
        {
            if (!_pathways.TryGetValue(studentId, out var p))
                _pathways[studentId] = p = new LearningPathway { StudentId = studentId, SkillSequence = defaultSequence.ToList() };
            return p;
        }
    }

    public LearningPathway? Get(string studentId) { lock (_gate) return _pathways.GetValueOrDefault(studentId); }

    /// <summary>Agents can only propose. Nothing changes until a teacher approves.</summary>
    public PathwayProposal Propose(string studentId, string agent, string rationale, List<string> sequence)
    {
        lock (_gate)
        {
            var existing = _proposals.Values.FirstOrDefault(p => p.StudentId == studentId && p.Status == AlertStatus.Pending
                                                                && p.ProposedSequence.SequenceEqual(sequence));
            if (existing is not null) return existing;
            var proposal = new PathwayProposal { StudentId = studentId, ProposedBy = agent, Rationale = rationale, ProposedSequence = sequence };
            _proposals[proposal.Id] = proposal;
            return proposal;
        }
    }

    public IReadOnlyList<PathwayProposal> Proposals(Func<PathwayProposal, bool>? filter = null)
    {
        lock (_gate) return _proposals.Values.Where(filter ?? (_ => true)).OrderByDescending(p => p.CreatedAt).ToList();
    }

    public PathwayProposal? GetProposal(string id) { lock (_gate) return _proposals.GetValueOrDefault(id); }

    public PathwayProposal Decide(string proposalId, string teacherId, bool approve, List<string>? modifiedSequence, string? note)
    {
        lock (_gate)
        {
            var p = _proposals[proposalId];
            if (p.Status != AlertStatus.Pending) throw new InvalidOperationException("Proposal already decided.");
            p.DecidedBy = teacherId;
            p.TeacherNote = note;
            if (approve || modifiedSequence is not null)
            {
                var path = _pathways[p.StudentId];
                path.SkillSequence.Clear();
                path.SkillSequence.AddRange(modifiedSequence ?? p.ProposedSequence);
                path.CurrentIndex = 0;
                path.ApprovedBy = teacherId;
                path.ApprovedAt = DateTimeOffset.UtcNow;
                p.Status = modifiedSequence is not null ? AlertStatus.Overridden : AlertStatus.Approved;
            }
            else p.Status = AlertStatus.Dismissed;
            return p;
        }
    }

    /// <summary>Direct teacher override of a pathway (no agent proposal needed).</summary>
    public LearningPathway Override(string studentId, string teacherId, List<string> sequence)
    {
        lock (_gate)
        {
            var path = _pathways[studentId];
            path.SkillSequence.Clear();
            path.SkillSequence.AddRange(sequence);
            path.CurrentIndex = 0;
            path.ApprovedBy = teacherId;
            path.ApprovedAt = DateTimeOffset.UtcNow;
            return path;
        }
    }
}
