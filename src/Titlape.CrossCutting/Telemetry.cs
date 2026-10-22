using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Titlape.CrossCutting;

/// <summary>
/// OpenTelemetry-compatible instrumentation sources. System.Diagnostics.ActivitySource is the .NET
/// implementation of the OpenTelemetry tracing API, so any OTLP exporter (e.g. Arize Phoenix) can be
/// attached at deployment time without code changes.
/// </summary>
public static class TitlapeTelemetry
{
    public const string SourceName = "Titlape";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    public static readonly Counter<long> GuardrailBlocks = Meter.CreateCounter<long>("titlape.guardrail.blocks");
    public static readonly Histogram<double> InferenceTokensPerSecond = Meter.CreateHistogram<double>("titlape.inference.tokens_per_second");
    public static readonly Histogram<int> SyncPayloadBytes = Meter.CreateHistogram<int>("titlape.sync.payload_bytes");

    // OpenInference semantic-convention attribute keys
    public const string SpanKind = "openinference.span.kind";
    public const string InputValue = "input.value";
    public const string OutputValue = "output.value";
    public const string LlmModel = "llm.model_name";
    public const string LlmTokensPrompt = "llm.token_count.prompt";
    public const string LlmTokensCompletion = "llm.token_count.completion";
    public const string RetrievalDocs = "retrieval.documents";
    public const string AgentName = "agent.name";
}
