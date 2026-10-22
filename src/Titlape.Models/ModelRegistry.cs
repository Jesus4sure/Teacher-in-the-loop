using System.Security.Cryptography;
using Titlape.Core;

namespace Titlape.Models;

public sealed record ModelManifest(
    string Name,
    string Family,
    string Parameters,
    string Quantization,
    string Format,
    string Sha256,
    double MinRamGb,
    ExecutionSite Site);

public sealed record ModelVerification(string Name, bool Verified, string Detail, DateTimeOffset At);

/// <summary>
/// Model Signature Verification (Layer 2 security pillar): a model may only be loaded if the SHA-256
/// digest of its artifact matches the signed manifest distributed with the curriculum pack. Prevents
/// tampered or swapped weights on physically exposed edge hardware.
/// </summary>
public sealed class ModelRegistry
{
    private readonly Dictionary<string, ModelManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelVerification> _verifications = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ModelManifest> Manifests => _manifests.Values;
    public IReadOnlyCollection<ModelVerification> Verifications => _verifications.Values;

    public void Register(ModelManifest manifest) => _manifests[manifest.Name] = manifest;

    public bool IsVerified(string name) => _verifications.TryGetValue(name, out var v) && v.Verified;

    public async Task<ModelVerification> VerifyFileAsync(string name, string path, CancellationToken ct = default)
    {
        if (!_manifests.TryGetValue(name, out var m)) return Store(name, false, "no signed manifest for model");
        if (!File.Exists(path)) return Store(name, false, $"artifact not found: {path}");
        await using var fs = File.OpenRead(path);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        return digest == m.Sha256.ToLowerInvariant()
            ? Store(name, true, "sha256 matches signed manifest")
            : Store(name, false, $"sha256 mismatch (expected {m.Sha256[..12]}…, got {digest[..12]}…)");
    }

    public ModelVerification VerifyDigest(string name, string actualSha256)
    {
        if (!_manifests.TryGetValue(name, out var m)) return Store(name, false, "no signed manifest for model");
        return string.Equals(m.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase)
            ? Store(name, true, "sha256 matches signed manifest")
            : Store(name, false, "sha256 mismatch");
    }

    /// <summary>Reference catalogue from Section 4 of the framework (digests filled in per deployment).</summary>
    public static IEnumerable<ModelManifest> ReferenceCatalogue() => new[]
    {
        new ModelManifest("phi-3-mini-4k-instruct-q4_k_m", "Phi-3-mini", "3.8B", "Q4_K_M", "GGUF", "", 4, ExecutionSite.Edge),
        new ModelManifest("qwen2.5-7b-instruct-q4_k_m", "Qwen-2.5", "7B", "Q4_K_M", "GGUF", "", 8, ExecutionSite.Edge),
        new ModelManifest("llama-3.1-8b-instruct-q4_k_m", "Llama-3.1", "8B", "Q4_K_M", "GGUF", "", 8, ExecutionSite.Edge),
        new ModelManifest("llama-3.1-8b-instruct", "Llama-3.1", "8B", "BF16", "safetensors (vLLM)", "", 24, ExecutionSite.Cloud),
    };

    private ModelVerification Store(string name, bool ok, string detail) =>
        _verifications[name] = new ModelVerification(name, ok, detail, DateTimeOffset.UtcNow);
}

public sealed record RoutingDecision(ILanguageModel Model, string Reason);

/// <summary>
/// Edge-first routing (adaptation #1): run locally whenever an edge engine is available; escalate to
/// the cloud only when online, permitted, and either the edge is unavailable or the solar battery is low.
/// Only signature-verified engines are eligible.
/// </summary>
public sealed class ModelRouter
{
    private readonly List<ILanguageModel> _engines = new();
    private readonly ModelRegistry _registry;
    private readonly PowerState _power;
    private readonly ILanguageModel _fallback;

    public ModelRouter(ModelRegistry registry, PowerState power, ILanguageModel fallback)
    {
        _registry = registry; _power = power; _fallback = fallback;
    }

    public bool AllowCloud { get; set; } = true;
    public IReadOnlyList<ILanguageModel> Engines => _engines;
    /// <summary>Installed engines plus the always-available offline fallback.</summary>
    public IEnumerable<ILanguageModel> AllEngines => _engines.Append(_fallback).Distinct();
    public ILanguageModel Fallback => _fallback;

    public void Add(ILanguageModel engine) => _engines.Add(engine);

    public RoutingDecision Route(string task)
    {
        bool Eligible(ILanguageModel m) => m.IsAvailable && _registry.IsVerified(m.Name);

        var edge = _engines.FirstOrDefault(m => m.Site == ExecutionSite.Edge && Eligible(m));
        var cloud = AllowCloud ? _engines.FirstOrDefault(m => m.Site == ExecutionSite.Cloud && Eligible(m)) : null;

        if (_power.LowPowerMode && cloud is not null)
            return new(cloud, $"solar battery {_power.BatteryPercent:0}% — offloading '{task}' to cloud to conserve power");
        if (edge is not null && !(_power.LowPowerMode))
            return new(edge, "edge SLM available (offline-first)");
        if (cloud is not null)
            return new(cloud, "no verified edge SLM available — using cloud endpoint");
        return new(_fallback, edge is null ? "no verified SLM reachable — offline template engine" : "low power — offline template engine");
    }
}
