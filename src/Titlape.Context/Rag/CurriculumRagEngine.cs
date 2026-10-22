using System.Diagnostics;
using System.Text;
using Titlape.Context.Curriculum;
using Titlape.Core;
using Titlape.CrossCutting;
using Titlape.Data.Privacy;
using Titlape.Data.Vector;

namespace Titlape.Context.Rag;

/// <summary>
/// Deterministic local text embedder (feature hashing of word unigrams, bigrams and character
/// trigrams). Runs on any CPU with no model download; a sentence-transformer can be swapped in through
/// <see cref="IEmbedder"/> when the edge hardware allows.
/// </summary>
public interface IEmbedder
{
    int Dimensions { get; }
    float[] Embed(string text);
}

public sealed class HashingEmbedder : IEmbedder
{
    public int Dimensions { get; }
    public HashingEmbedder(int dimensions = 1024) => Dimensions = dimensions;

    public float[] Embed(string text)
    {
        var v = new float[Dimensions];
        var toks = TextUtil.Tokenize(text).Where(t => !PedagogicalRegister.StopWords.Contains(t)).Select(Stem).ToArray();
        for (int i = 0; i < toks.Length; i++)
        {
            Add(v, "w:" + toks[i], 1.0f);
            if (i + 1 < toks.Length) Add(v, "b:" + toks[i] + "_" + toks[i + 1], 0.7f);
            var padded = $"#{toks[i]}#";
            for (int j = 0; j + 3 <= padded.Length; j++) Add(v, "c:" + padded.Substring(j, 3), 0.25f);
        }
        return v;
    }

    private void Add(float[] v, string feature, float weight)
    {
        uint h = Fnv1a(feature);
        int idx = (int)(h % (uint)Dimensions);
        v[idx] += (h & 0x80000000) != 0 ? weight : -weight;
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
        return h;
    }

    /// <summary>Domain lemmas for primary-maths vocabulary (teacher and pupil phrasings differ).</summary>
    private static readonly Dictionary<string, string> Lemmas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["multiplication"] = "multiply", ["multiplying"] = "multiply", ["multiplied"] = "multiply", ["times"] = "multiply",
        ["product"] = "multiply", ["products"] = "multiply",
        ["carried"] = "carry", ["carrying"] = "carry", ["carries"] = "carry",
        ["adding"] = "add", ["addition"] = "add", ["added"] = "add", ["adds"] = "add", ["plus"] = "add",
        ["subtraction"] = "subtract", ["subtracting"] = "subtract", ["minus"] = "subtract", ["subtracts"] = "subtract",
        ["borrowing"] = "borrow", ["borrowed"] = "borrow", ["borrows"] = "borrow",
        ["regrouping"] = "regroup", ["regrouped"] = "regroup",
        ["denominators"] = "denominator", ["numerators"] = "numerator", ["fractions"] = "fraction",
        ["halves"] = "half", ["quarters"] = "quarter", ["fifths"] = "fifth", ["eighths"] = "eighth",
        ["equivalent"] = "equivalent", ["places"] = "place", ["digits"] = "digit",
    };

    internal static string Stem(string t)
    {
        if (Lemmas.TryGetValue(t, out var lemma)) return lemma;
        if (t.Length > 4 && t.EndsWith("ing")) return t[..^3];
        if (t.Length > 3 && t.EndsWith("es")) return t[..^2];
        if (t.Length > 3 && t.EndsWith('s')) return t[..^1];
        return t;
    }
}

public sealed record RetrievedChunk(SyllabusChunk Chunk, double Score, double VectorScore, double Bm25Score);

/// <summary>
/// Curriculum RAG Engine (Layer 4). Hybrid retrieval (BM25 lexical + dense vector, reciprocal fusion)
/// over the national syllabus, with skill and language filters. All indexed text passes the PII
/// gateway first (RQ 3.2). Also implements <see cref="ICurriculumGrounding"/> for the off-curriculum rail.
/// </summary>
public sealed class CurriculumRagEngine : ICurriculumGrounding
{
    private readonly IEmbedder _embedder;
    private readonly VectorStore _vectors = new();
    private readonly PiiGateway _pii;
    private readonly Dictionary<string, SyllabusChunk> _chunks = new();
    private readonly Dictionary<string, Dictionary<string, int>> _termFreq = new();
    private readonly Dictionary<string, int> _docFreq = new();
    private readonly HashSet<string> _vocabulary = new(StringComparer.OrdinalIgnoreCase);
    private double _avgLen;

