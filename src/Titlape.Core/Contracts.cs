namespace Titlape.Core;

/// <summary>
/// Implemented by the Curriculum RAG engine (Layer 4) and consumed by the guardrails (Layer 3) to detect
/// off-curriculum drift without Layer 3 depending on Layer 4's internals.
/// </summary>
public interface ICurriculumGrounding
{
    /// <summary>Fraction (0..1) of the sentence's content words that belong to the curated curriculum vocabulary.</summary>
    double VocabularyCoverage(string sentence, out IReadOnlyList<string> unknownWords);

    /// <summary>Best retrieval similarity (0..1) between the text and the syllabus chunks for a skill.</summary>
    double SkillSimilarity(string text, string? skillCode);
}

/// <summary>Words that are part of the tutoring register rather than lesson content.</summary>
public static class PedagogicalRegister
{
    public static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","but","if","so","then","than","to","of","in","on","at","by","for","with","from","into",
        "is","are","was","were","be","been","being","am","do","does","did","done","have","has","had","will","would",
        "can","could","should","may","might","must","shall","it","its","this","that","these","those","there","here",
        "i","you","we","they","he","she","me","us","them","my","your","our","their","his","her","what","which","who",
        "how","when","where","why","not","no","yes","all","any","each","every","some","more","most","one","ones","just",
        "also","very","too","up","down","out","about","again","let","lets","let's","s","t","ll","re","ve","d","m","as",
        "now","same","way","get","got","go","see","look","like","much","many","only","own","both","new","use","into"
    };

    public static readonly HashSet<string> Register = new(StringComparer.OrdinalIgnoreCase)
    {
        "good","great","nice","well","excellent","effort","try","trying","tried","think","thinking","thought","step",
        "steps","answer","answers","check","careful","carefully","together","close","reason","slow","share","tell",
        "write","find","show","work","working","practise","practice","time","take","thank","thanks","right","correct",
        "wrong","mistake","again","first","next","last","start","finally","remember","notice","similar","example",
        "question","problem","sum","total","value","number","numbers","digit","digits","calculate","compare","learn",
        "lesson","today","teacher","help","hint","idea","part","parts","kind","something","somebody","everyone","yours",
        "becomes","become","make","makes","made","give","gives","keep","need","needs","want","know","means","mean",
        "said","say","says","sure","fine","okay","ok","done","great","job","brilliant","wonderful","fantastic","thinking",
        "bit","little","mind","focus","explain","explains","each","side","whole","left","right","top","bottom","line",
        "place","put","goes","went","going","come","comes","happens","happen","doing","did","trying","close","closer"
    };
}
