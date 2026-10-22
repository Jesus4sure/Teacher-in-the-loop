using Titlape.Core;

namespace Titlape.Context.Curriculum;

/// <summary>A syllabus passage indexed by the Curriculum RAG engine.</summary>
public sealed record SyllabusChunk(string Id, string SkillCode, string Kind, string Text, LearnerLanguage Language = LearnerLanguage.English);

/// <summary>Bilingual bridge term used for code-switching.</summary>
public sealed record GlossaryTerm(string English, LearnerLanguage Language, string Local);

/// <summary>
/// A national-syllabus content pack (Primary 4 Mathematics, aligned to the NERDC Basic Education
/// curriculum strands: Number &amp; Numeration, Basic Operations, Fractions). Packs are distributed as signed
/// files to edge nodes; this built-in pack makes the platform runnable out of the box.
///
/// <para>Local-language terms and metaphors must be reviewed by in-country language teachers before
/// deployment; they live in data, not code, so teachers can correct them.</para>
/// </summary>
public static class CurriculumPack
{
    public const string PackId = "ng-nerdc-primary4-maths-v1";

    public static readonly IReadOnlyList<Skill> Skills = new[]
    {
        new Skill("MTH-P4-PV", "Mathematics", "Place value of whole numbers up to 9,999", 4, Array.Empty<string>()),
        new Skill("MTH-P4-ADD", "Mathematics", "Addition of 2-digit numbers with regrouping (carrying)", 4, new[] { "MTH-P4-PV" }),
        new Skill("MTH-P4-SUB", "Mathematics", "Subtraction of 2-digit numbers with regrouping (borrowing)", 4, new[] { "MTH-P4-PV", "MTH-P4-ADD" }),
        new Skill("MTH-P4-MUL", "Mathematics", "Multiplication of a 2-digit number by a 1-digit number", 4, new[] { "MTH-P4-ADD" }),
        new Skill("MTH-P4-FEQ", "Mathematics", "Equivalent fractions", 4, Array.Empty<string>()),
        new Skill("MTH-P4-FAD", "Mathematics", "Addition of fractions with the same denominator", 4, new[] { "MTH-P4-FEQ" }),
    };

    public static Skill Skill(string code) => Skills.First(s => s.Code == code);

    public static readonly IReadOnlyDictionary<string, string> MisconceptionNames = new Dictionary<string, string>
    {
        ["digit-not-value"] = "Gives the digit itself instead of its place value",
        ["wrong-place"] = "Assigns the digit to the wrong place (off by a factor of 10)",
        ["no-regroup"] = "Does not carry the extra ten into the tens column",
        ["column-concatenation"] = "Writes column sums side by side instead of regrouping",
        ["smaller-from-larger"] = "Subtracts the smaller digit from the larger in each column",
        ["borrow-without-decrement"] = "Borrows a ten but does not reduce the tens digit",
        ["multiplication-carry-forgotten"] = "Forgets to add the carried tens in multiplication",
        ["partial-product"] = "Multiplies only the ones digit",
        ["additive-reasoning"] = "Adds the same amount to top and bottom instead of multiplying",
        ["added-denominators"] = "Adds the denominators as well as the numerators",
        ["unknown"] = "Error pattern not yet recognised"
    };