    public int PiiRedactionsAtIndex { get; private set; }
    public int ChunkCount => _chunks.Count;

    public CurriculumRagEngine(PiiGateway pii, IEmbedder? embedder = null)
    {
        _pii = pii;
        _embedder = embedder ?? new HashingEmbedder();
    }

    public void Index(IEnumerable<SyllabusChunk> chunks, IEnumerable<GlossaryTerm>? glossary = null, IEnumerable<Skill>? skills = null)
    {
        foreach (var raw in chunks)
        {
            var red = _pii.Redact(raw.Text);
            if (red.HadPii) PiiRedactionsAtIndex += red.Entities.Count;
            var c = raw with { Text = red.Text };
            _chunks[c.Id] = c;
            _vectors.Upsert(new VectorRecord(c.Id, _embedder.Embed(c.Text), c.Text,
                new Dictionary<string, string> { ["skill"] = c.SkillCode, ["kind"] = c.Kind, ["lang"] = c.Language.ToString() }));
            var toks = Content(c.Text).ToList();
            var tf = toks.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            _termFreq[c.Id] = tf;
            foreach (var t in tf.Keys) _docFreq[t] = _docFreq.GetValueOrDefault(t) + 1;
            foreach (var w in TextUtil.Tokenize(c.Text)) _vocabulary.Add(HashingEmbedder.Stem(w));
        }
        foreach (var g in glossary ?? Enumerable.Empty<GlossaryTerm>())
            foreach (var w in TextUtil.Tokenize(g.English + " " + g.Local)) _vocabulary.Add(HashingEmbedder.Stem(w));
        foreach (var s in skills ?? Enumerable.Empty<Skill>())
            foreach (var w in TextUtil.Tokenize(s.Title)) _vocabulary.Add(HashingEmbedder.Stem(w));
        _avgLen = _termFreq.Count == 0 ? 0 : _termFreq.Values.Average(d => d.Values.Sum());
    }

    /// <summary>
    /// Registers vetted tutoring language (hint-ladder templates, agent nudges, praise phrases) as
    /// in-curriculum vocabulary, so the off-curriculum rail only fires on genuinely new topics.
    /// </summary>
    public void AddVocabulary(IEnumerable<string> vettedTexts)
    {
        foreach (var t in vettedTexts)
            foreach (var w in TextUtil.Tokenize(t)) _vocabulary.Add(HashingEmbedder.Stem(w));
    }

