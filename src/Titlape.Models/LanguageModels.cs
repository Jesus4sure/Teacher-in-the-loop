using System.Diagnostics;
using System.Runtime.CompilerServices;
using Titlape.Core;
using Titlape.CrossCutting;

namespace Titlape.Models;

public sealed record ChatMessage(string Role, string Content);

public sealed class LlmRequest
{
    public required string Task { get; init; }
    public required string SystemPrompt { get; init; }
    public List<ChatMessage> Messages { get; init; } = new();
    public Dictionary<string, string> Slots { get; init; } = new();
    public int MaxTokens { get; init; } = 160;
    public double Temperature { get; init; } = 0.3;
    public int? Seed { get; init; }
}

public sealed record LlmResponse(
    string Text,
    string Model,
    ExecutionSite Site,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Elapsed)
{
    public double TokensPerSecond => Elapsed.TotalSeconds <= 0 ? CompletionTokens * 1000.0 : CompletionTokens / Elapsed.TotalSeconds;
}

/// <summary>Layer 2 inference-engine contract shared by edge SLMs and cloud endpoints.</summary>
public interface ILanguageModel
{
    string Name { get; }
    ExecutionSite Site { get; }
    string Quantization { get; }
    bool IsAvailable { get; }
    Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default);
}

/// <summary>Rolling inference statistics per engine (RQ 4.1: target ≥ 15 tokens/sec on edge).</summary>
public sealed class InferenceStats
{
    private readonly Queue<LlmResponse> _window = new();
    private readonly object _gate = new();
    public int WindowSize { get; init; } = 200;
    public long TotalCalls { get; private set; }

    public void Record(LlmResponse r)
    {
        lock (_gate)
        {
            TotalCalls++;
            _window.Enqueue(r);
            while (_window.Count > WindowSize) _window.Dequeue();
        }
        TitlapeTelemetry.InferenceTokensPerSecond.Record(r.TokensPerSecond,
            new KeyValuePair<string, object?>("model", r.Model), new KeyValuePair<string, object?>("site", r.Site.ToString()));
    }

    public (double MeanTokensPerSecond, double P50LatencyMs, double P95LatencyMs, int Samples) Summary()
    {
        lock (_gate)
        {
            if (_window.Count == 0) return (0, 0, 0, 0);
            var lat = _window.Select(r => r.Elapsed.TotalMilliseconds).OrderBy(x => x).ToArray();
            var totalTok = _window.Sum(r => r.CompletionTokens);
            var totalSec = _window.Sum(r => r.Elapsed.TotalSeconds);
            return (totalSec > 0 ? totalTok / totalSec : 0, lat[lat.Length / 2], lat[(int)Math.Min(lat.Length - 1, Math.Ceiling(lat.Length * 0.95) - 1)], lat.Length);
        }
    }
}

/// <summary>Base class that adds timing and tracing around any engine.</summary>
public abstract class InstrumentedModel : ILanguageModel
{
    public InferenceStats Stats { get; } = new();
    public abstract string Name { get; }
    public abstract ExecutionSite Site { get; }
    public abstract string Quantization { get; }
    public abstract bool IsAvailable { get; }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        using var act = TitlapeTelemetry.Source.StartActivity($"llm.{request.Task}");
        act?.SetTag(TitlapeTelemetry.SpanKind, "LLM");
        act?.SetTag(TitlapeTelemetry.LlmModel, Name);
        var sw = Stopwatch.StartNew();
        var text = await GenerateCoreAsync(request, ct).ConfigureAwait(false);
        sw.Stop();
        var promptTok = TextUtil.ApproxTokenCount(request.SystemPrompt + string.Concat(request.Messages.Select(m => m.Content)));
        var resp = new LlmResponse(text, Name, Site, promptTok, TextUtil.ApproxTokenCount(text), sw.Elapsed);
        Stats.Record(resp);
        act?.SetTag(TitlapeTelemetry.LlmTokensPrompt, resp.PromptTokens);
        act?.SetTag(TitlapeTelemetry.LlmTokensCompletion, resp.CompletionTokens);
        return resp;
    }

    public virtual async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await GenerateAsync(request, ct).ConfigureAwait(false);
        foreach (var chunk in ChunkWords(r.Text)) { ct.ThrowIfCancellationRequested(); yield return chunk; }
    }

    protected abstract Task<string> GenerateCoreAsync(LlmRequest request, CancellationToken ct);

    public static IEnumerable<string> ChunkWords(string text)
    {
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++) yield return i == 0 ? words[i] : " " + words[i];
    }
}
