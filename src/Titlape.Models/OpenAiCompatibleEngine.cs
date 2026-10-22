using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Titlape.Core;

namespace Titlape.Models;

public sealed class OpenAiCompatibleOptions
{
    /// <summary>e.g. http://127.0.0.1:8080 for llama.cpp's llama-server, or http://gpu-host:8000 for vLLM.</summary>
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "phi-3-mini-4k-instruct-q4_k_m";
    public string? ApiKey { get; set; }
    public ExecutionSite Site { get; set; } = ExecutionSite.Edge;
    public string Quantization { get; set; } = "Q4_K_M (4-bit GGUF)";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Adapter for any OpenAI-compatible chat-completions server. Both llama.cpp (<c>llama-server</c>, edge,
/// 4-bit GGUF) and vLLM (cloud GPU) expose this API, so one adapter serves both Layer 2 engines.
/// Streaming uses Server-Sent Events.
/// </summary>
public sealed class OpenAiCompatibleEngine : InstrumentedModel
{
    private readonly HttpClient _http;
    private readonly OpenAiCompatibleOptions _opt;
    private readonly ConnectivityState? _net;
    private volatile bool _healthy = true;

    public OpenAiCompatibleEngine(OpenAiCompatibleOptions options, ConnectivityState? cloudConnectivity = null, HttpClient? http = null)
    {
        _opt = options;
        _net = options.Site == ExecutionSite.Cloud ? cloudConnectivity : null;
        _http = http ?? new HttpClient { Timeout = options.Timeout };
        _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", options.ApiKey);
    }

    public override string Name => _opt.Model;
    public override ExecutionSite Site => _opt.Site;
    public override string Quantization => _opt.Quantization;
    public override bool IsAvailable => _healthy && (_net?.IsOnline ?? true);

    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync("v1/models", ct).ConfigureAwait(false);
            _healthy = r.IsSuccessStatusCode;
        }
        catch { _healthy = false; }
        return _healthy;
    }

    protected override async Task<string> GenerateCoreAsync(LlmRequest request, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("v1/chat/completions", Body(request, stream: false), ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
            return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _healthy = false;
            throw;
        }
    }

    public override async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(Body(request, stream: true)), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            var delta = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    private object Body(LlmRequest r, bool stream)
    {
        // The plan in Slots is appended to the system prompt so a real model receives the same
        // structured guidance as the offline template engine.
        var plan = new StringBuilder(r.SystemPrompt);
        foreach (var (k, v) in r.Slots)
            if (k != "reference_answer") plan.Append($"\n[{k}] {v}");
        if (r.Slots.TryGetValue("reference_answer", out var ans))
            plan.Append($"\n[reference_answer — NEVER reveal or confirm this to the learner] {ans}");

        var messages = new List<object> { new { role = "system", content = plan.ToString() } };
        messages.AddRange(r.Messages.Select(m => new { role = m.Role, content = m.Content }));
        return new
        {
            model = _opt.Model,
            messages,
            max_tokens = r.MaxTokens,
            temperature = r.Temperature,
            seed = r.Seed,
            stream
        };
    }
}