    public IReadOnlyList<RetrievedChunk> Retrieve(string query, int k = 3, string? skillCode = null,
        LearnerLanguage? language = null, string? kind = null)
    {
        using var act = TitlapeTelemetry.Source.StartActivity("rag.retrieve");
        act?.SetTag(TitlapeTelemetry.SpanKind, "RETRIEVER");
        act?.SetTag(TitlapeTelemetry.InputValue, query);

        bool Filter(SyllabusChunk c) =>
            (skillCode is null || c.SkillCode == skillCode) &&
            (kind is null || c.Kind == kind) &&
            // Metaphors are only returned when explicitly requested (Localization Agent); syllabus
            // retrieval for grounding and evaluation never mixes them in.
            (c.Kind != "metaphor" || (kind == "metaphor" && (language is null || c.Language == language)));

        var dense = _vectors.Search(_embedder.Embed(query), 50, r => Filter(_chunks[r.Id]))
            .ToDictionary(h => h.Record.Id, h => h.Score);
        var qTerms = Content(query).Distinct().ToArray();
        var lexical = _chunks.Values.Where(Filter).ToDictionary(c => c.Id, c => Bm25(c.Id, qTerms));

        // Reciprocal rank fusion of the two rankings.
        var denseRank = dense.OrderByDescending(x => x.Value).Select((x, i) => (x.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var lexRank = lexical.OrderByDescending(x => x.Value).Select((x, i) => (x.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var scored = lexical.Keys.Union(dense.Keys)
            .Select(id => (id, score: 1.0 / (60 + denseRank.GetValueOrDefault(id, 1000)) + 1.0 / (60 + lexRank.GetValueOrDefault(id, 1000))))
            .ToList();

        // Skill-aware reranking: the syllabus is organised by learning objective, so first infer the
        // objective the query is about (evidence pooled over its passages), then rank passages within it.
        var skillScore = scored.GroupBy(x => _chunks[x.id].SkillCode)
            .ToDictionary(g => g.Key, g =>
            {
                var top = g.Select(x => x.score).OrderByDescending(v => v).ToArray();
                return top[0] + (top.Length > 1 ? 0.5 * top[1] : 0);
            });
        var fused = scored
            .OrderByDescending(x => skillScore[_chunks[x.id].SkillCode])
            .ThenByDescending(x => x.score)
            .Take(k)
            .Select(x => new RetrievedChunk(_chunks[x.id], x.score, dense.GetValueOrDefault(x.id), lexical.GetValueOrDefault(x.id)))
            .ToList();
        act?.SetTag(TitlapeTelemetry.RetrievalDocs, string.Join(",", fused.Select(f => f.Chunk.Id)));
        return fused;
    }

    /// <summary>Precision@k over labelled queries (each query's gold skill) — RQ 3.1 metric.</summary>
    public double PrecisionAtK(IEnumerable<(string Query, string GoldSkill)> labelled, int k = 3)
    {
        var list = labelled.ToList();
        if (list.Count == 0) return 0;
        double total = 0;
        foreach (var (q, gold) in list)
        {
            var hits = Retrieve(q, k, kind: null).Where(h => h.Chunk.Kind != "metaphor").ToList();
            total += hits.Count == 0 ? 0 : hits.Count(h => h.Chunk.SkillCode == gold) / (double)hits.Count;
        }
        return total / list.Count;
    }

    public double VocabularyCoverage(string sentence, out IReadOnlyList<string> unknownWords)
    {
        var content = TextUtil.Tokenize(sentence)
            .Where(t => !PedagogicalRegister.StopWords.Contains(t) && !PedagogicalRegister.Register.Contains(t)
                        && !t.All(char.IsDigit) && t.Length > 1)
            .Select(HashingEmbedder.Stem)
            .ToList();
        var unknown = content.Where(w => !_vocabulary.Contains(w)).Distinct().ToList();
        unknownWords = unknown;
        return content.Count == 0 ? 1.0 : 1.0 - unknown.Count / (double)content.Distinct().Count();
    }

    public double SkillSimilarity(string text, string? skillCode)
    {
        var hits = _vectors.Search(_embedder.Embed(text), 1, r => skillCode is null || r.Metadata["skill"] == skillCode);
        return hits.Count == 0 ? 0 : Math.Max(0, hits[0].Score);
    }

    private double Bm25(string docId, string[] qTerms, double k1 = 1.2, double b = 0.75)
    {
        var tf = _termFreq[docId];
        double len = tf.Values.Sum(), score = 0, n = _termFreq.Count;
        foreach (var t in qTerms)
        {
            if (!tf.TryGetValue(t, out var f)) continue;
            var df = _docFreq.GetValueOrDefault(t);
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            score += idf * f * (k1 + 1) / (f + k1 * (1 - b + b * len / Math.Max(1, _avgLen)));
        }
        return score;
    }

    private static IEnumerable<string> Content(string text) =>
        TextUtil.Tokenize(text).Where(t => !PedagogicalRegister.StopWords.Contains(t)).Select(HashingEmbedder.Stem);

    /// <summary>Labelled retrieval benchmark used by the evaluation harness and tests.</summary>
    public static IEnumerable<(string Query, string GoldSkill)> BenchmarkQueries() => new[]
    {
        ("what is the value of the digit 6 in 3,642", "MTH-P4-PV"),
        ("which place is the digit in hundreds thousands", "MTH-P4-PV"),
        ("pupil says the value of the digit is just the digit", "MTH-P4-PV"),
        ("carry the ten into the tens column when adding", "MTH-P4-ADD"),
        ("47 plus 38 regrouping ones", "MTH-P4-ADD"),
        ("pupil forgets to carry when adding two numbers", "MTH-P4-ADD"),
        ("borrow one ten when the top ones digit is smaller", "MTH-P4-SUB"),
        ("52 minus 27 subtract smaller from larger", "MTH-P4-SUB"),
        ("take away with regrouping tens digit becomes one less", "MTH-P4-SUB"),
        ("23 times 4 partial products tens and ones", "MTH-P4-MUL"),
        ("multiply a two digit number by a one digit number", "MTH-P4-MUL"),
        ("forgets the carried ten in multiplication", "MTH-P4-MUL"),
        ("one half is the same as two quarters", "MTH-P4-FEQ"),
        ("multiply numerator and denominator by the same number", "MTH-P4-FEQ"),
        ("equivalent fractions with paper strips", "MTH-P4-FEQ"),
        ("two fifths plus one fifth", "MTH-P4-FAD"),
        ("adding fractions with the same denominator", "MTH-P4-FAD"),
        ("pupil adds the denominators too", "MTH-P4-FAD"),
    };
}
