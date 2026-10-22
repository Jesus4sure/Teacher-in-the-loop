using System.Security.Cryptography;
using System.Text;
using Titlape.Agents;
using Titlape.Context.Curriculum;
using Titlape.Context.Memory;
using Titlape.Context.Rag;
using Titlape.Core;
using Titlape.CrossCutting.Communication;
using Titlape.CrossCutting.Security;
using Titlape.Data.Privacy;
using Titlape.Data.Sync;
using Titlape.Data.Telemetry;
using Titlape.Governance.Audit;
using Titlape.Governance.Bias;
using Titlape.Governance.Guardrails;
using Titlape.Governance.Tracing;
using Titlape.Models;

namespace Titlape.Orchestration;

public sealed class TitlapeOptions
{
    public string EdgeNodeId { get; set; } = "edge-ng-01";
    public string DataDirectory { get; set; } = "titlape-data";
    /// <summary>Root secret for key derivation. In production this comes from a TPM/KMS, never config.</summary>
    public string? MasterSecret { get; set; }
    public bool PersistState { get; set; }
    public string? GuardrailPolicyFile { get; set; }

    /// <summary>llama.cpp <c>llama-server</c> on the edge node (OpenAI-compatible), e.g. http://127.0.0.1:8080</summary>
    public string? EdgeSlmUrl { get; set; }
    public string EdgeSlmModel { get; set; } = "phi-3-mini-4k-instruct-q4_k_m";
    public string? EdgeSlmSha256 { get; set; }
    public string? EdgeSlmModelPath { get; set; }

    /// <summary>vLLM endpoint in-country cloud (OpenAI-compatible), e.g. https://inference.example.ng</summary>
    public string? CloudLlmUrl { get; set; }
    public string CloudLlmModel { get; set; } = "llama-3.1-8b-instruct";
    public string? CloudApiKey { get; set; }
    public bool AllowCloud { get; set; } = true;

    public double FaultInjectionLeakRate { get; set; }
    public double FaultInjectionDriftRate { get; set; }
    public double FaultInjectionHallucinationRate { get; set; }
    public bool StartOnline { get; set; } = true;
}

/// <summary>
/// Composition root for one TITLAPE deployment: a solar edge node (Layers 1–6 running locally) plus its
/// in-country cloud replica. Wires the cross-cutting security and communication pillars through every layer.
/// </summary>
public sealed class TitlapePlatform : IAsyncDisposable
{
    public TitlapeOptions Options { get; }

    // Cross-cutting pillars
    public TokenService Tokens { get; }
    public AbacPolicyEngine Abac { get; } = new();
    public ConsentRegistry Consent { get; } = new();
    public AgentToolPolicy ToolPolicy { get; } = AgentToolPolicy.Default();
    public MessageBus Bus { get; } = new();
    public ConnectivityState Connectivity { get; }
    public PowerState Power { get; } = new();

    // Layer 1
    public PiiGateway Pii { get; } = new();
    public Pseudonymizer Pseudonyms { get; }
    public AesGcmVault Vault { get; }
    public ReplicaStore EdgeReplica { get; }
    public ReplicaStore CloudReplica { get; }
    public SyncEngine Sync { get; }
    public InteractionLog Interactions { get; }

    // Layer 2
    public ModelRegistry Models { get; } = new();
    public ModelRouter Router { get; }
    public OfflineTemplateEngine OfflineEngine { get; } = new();

    // Layer 3
    public GuardrailEngine Guardrails { get; }
    public CotTracer Tracer { get; } = new();
    public AuditLog Audit { get; }
    public BiasAuditor Bias { get; } = new();

    // Layer 4
    public CurriculumRagEngine Rag { get; }
    public StudentMemory Memory { get; }
    public PathwayService Pathways { get; } = new();
    public ItemBank Items { get; } = new();

    // Layer 5 / 6
    public AgentServices AgentServices { get; }
    public MasterOrchestrator Orchestrator { get; }
    public AlertService Alerts { get; }
    public Roster Roster { get; }

