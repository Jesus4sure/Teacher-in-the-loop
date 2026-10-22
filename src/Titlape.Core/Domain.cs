namespace Titlape.Core;

/// <summary>Educational tiers described in Section 1 of the framework.</summary>
public enum EducationTier { Primary, Secondary, Tvet, Tertiary }

/// <summary>Languages supported for code-switching (RAI principle 2: Epistemic Justice).</summary>
public enum LearnerLanguage { English, Yoruba, Hausa, Swahili, Amharic }

public enum Role { Student, Teacher, Admin, Guardian, Agent }

/// <summary>Where an inference or session is executed (Edge-Cloud hybrid, adaptation #1).</summary>
public enum ExecutionSite { Edge, Cloud }

public enum AlertSeverity { Info = 0, Watch = 1, Act = 2, Urgent = 3 }

public enum AlertStatus { Pending, Acknowledged, Approved, Overridden, Dismissed }

public sealed record School(
    string Id,
    string Name,
    string Country,
    string Region,
    bool HasGridPower,
    bool HasSolarHub,
    string EdgeNodeId);

public sealed record Teacher(string Id, string Name, string SchoolId, IReadOnlyList<string> ClassIds);

public sealed record ClassRoom(string Id, string SchoolId, string Name, EducationTier Tier, int GradeLevel, string TeacherId);

/// <summary>
/// A learner. Personally identifiable fields (name, guardian phone) never leave the Data layer
/// un-redacted: everything above Layer 1 sees only <see cref="PseudonymId"/>.
/// </summary>
public sealed class Student
{
    public required string Id { get; init; }
    public required string PseudonymId { get; init; }
    public required string FullName { get; init; }
    public required string ClassId { get; init; }
    public required string SchoolId { get; init; }
    public LearnerLanguage HomeLanguage { get; set; } = LearnerLanguage.English;
    public string Gender { get; init; } = "unspecified";
    public string? GuardianPhone { get; init; }
    public bool GuardianConsent { get; set; }
}

/// <summary>A national-syllabus learning objective (skill) used by diagnosis and RAG.</summary>
public sealed record Skill(
    string Code,
    string Subject,
    string Title,
    int GradeLevel,
    IReadOnlyList<string> Prerequisites);

/// <summary>A practice item delivered during after-school sessions.</summary>
public sealed record PracticeItem(
    string Id,
    string SkillCode,
    string Prompt,
    string CorrectAnswer,
    IReadOnlyDictionary<string, string> KnownMisconceptions,
    int Difficulty);

/// <summary>Teacher-approved learning pathway for a student. Agents may only propose changes.</summary>
public sealed class LearningPathway
{
    public required string StudentId { get; init; }
    public List<string> SkillSequence { get; init; } = new();
    public int CurrentIndex { get; set; }
    public string ApprovedBy { get; set; } = "curriculum-default";
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CurrentSkill => CurrentIndex < SkillSequence.Count ? SkillSequence[CurrentIndex] : null;
}

/// <summary>A change to a pathway proposed by an agent and awaiting a teacher decision (Human Sovereignty).</summary>
public sealed class PathwayProposal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public required string StudentId { get; init; }
    public required string ProposedBy { get; init; }
    public required string Rationale { get; init; }
    public required List<string> ProposedSequence { get; init; }
    public AlertStatus Status { get; set; } = AlertStatus.Pending;
    public string? DecidedBy { get; set; }
    public string? TeacherNote { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One exchange in a tutoring session.</summary>
public sealed record TutorTurn(
    string SessionId,
    int TurnIndex,
    string Speaker,
    string Text,
    DateTimeOffset At,
    string? TraceId = null);

public sealed class TeacherAlert
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public required string ClassId { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required AlertSeverity Severity { get; init; }
    public List<string> StudentIds { get; init; } = new();
    public string? SkillCode { get; init; }
    public List<FeatureAttribution> Attributions { get; init; } = new();
    public List<string> EvidenceTraceIds { get; init; } = new();
    public string? RecommendedAction { get; init; }
    public string? ProposalId { get; init; }
    public AlertStatus Status { get; set; } = AlertStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActedAt { get; set; }
}

/// <summary>SHAP-style contribution of one feature to a risk score.</summary>
public sealed record FeatureAttribution(string Feature, double Value, double Contribution, string PlainLanguage);
