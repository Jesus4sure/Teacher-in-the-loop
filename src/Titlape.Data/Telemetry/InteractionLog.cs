using System.Text.Json;
using Titlape.Data.Privacy;
using Titlape.Data.Sync;

namespace Titlape.Data.Telemetry;

/// <summary>An anonymised learning event. Contains a pseudonym only — never a name or contact detail.</summary>
public sealed record InteractionEvent(
    string EventId,
    string Pseudonym,
    string ClassId,
    string SkillCode,
    string ItemId,
    bool Correct,
    int HintsUsed,
    double ResponseSeconds,
    string? Misconception,
    string Language,
    DateTimeOffset At);

/// <summary>
/// Relational-style telemetry store on top of the CRDT replica (so telemetry syncs offline).
/// Implements zero-retention for dialogue text: only redacted, consented text is stored, and raw turns
/// are held in memory for the live session only.
/// </summary>
public sealed class InteractionLog
{
    private readonly ReplicaStore _store;
    private readonly PiiGateway _pii;
    private const string Collection = "interactions";

    public InteractionLog(ReplicaStore store, PiiGateway pii)
    {
        _store = store; _pii = pii;
    }

    public void Record(InteractionEvent e)
    {
        _store.PutJson(Collection, e.EventId, "event", e);
        _store.Increment("class-stats", e.ClassId, e.Correct ? "correct" : "incorrect");
        _store.Increment("learner-stats", e.Pseudonym, "attempts");
        if (e.HintsUsed > 0) _store.Increment("learner-stats", e.Pseudonym, "hints", e.HintsUsed);
    }

    /// <summary>Stores a redacted transcript excerpt for teacher review, only with guardian consent.</summary>
    public RedactionResult? StoreTranscript(string pseudonym, string sessionId, int turn, string text, bool consent)
    {
        if (!consent) return null;
        var redacted = _pii.Redact(text);
        _store.Put("transcripts", $"{sessionId}-{turn:D4}", "text", redacted.Text);
        _store.Put("transcripts", $"{sessionId}-{turn:D4}", "learner", pseudonym);
        return redacted;
    }

    public IReadOnlyList<InteractionEvent> Events(string? classId = null, string? pseudonym = null) =>
        _store.Query(Collection)
            .Select(d => d.Get("event"))
            .Where(j => j is not null)
            .Select(j => JsonSerializer.Deserialize<InteractionEvent>(j!)!)
            .Where(e => (classId is null || e.ClassId == classId) && (pseudonym is null || e.Pseudonym == pseudonym))
            .OrderBy(e => e.At)
            .ToList();
}