    public static IEnumerable<SyllabusChunk> Chunks()
    {
        // Place value
        yield return new("pv-exp", "MTH-P4-PV", "explanation",
            "Place value: each digit in a whole number has a value that depends on its place. From the right the places are ones, tens, hundreds and thousands. In 3,642 the digit 6 is in the hundreds place so its value is 6 hundreds, which is 600.");
        yield return new("pv-notes", "MTH-P4-PV", "teaching_notes",
            "Use a place value chart with columns for thousands, hundreds, tens and ones. Ask pupils to name the place of each digit before stating its value. Bundles of ten sticks and bundles of one hundred help pupils see that each place is ten times the place to its right.");
        yield return new("pv-mc1", "MTH-P4-PV", "misconception",
            "Common error: the pupil gives the digit instead of its value, for example saying the value of 6 in 3,642 is 6. Remediate by asking how many hundreds the digit stands for.");
        yield return new("pv-mc2", "MTH-P4-PV", "misconception",
            "Common error: the pupil places the digit in the wrong column, giving an answer ten times too big or too small. Remediate by counting places from the right, starting with ones.");

        // Addition
        yield return new("add-exp", "MTH-P4-ADD", "explanation",
            "Addition with regrouping: add the ones column first. If the ones add up to ten or more, write the ones digit and carry one ten into the tens column. Then add the tens, including the carried ten.");
        yield return new("add-notes", "MTH-P4-ADD", "teaching_notes",
            "Model regrouping with bundles of ten sticks or bottle tops: ten loose ones are exchanged for one bundle of ten. Pupils should say aloud: ones first, regroup, then tens plus the carried ten.");
        yield return new("add-mc1", "MTH-P4-ADD", "misconception",
            "Common error: the pupil forgets to carry the ten, so 47 plus 38 becomes 75. Remediate by asking whether the ones total is more than nine and where the extra ten goes.");
        yield return new("add-mc2", "MTH-P4-ADD", "misconception",
            "Common error: the pupil writes both column totals side by side, so 47 plus 38 becomes 715. Remediate by showing that the ones place can hold only one digit.");

        // Subtraction
        yield return new("sub-exp", "MTH-P4-SUB", "explanation",
            "Subtraction with regrouping: start with the ones column. If the top ones digit is smaller than the bottom ones digit, borrow one ten from the tens column: the tens digit goes down by one and the ones increase by ten. Then subtract the ones and the tens.");
        yield return new("sub-notes", "MTH-P4-SUB", "teaching_notes",
            "Use bundles of ten: to take away more ones than you have, open one bundle of ten into ten loose ones. Emphasise that the tens digit becomes one less after borrowing.");
        yield return new("sub-mc1", "MTH-P4-SUB", "misconception",
            "Common error: the pupil always subtracts the smaller digit from the larger digit, so 52 minus 27 becomes 35. Remediate by asking whether we can take seven ones away from two ones.");
        yield return new("sub-mc2", "MTH-P4-SUB", "misconception",
            "Common error: the pupil borrows ten ones but forgets to reduce the tens digit, so 52 minus 27 becomes 35 in the tens. Remediate by crossing out the tens digit and writing one less.");

        // Multiplication
        yield return new("mul-exp", "MTH-P4-MUL", "explanation",
            "Multiplying a 2-digit number by a 1-digit number: split the number into tens and ones, multiply each part, then add the parts together. For 23 times 4: 20 times 4 is 80 and 3 times 4 is 12, so the total is 80 plus 12.");
        yield return new("mul-notes", "MTH-P4-MUL", "teaching_notes",
            "Use arrays or groups of objects. Pupils should write the partial products for tens and ones and then add them, before moving to the short method with carrying.");
        yield return new("mul-mc1", "MTH-P4-MUL", "misconception",
            "Common error: the pupil forgets the carried ten, so 23 times 4 becomes 82. Remediate by writing the partial products separately and adding them.");
        yield return new("mul-mc2", "MTH-P4-MUL", "misconception",
            "Common error: the pupil multiplies only the ones digit. Remediate by asking what happened to the tens part of the number.");

        // Equivalent fractions
        yield return new("feq-exp", "MTH-P4-FEQ", "explanation",
            "Equivalent fractions name the same amount. Multiply the numerator (top number) and the denominator (bottom number) by the same number to make an equivalent fraction. One half is the same as two quarters and four eighths.");
        yield return new("feq-notes", "MTH-P4-FEQ", "teaching_notes",
            "Fold paper strips into halves, quarters and eighths and compare their lengths. Ask: how many times bigger is the new denominator, and so how many times bigger must the numerator be?");
        yield return new("feq-mc1", "MTH-P4-FEQ", "misconception",
            "Common error: the pupil adds the same amount to the numerator and denominator instead of multiplying. Remediate with fraction strips showing the parts are not equal in size.");

        // Adding fractions
        yield return new("fad-exp", "MTH-P4-FAD", "explanation",
            "Adding fractions with the same denominator: the denominator tells the size of the parts and stays the same. Add only the numerators to count how many parts there are altogether. Two fifths plus one fifth is three fifths.");
        yield return new("fad-notes", "MTH-P4-FAD", "teaching_notes",
            "Use a shared object cut into equal parts, such as a loaf of bread or an orange. Pupils count the parts they have altogether while noticing that the size of each part does not change.");
        yield return new("fad-mc1", "MTH-P4-FAD", "misconception",
            "Common error: the pupil adds the denominators too, so two fifths plus one fifth becomes three tenths. Remediate by asking whether the pieces got smaller when we put them together.");

        foreach (var m in Metaphors()) yield return m;
    }

