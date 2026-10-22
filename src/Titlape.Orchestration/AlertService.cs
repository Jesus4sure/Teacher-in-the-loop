using Titlape.Context.Curriculum;
using Titlape.Context.Memory;
using Titlape.Core;
using Titlape.Data.Telemetry;
using Titlape.Governance.Audit;
using Titlape.Governance.Contracts;
using Titlape.Governance.Xai;

namespace Titlape.Orchestration;

public sealed record RiskAssessment(string Pseudonym, double Risk, double[] Features, List<FeatureAttribution> Attributions);

public sealed record AlertMetrics(
    int Total,
    int Pending,
    int ActedUpon,
    int Ignored,
    double ActedRate,
    double IgnoredRate,
    double MedianMinutesToAction,
    int DigestSuppressed,
    int ContractRejections);

/// <summary>
/// Alert synthesis for the Master Orchestrator (RQ 1.1). Turns a stream of per-turn agent signals into
/// a small number of explainable, actionable alerts: misconceptions are clustered per class/skill,
/// risk alerts carry exact Shapley attributions, safeguarding bypasses every filter, and low-priority
/// items fall into a digest so teachers with 60+ pupils are not flooded.
/// </summary>
public sealed class AlertService
{
    private readonly List<TeacherAlert> _alerts = new();
    private readonly object _gate = new();
    private readonly StudentMemory _memory;
    private readonly InteractionLog _log;
    private readonly AuditLog _audit;
    private readonly LearningRiskModel _risk = new();
    private readonly Dictionary<string, (HashSet<string> Learners, List<string> Traces)> _clusters = new();
    private readonly Dictionary<string, int> _flagCounts = new();
    private int _digestSuppressed, _contractRejections;

    public double RiskThreshold { get; set; } = 0.7;
    public int ClusterActThreshold { get; set; } = 3;
    public double ClusterActShare { get; set; } = 0.3;
    public int MaxActiveAlertsPerClass { get; set; } = 8;
    public TimeSpan IgnoreWindow { get; set; } = TimeSpan.FromHours(24);
    public LearningRiskModel RiskModel => _risk;

    public AlertService(StudentMemory memory, InteractionLog log, AuditLog audit)
    {
        _memory = memory; _log = log; _audit = audit;
    }

    public event Action<TeacherAlert>? Raised;

    public IReadOnlyList<TeacherAlert> ForClass(string classId)
    {
        lock (_gate) return _alerts.Where(a => a.ClassId == classId)
            .OrderBy(a => a.Status == AlertStatus.Pending ? 0 : 1).ThenByDescending(a => a.Severity).ThenByDescending(a => a.CreatedAt).ToList();
    }

    public TeacherAlert? Get(string id) { lock (_gate) return _alerts.FirstOrDefault(a => a.Id == id); }

    public TeacherAlert Safeguarding(string classId, string pseudonym, string traceId)
    {
        var a = new TeacherAlert
        {
            ClassId = classId, Title = "Safeguarding: a learner may need to talk to you",
            Summary = "A message in an after-school session matched a safeguarding pattern. Tutoring was paused. Please speak with the learner privately today and follow your school's child-protection procedure.",
            Severity = AlertSeverity.Urgent, StudentIds = { pseudonym }, EvidenceTraceIds = { traceId },
            RecommendedAction = "Speak with the learner privately; follow the child-protection procedure."
        };
        Add(a, bypassCap: true);
        return a;
    }