    public TitlapePlatform(TitlapeOptions options)
    {
        Options = options;
        Directory.CreateDirectory(options.DataDirectory);
        if (options.PersistState)
        {
            // Single-writer guard: a second process on the same data directory would fork the audit chain.
            try { _lock = new FileStream(Path.Combine(options.DataDirectory, "node.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException($"Another TITLAPE process is already using '{options.DataDirectory}'."); }
        }
        var master = LoadOrCreateMaster(options);
        byte[] Key(string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, Encoding.UTF8.GetBytes(options.EdgeNodeId), Encoding.UTF8.GetBytes(purpose));

        Tokens = new TokenService(Key("token-signing"));
        Vault = new AesGcmVault(Key("data-at-rest"));
        Pseudonyms = new Pseudonymizer(Key("pseudonyms"));
        Audit = new AuditLog(Key("audit-chain"), options.PersistState ? Path.Combine(options.DataDirectory, "audit.jsonl") : null);
        Connectivity = new ConnectivityState(options.StartOnline);

        EdgeReplica = new ReplicaStore(options.EdgeNodeId);
        CloudReplica = new ReplicaStore("cloud-" + options.EdgeNodeId);
        if (options.PersistState && Vault.LoadJson<ReplicaSnapshot>(SnapshotPath) is { } snap) EdgeReplica.Restore(snap);
        Sync = new SyncEngine(EdgeReplica, CloudReplica, Connectivity, Bus);
        Interactions = new InteractionLog(EdgeReplica, Pii);

        Roster = Roster.Demo(Pseudonyms, Pii);
        foreach (var s in Roster.Students)
        {
            Consent.Record(s.Id, "guardian-form", true, ConsentRegistry.Tutoring);
            Consent.Record(s.Id, "guardian-form", !s.Id.EndsWith("04"), ConsentRegistry.Transcript);
        }

        Rag = new CurriculumRagEngine(Pii);
        Rag.Index(CurriculumPack.Chunks(), CurriculumPack.Glossary(), CurriculumPack.Skills);
        var vocabRng = new Random(17);
        Rag.AddVocabulary(CurriculumPack.Skills.SelectMany(s => Enumerable.Range(0, 40).Select(_ => Items.Generate(s.Code, vocabRng)))
            .SelectMany(g => g.HintLadder.Append(g.Item.Prompt).Append(g.Reinforcement))
            .Concat(SocraticAgent.NudgeTexts.Values).Concat(OfflineTemplateEngine.VettedPhrases)
            .Concat(CurriculumPack.Encouragement.Values));
        Memory = new StudentMemory(EdgeReplica);
        Guardrails = new GuardrailEngine(GuardrailPolicy.Load(options.GuardrailPolicyFile), Rag, Pii);

        OfflineEngine.LeakRate = options.FaultInjectionLeakRate;
        OfflineEngine.DriftRate = options.FaultInjectionDriftRate;
        OfflineEngine.HallucinationRate = options.FaultInjectionHallucinationRate;
        Models.Register(new ModelManifest(OfflineEngine.Name, "TITLAPE", "rule-based", "n/a", "template pack",
            OfflineTemplateEngine.TemplatePackSha256, 0.1, ExecutionSite.Edge));
        Models.VerifyDigest(OfflineEngine.Name, OfflineTemplateEngine.TemplatePackSha256);
        foreach (var m in ModelRegistry.ReferenceCatalogue()) Models.Register(m);

        Router = new ModelRouter(Models, Power, OfflineEngine) { AllowCloud = options.AllowCloud };
        if (!string.IsNullOrWhiteSpace(options.EdgeSlmUrl))
        {
            var edge = new OpenAiCompatibleEngine(new OpenAiCompatibleOptions
            { BaseUrl = options.EdgeSlmUrl, Model = options.EdgeSlmModel, Site = ExecutionSite.Edge });
            Models.Register(new ModelManifest(options.EdgeSlmModel, "edge", "?", "Q4_K_M", "GGUF", options.EdgeSlmSha256 ?? "", 4, ExecutionSite.Edge));
            if (!string.IsNullOrWhiteSpace(options.EdgeSlmModelPath) && !string.IsNullOrWhiteSpace(options.EdgeSlmSha256))
                Models.VerifyFileAsync(options.EdgeSlmModel, options.EdgeSlmModelPath).GetAwaiter().GetResult();
            Router.Add(edge);
        }
        if (!string.IsNullOrWhiteSpace(options.CloudLlmUrl))
        {
            var cloud = new OpenAiCompatibleEngine(new OpenAiCompatibleOptions
            { BaseUrl = options.CloudLlmUrl, Model = options.CloudLlmModel, ApiKey = options.CloudApiKey, Site = ExecutionSite.Cloud, Quantization = "BF16" }, Connectivity);
            // Cloud endpoints are authenticated by mTLS/API key rather than a weights digest.
            Models.Register(new ModelManifest(options.CloudLlmModel, "cloud", "8B", "BF16", "vLLM", "endpoint", 24, ExecutionSite.Cloud));
            Models.VerifyDigest(options.CloudLlmModel, "endpoint");
            Router.Add(cloud);
        }

        AgentServices = new AgentServices
        {
            Memory = Memory, Pathways = Pathways, Rag = Rag, Items = Items, Guardrails = Guardrails,
            Router = Router, Tracer = Tracer, ToolPolicy = ToolPolicy
        };
        Alerts = new AlertService(Memory, Interactions, Audit);
        Orchestrator = new MasterOrchestrator(AgentServices, Roster, Consent, Abac, Interactions, Alerts, Audit);
        foreach (var s in Roster.Students) Pathways.GetOrCreate(s.PseudonymId, MasterOrchestrator.DefaultPathway);
        Audit.Append("system", "platform.start", options.EdgeNodeId, $"curriculum={CurriculumPack.PackId}; chunks={Rag.ChunkCount}");
    }

    private readonly FileStream? _lock;

    private string SnapshotPath => Path.Combine(Options.DataDirectory, $"{Options.EdgeNodeId}.replica.enc");

    public void SaveState()
    {
        if (!Options.PersistState) return;
        Vault.SaveJson(SnapshotPath, EdgeReplica.Snapshot());
    }

    /// <summary>Issues a session token for a roster identity (demo login; production uses the school's OIDC IdP).</summary>
    public string Login(string userId)
    {
        if (Roster.Student(userId) is { } s) return Tokens.Issue(s.Id, Role.Student, s.SchoolId, new[] { s.ClassId });
        if (Roster.Teacher(userId) is { } t) return Tokens.Issue(t.Id, Role.Teacher, t.SchoolId, t.ClassIds);
        if (userId == "admin") return Tokens.Issue("admin", Role.Admin, "*");
        throw new UnauthorizedAccessException("Unknown user.");
    }

    private static byte[] LoadOrCreateMaster(TitlapeOptions o)
    {
        if (!string.IsNullOrEmpty(o.MasterSecret)) return SHA256.HashData(Encoding.UTF8.GetBytes(o.MasterSecret));
        var path = Path.Combine(o.DataDirectory, "node.key");
        if (o.PersistState && File.Exists(path)) return File.ReadAllBytes(path);
        var key = RandomNumberGenerator.GetBytes(32);
        if (o.PersistState) File.WriteAllBytes(path, key);
        return key;
    }

    public ValueTask DisposeAsync()
    {
        SaveState();
        _lock?.Dispose();
        return ValueTask.CompletedTask;
    }
}