    /// <summary>Culturally resonant metaphors (curated, so the off-curriculum rail recognises them).</summary>
    public static IEnumerable<SyllabusChunk> Metaphors()
    {
        string[] codes = { "MTH-P4-PV", "MTH-P4-ADD", "MTH-P4-SUB", "MTH-P4-MUL", "MTH-P4-FEQ", "MTH-P4-FAD" };
        var byLang = new Dictionary<LearnerLanguage, string[]>
        {
            [LearnerLanguage.English] = new[]
            {
                "Think of a trader packing oranges: ten oranges fill a small basket, and ten small baskets fill one big basket",
                "Think of counting oranges at the market: when you have ten loose oranges, you put them into one basket of ten",
                "Think of buying from a market stall: if you need more loose oranges than you have, you open one basket of ten",
                "Think of a market stall with rows of tomatoes: each row has the same number, and we count all the rows",
                "Think of sharing a loaf of bread: cutting each piece into smaller equal pieces gives more pieces of the same total amount",
                "Think of an orange cut into equal pieces: putting pieces together gives more pieces, but each piece stays the same size"
            },
            [LearnerLanguage.Yoruba] = new[]
            {
                "Think of a trader at the ọjà (market) packing oranges: ten oranges fill a small agbọn (basket), ten small baskets fill a big one",
                "Think of counting oranges at the ọjà (market): ten loose oranges go into one agbọn (basket) of mẹ́wàá (ten)",
                "Think of buying at the ọjà (market): if you need more loose oranges, you open one agbọn (basket) of mẹ́wàá (ten)",
                "Think of a stall at the ọjà (market) with rows of tomatoes: each row has the same number, and we count all the rows",
                "Think of sharing a loaf of bread at home: cutting each piece into smaller equal pieces gives more pieces of the same total amount",
                "Think of an orange cut into equal pieces (ìdá): putting pieces together gives more pieces, but each piece stays the same size"
            },
            [LearnerLanguage.Hausa] = new[]
            {
                "Think of a trader at the kasuwa (market) packing oranges: ten oranges fill a small basket, and ten small baskets fill a big one",
                "Think of counting oranges at the kasuwa (market): goma (ten) loose oranges go into one basket",
                "Think of buying at the kasuwa (market): if you need more loose oranges, you open one basket of goma (ten)",
                "Think of a stall at the kasuwa (market) with rows of tomatoes: each row has the same number, and we count all the rows",
                "Think of sharing a loaf of bread: cutting each piece into smaller equal pieces gives more pieces of the same total amount",
                "Think of an orange cut into equal pieces: putting pieces together gives more pieces, but each piece stays the same size"
            },
            [LearnerLanguage.Swahili] = new[]
            {
                "Think of a trader sokoni (at the market) packing maembe (mangoes): ten mangoes fill a small basket, ten small baskets fill a big one",
                "Think of counting maembe (mangoes) sokoni (at the market): kumi (ten) loose mangoes go into one basket",
                "Think of buying sokoni (at the market): if you need more loose mangoes, you open one basket of kumi (ten)",
                "Think of a stall sokoni (at the market) with rows of tomatoes: each row has the same number, and we count all the rows",
                "Think of sharing a loaf of bread: cutting each piece into smaller equal pieces gives more pieces of the same total amount",
                "Think of a mango cut into equal sehemu (parts): putting parts together gives more parts, but each part stays the same size"
            },
            [LearnerLanguage.Amharic] = new[]
            {
                "Think of a trader at the gebeya (ገበያ, market) packing oranges: ten oranges fill a small basket, ten small baskets fill a big one",
                "Think of counting oranges at the gebeya (ገበያ, market): asir (አስር, ten) loose oranges go into one basket",
                "Think of buying at the gebeya (ገበያ, market): if you need more loose oranges, you open one basket of asir (ten)",
                "Think of a stall at the gebeya (ገበያ, market) with rows of tomatoes: each row has the same number, and we count all the rows",
                "Think of sharing injera: cutting each piece into smaller equal pieces gives more pieces of the same total amount",
                "Think of injera torn into equal pieces: putting pieces together gives more pieces, but each piece stays the same size"
            }
        };
        foreach (var (lang, lines) in byLang)
            for (int i = 0; i < codes.Length; i++)
                yield return new SyllabusChunk($"meta-{lang}-{codes[i]}", codes[i], "metaphor", lines[i], lang);
    }

    /// <summary>Bridge terms for code-switching between English and regional languages.</summary>
    public static IEnumerable<GlossaryTerm> Glossary()
    {
        var t = new (string En, string Yo, string Ha, string Sw, string Am)[]
        {
            ("add", "àròpọ̀", "tara", "jumlisha", "መደመር (mädämär)"),
            ("subtract", "àyọkúrò", "rage", "toa", "መቀነስ (mäqännäs)"),
            ("multiply", "ìsọdipúpọ̀", "ninka", "zidisha", "ማባዛት (mabazat)"),
            ("fraction", "ìdá", "juzu'i", "sehemu", "ክፍልፋይ (kəfəlfay)"),
            ("half", "ìdajì", "rabi", "nusu", "ግማሽ (gəmmaš)"),
            ("ten", "mẹ́wàá", "goma", "kumi", "አስር (asər)"),
            ("market", "ọjà", "kasuwa", "soko", "ገበያ (gäbäya)"),
        };
        foreach (var (en, yo, ha, sw, am) in t)
        {
            yield return new GlossaryTerm(en, LearnerLanguage.Yoruba, yo);
            yield return new GlossaryTerm(en, LearnerLanguage.Hausa, ha);
            yield return new GlossaryTerm(en, LearnerLanguage.Swahili, sw);
            yield return new GlossaryTerm(en, LearnerLanguage.Amharic, am);
        }
    }

    public static readonly IReadOnlyDictionary<LearnerLanguage, string> Encouragement = new Dictionary<LearnerLanguage, string>
    {
        [LearnerLanguage.English] = "Well done!",
        [LearnerLanguage.Yoruba] = "Ó dára gan-an! (Very good!)",
        [LearnerLanguage.Hausa] = "Madalla! (Well done!)",
        [LearnerLanguage.Swahili] = "Hongera! (Well done!)",
        [LearnerLanguage.Amharic] = "በጣም ጥሩ! (Very good!)"
    };
}