    public void Misconception(string classId, string skill, string code, string pseudonym, string traceId, int classSize = 0)
    {
        var key = $"{classId}|{skill}|{code}";
        TeacherAlert? existing;
        lock (_gate)
        {
            if (!_clusters.TryGetValue(key, out var c)) _clusters[key] = c = (new HashSet<string>(), new List<string>());
            c.Learners.Add(pseudonym);
            c.Traces.Add(traceId);
            existing = _alerts.FirstOrDefault(a => a.Status == AlertStatus.Pending && a.ClassId == classId
                                                   && a.SkillCode == skill && a.Title.Contains(CurriculumPack.MisconceptionNames[code]));
            if (c.Learners.Count < 2) { _digestSuppressed++; return; }
        }

        var cluster = _clusters[key];
        var learners = cluster.Learners.ToList();
        // Whole-class reteach is recommended once a meaningful share of the class shows the same error.
        int actAt = Math.Max(ClusterActThreshold, (int)Math.Ceiling(classSize * ClusterActShare));
        var digest = new TeacherDigest(
            $"{learners.Count} learners show the same error",
            skill, learners.Count, learners, cluster.Traces.TakeLast(5).ToList(),
            learners.Count >= actAt
                ? "Reteach this step to the whole group tomorrow (5 minutes), then assign targeted practice."
                : "Watch: check these learners' work in class.",
            Math.Min(1, 0.5 + 0.1 * learners.Count));
        var facts = new DigestFacts(skill, cluster.Learners, new HashSet<string>(cluster.Traces), new HashSet<long> { learners.Count });
        var contract = DigestContract.Validate(digest, facts);
        if (!contract.Valid)
        {
            lock (_gate) _contractRejections++;
            _audit.Append("orchestrator", "digest.rejected", key, string.Join("; ", contract.Errors));
            return;
        }

        var severity = learners.Count >= actAt ? AlertSeverity.Act : AlertSeverity.Watch;
        var alert = new TeacherAlert
        {
            ClassId = classId,
            Title = $"Common error: {CurriculumPack.MisconceptionNames[code]}",
            Summary = $"{digest.Headline} — {CurriculumPack.Skill(skill).Title}. Each case is linked to its diagnostic trace.",
            Severity = severity, SkillCode = skill, StudentIds = learners, EvidenceTraceIds = digest.EvidenceTraceIds.ToList(),
            RecommendedAction = digest.RecommendedAction
        };
        lock (_gate)
        {
            if (existing is not null) _alerts.Remove(existing); // superseded by the updated cluster alert
        }
        Add(alert, bypassCap: false);
    }

    public void InputFlag(string classId, string pseudonym, string rule, string traceId)
    {
        int n;
        lock (_gate) n = _flagCounts[$"{pseudonym}|{rule}"] = _flagCounts.GetValueOrDefault($"{pseudonym}|{rule}") + 1;
        if (n == 3)
            Add(new TeacherAlert
            {
                ClassId = classId, Title = $"Repeated {rule.Replace('_', ' ')} attempts",
                Summary = $"A learner triggered the {rule.Replace('_', ' ')} rail {n} times. The tutor redirected each time; you may want to check in.",
                Severity = AlertSeverity.Watch, StudentIds = { pseudonym }, EvidenceTraceIds = { traceId },
                RecommendedAction = "Check in with the learner about using the tutor for practice."
            }, bypassCap: false);
        else lock (_gate) _digestSuppressed++;
    }

    public RiskAssessment AssessRisk(string pseudonym, string classId, IReadOnlyList<string> classPseudonyms, IReadOnlyList<string> pathway, string? currentSkill)
    {
        var x = Features(pseudonym, pathway, currentSkill);
        var others = classPseudonyms.Select(p => Features(p, pathway, currentSkill)).ToList();
        var baseline = Enumerable.Range(0, x.Length).Select(i => others.Count == 0 ? 0 : others.Average(o => o[i])).ToArray();
        var risk = _risk.Predict(x);
        return new RiskAssessment(pseudonym, risk, x, ShapleyExplainer.ExplainRisk(_risk, x, baseline));
    }

    public TeacherAlert? MaybeRiskAlert(RiskAssessment ra, string classId, string traceId, string? proposalId, string? skill)
    {
        if (ra.Risk < RiskThreshold) return null;
        lock (_gate)
            if (_alerts.Any(a => a.Status == AlertStatus.Pending && a.Title.StartsWith("At risk") && a.StudentIds.Contains(ra.Pseudonym)))
                return null;
        var top = ra.Attributions.Where(a => a.Contribution > 0).Take(3).ToList();
        var alert = new TeacherAlert
        {
            ClassId = classId,
            Title = $"At risk on {skill ?? "current skills"}",
            Summary = $"Estimated risk of falling behind: {ra.Risk:P0}. Main reasons: " + string.Join("; ", top.Select(t => t.PlainLanguage)) + ".",
            Severity = AlertSeverity.Act,
            StudentIds = { ra.Pseudonym }, SkillCode = skill, Attributions = ra.Attributions, EvidenceTraceIds = { traceId },
            ProposalId = proposalId,
            RecommendedAction = proposalId is not null ? "Review the proposed pathway change (approve, modify or dismiss)." : "Check in during class; consider pairing with a peer."
        };
        Add(alert, bypassCap: false);
        return alert;
    }

    public TeacherAlert Decide(string alertId, string teacherId, AlertStatus decision, string? note)
    {
        TeacherAlert a;
        lock (_gate)
        {
            a = _alerts.First(x => x.Id == alertId);
            a.Status = decision;
            a.ActedAt = DateTimeOffset.UtcNow;
        }
        _audit.Append(teacherId, $"alert.{decision.ToString().ToLowerInvariant()}", alertId, note ?? a.Title);
        return a;
    }

    private readonly List<(string AlertId, int Score)> _trust = new();

    /// <summary>Teacher rates how useful an alert's explanation was (1–5): the RQ 2.1 trust instrument.</summary>
    public void RecordTrust(string alertId, string teacherId, int score)
    {
        if (score is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(score));
        lock (_gate) _trust.Add((alertId, score));
        _audit.Append(teacherId, "alert.trust_rating", alertId, score.ToString());
    }

    public (double Mean, int N) TrustScore()
    {
        lock (_gate) return (_trust.Count == 0 ? 0 : _trust.Average(t => t.Score), _trust.Count);
    }

    public AlertMetrics Metrics(string? classId = null, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var list = _alerts.Where(a => classId is null || a.ClassId == classId).ToList();
            int acted = list.Count(a => a.Status != AlertStatus.Pending);
            int ignored = list.Count(a => a.Status == AlertStatus.Pending && t - a.CreatedAt > IgnoreWindow);
            var mins = list.Where(a => a.ActedAt is not null).Select(a => (a.ActedAt!.Value - a.CreatedAt).TotalMinutes).OrderBy(x => x).ToList();
            return new AlertMetrics(list.Count, list.Count - acted, acted, ignored,
                list.Count == 0 ? 0 : acted / (double)list.Count,
                list.Count == 0 ? 0 : ignored / (double)list.Count,
                mins.Count == 0 ? 0 : mins[mins.Count / 2], _digestSuppressed, _contractRejections);
        }
    }

    private void Add(TeacherAlert alert, bool bypassCap)
    {
        lock (_gate)
        {
            if (!bypassCap)
            {
                var active = _alerts.Where(a => a.ClassId == alert.ClassId && a.Status == AlertStatus.Pending).ToList();
                if (active.Count >= MaxActiveAlertsPerClass)
                {
                    var weakest = active.Where(a => a.Severity < AlertSeverity.Urgent).OrderBy(a => a.Severity).ThenBy(a => a.CreatedAt).FirstOrDefault();
                    if (weakest is null || weakest.Severity > alert.Severity) { _digestSuppressed++; return; }
                    _alerts.Remove(weakest);
                    _digestSuppressed++;
                }
            }
            _alerts.Add(alert);
        }
        _audit.Append("orchestrator", "alert.raised", alert.Id, $"{alert.Severity}: {alert.Title}");
        Raised?.Invoke(alert);
    }

    private double[] Features(string pseudonym, IReadOnlyList<string> pathway, string? currentSkill)
    {
        var mastery = _memory.All(pseudonym, pathway).ToList();
        var attempted = mastery.Where(m => m.Attempts > 0).ToList();
        var cur = currentSkill is null ? null : mastery.FirstOrDefault(m => m.SkillCode == currentSkill);
        double overallGap = attempted.Count == 0 ? 0.5 : 1 - attempted.Average(m => m.PKnown);
        double currentGap = cur is { Attempts: > 0 } ? 1 - cur.PKnown : overallGap;
        double gap = 0.7 * currentGap + 0.3 * overallGap;
        double streak = Math.Min(1, (cur?.CurrentErrorStreak ?? 0) / 5.0);
        int attempts = mastery.Sum(m => m.Attempts), hints = mastery.Sum(m => m.HintsUsed);
        double hintRel = attempts == 0 ? 0 : Math.Min(1, hints / (double)attempts / 3.0);
        var mc = _memory.Misconceptions(pseudonym);
        double recur = mc.Count == 0 ? 0 : Math.Min(1, mc.Values.Max() / 5.0);
        var events = _log.Events(pseudonym: pseudonym);
        var recent = events.TakeLast(10).ToList();
        double rt = recent.Count == 0 ? 0.3 : Math.Min(1, recent.Average(e => e.ResponseSeconds) / 60.0);
        double gapDays = events.Count == 0 ? 0.3 : Math.Min(1, (DateTimeOffset.UtcNow - events[^1].At).TotalDays / 7.0);
        return new[] { gap, streak, hintRel, recur, rt, gapDays };
    }
}
