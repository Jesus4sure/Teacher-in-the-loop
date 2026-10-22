using System.Globalization;
using System.Text.RegularExpressions;
using Titlape.Core;

namespace Titlape.Context.Curriculum;

/// <summary>A practice item plus its graduated Socratic hint ladder (none of which contain the answer).</summary>
public sealed record GeneratedItem(PracticeItem Item, IReadOnlyList<string> HintLadder, string Reinforcement);

/// <summary>Parametric item bank: generates unlimited syllabus-aligned items with known misconception signatures.</summary>
public sealed class ItemBank
{
    private static readonly string[] PlaceNames = { "ones", "tens", "hundreds", "thousands" };

    /// <summary>
    /// Generates an item, rejecting any whose hint ladder would state the answer as a standalone number
    /// (defence in depth: the Safety Agent would also block such a hint at runtime).
    /// </summary>
    public GeneratedItem Generate(string skillCode, Random rng)
    {
        for (int attempt = 0; ; attempt++)
        {
            var g = GenerateOnce(skillCode, rng);
            if (attempt >= 50 || !g.HintLadder.Any(h => MentionsAnswer(h, g.Item.CorrectAnswer))) return g;
        }
    }

    private static bool MentionsAnswer(string text, string answer)
    {
        if (answer.Contains('/'))
            return Regex.IsMatch(text, $@"(?<![\d/]){Regex.Escape(answer)}(?![\d/])");
        return Regex.Matches(text, @"(?<![\d/.,])\d{1,3}(?:,\d{3})+(?![\d/])|(?<![\d/.,])\d+(?![\d/]|,\d{3})")
            .Any(m => m.Value.Replace(",", "") == answer);
    }

    private GeneratedItem GenerateOnce(string skillCode, Random rng) => skillCode switch
    {
        "MTH-P4-PV" => PlaceValue(rng),
        "MTH-P4-ADD" => Addition(rng),
        "MTH-P4-SUB" => Subtraction(rng),
        "MTH-P4-MUL" => Multiplication(rng),
        "MTH-P4-FEQ" => EquivalentFraction(rng),
        "MTH-P4-FAD" => FractionAddition(rng),
        _ => throw new ArgumentException($"Unknown skill {skillCode}")
    };

    private static GeneratedItem PlaceValue(Random rng)
    {
        while (true)
        {
            int n = rng.Next(1000, 10000);
            var s = n.ToString(CultureInfo.InvariantCulture);
            int pos = rng.Next(1, 4); // tens, hundreds or thousands
            char dc = s[s.Length - 1 - pos];
            int d = dc - '0';
            if (d < 2 || s.Count(c => c == dc) != 1 || n == d * (int)Math.Pow(10, pos)) continue;
            int pv = (int)Math.Pow(10, pos);
            int ans = d * pv;
            var mis = new Dictionary<string, string> { [d.ToString()] = "digit-not-value" };
            mis.TryAdd((ans * 10).ToString(), "wrong-place");
            if (pos > 0) mis.TryAdd((ans / 10).ToString(), "wrong-place");
            var item = new PracticeItem($"PV-{n}-{pos}", "MTH-P4-PV",
                $"What is the value of the digit {d} in {n:N0}?", ans.ToString(), mis, 1 + pos / 2);
            var ex = new[] { (num: "5,281", dig: 2, place: "hundreds", val: 200), (num: "5,381", dig: 3, place: "hundreds", val: 300),
                             (num: "7,146", dig: 4, place: "tens", val: 40) }.First(e => e.val != ans && e.val != ans * 10 && e.val * 10 != ans);
            return new GeneratedItem(item, new[]
            {
                $"Name the place of each digit in {n:N0}, starting from the right: ones, tens, hundreds, thousands. Which place is the {d} in?",
                $"The {d} is in the {PlaceNames[pos]} place. That means it stands for {d} groups of {pv:N0}. How much is {d} groups of {pv:N0}?",
                $"Look at a similar number: in {ex.num} the digit {ex.dig} is in the {ex.place} place, so its value is {ex.val}. Use the same idea for the {d} in {n:N0}."
            }, $"The digit {d} is worth {d} {PlaceNames[pos]}.");
        }
    }

    private static GeneratedItem Addition(Random rng)
    {
        int a0 = rng.Next(2, 10), b0 = rng.Next(Math.Max(1, 10 - a0), 10); // ones always regroup
        int a1 = rng.Next(1, 6), b1 = rng.Next(1, 4);
        int a = a1 * 10 + a0, b = b1 * 10 + b0, ans = a + b, s0 = a0 + b0;
        var mis = new Dictionary<string, string>
        {
            [((a1 + b1) * 10 + s0 % 10).ToString()] = "no-regroup",
            [$"{a1 + b1}{s0}"] = "column-concatenation"
        };
        var (ea, eb) = FirstDifferent(ans, (x, y) => x + y, (47, 38), (36, 29), (58, 27));
        var item = new PracticeItem($"ADD-{a}-{b}", "MTH-P4-ADD", $"Work out {a} + {b}.", ans.ToString(), mis, 2);
        return new GeneratedItem(item, new[]
        {
            $"Start with the ones column. What is {a0} + {b0}? Is that more than 9?",
            $"{a0} + {b0} = {s0}. We write {s0 % 10} in the ones place and carry 1 ten to the tens column. Now add the tens: {a1} + {b1} + the carried 1. What do you get?",
            $"Look at a similar sum: {ea} + {eb}. Ones: {ea % 10} + {eb % 10} = {ea % 10 + eb % 10}, write {(ea % 10 + eb % 10) % 10}, carry 1. Tens: {ea / 10} + {eb / 10} + 1 = {ea / 10 + eb / 10 + 1}. So {ea} + {eb} = {ea + eb}. Now do {a} + {b} the same way."
        }, "Remember: ones first, regroup ten ones into one ten, then add the tens.");
    }

    private static GeneratedItem Subtraction(Random rng)
    {
        int a1 = rng.Next(4, 10), b1 = rng.Next(1, a1);
        int a0 = rng.Next(0, 8), b0 = rng.Next(a0 + 1, 10);
        int a = a1 * 10 + a0, b = b1 * 10 + b0, ans = a - b;
        if (ans == b) return Subtraction(rng); // the prompt itself would reveal the answer
        var mis = new Dictionary<string, string>();
        mis.TryAdd(((a1 - b1) * 10 + (b0 - a0)).ToString(), "smaller-from-larger");
        mis.TryAdd(((a1 - b1) * 10 + (a0 + 10 - b0)).ToString(), "borrow-without-decrement");
        mis.Remove(ans.ToString());
        var (ea, eb) = FirstDifferent(ans, (x, y) => x - y, (52, 27), (63, 28), (81, 46));
        var item = new PracticeItem($"SUB-{a}-{b}", "MTH-P4-SUB", $"Work out {a} − {b}.", ans.ToString(), mis, 2);
        return new GeneratedItem(item, new[]
        {
            $"Look at the ones column: {a0} − {b0}. Can we take {b0} away from {a0} without going below zero?",
            $"We need to borrow: take 1 ten from the {a1} tens, leaving {a1 - 1} tens. The ones become {a0 + 10}. Subtract {b0} from those ones, then subtract {b1} from the {a1 - 1} tens. What do you get?",
            $"Look at a similar problem: {ea} − {eb}. Borrow 1 ten: {ea / 10} tens become {ea / 10 - 1} tens and {ea % 10} ones become {ea % 10 + 10}. Ones: {ea % 10 + 10} − {eb % 10} = {ea % 10 + 10 - eb % 10}. Tens: {ea / 10 - 1} − {eb / 10} = {ea / 10 - 1 - eb / 10}. So {ea} − {eb} = {ea - eb}. Now try {a} − {b}."
        }, "Remember: when the top ones are too small, borrow one ten and make the tens digit one less.");
    }

    private static GeneratedItem Multiplication(Random rng)
    {
        int m = rng.Next(3, 10), a1 = rng.Next(1, 5), a0;
        do a0 = rng.Next(2, 10); while (a0 * m < 10);
        int a = a1 * 10 + a0, ans = a * m;
        var mis = new Dictionary<string, string>();
        mis.TryAdd(((a1 * m) * 10 + (a0 * m) % 10).ToString(), "multiplication-carry-forgotten");
        mis.TryAdd((a0 * m).ToString(), "partial-product");
        mis.Remove(ans.ToString());
        var (ea, em) = FirstDifferent(ans, (x, y) => x * y, (23, 4), (34, 3), (16, 5));
        var item = new PracticeItem($"MUL-{a}-{m}", "MTH-P4-MUL", $"Work out {a} × {m}.", ans.ToString(), mis, 3);
        return new GeneratedItem(item, new[]
        {
            $"Break {a} into tens and ones: {a1 * 10} + {a0}. What is {a0} × {m}?",
            $"{a0} × {m} = {a0 * m} and {a1 * 10} × {m} = {a1 * 10 * m}. How do you combine these two parts to get {a} × {m}?",
            $"Look at a similar problem: {ea} × {em}. Split {ea} into {ea / 10 * 10} + {ea % 10}. {ea / 10 * 10} × {em} = {ea / 10 * 10 * em} and {ea % 10} × {em} = {ea % 10 * em}. Add them: {ea * em}. Now do {a} × {m} the same way."
        }, "Remember: multiply the tens and the ones separately, then add the parts.");
    }

    private static GeneratedItem EquivalentFraction(Random rng)
    {
        int d = rng.Next(2, 6), n = rng.Next(1, d), k = rng.Next(2, 5);
        int nd = d * k, ans = n * k;
        var mis = new Dictionary<string, string>();
        var additive = n + (nd - d);
        if (additive != ans) mis[additive.ToString()] = "additive-reasoning";
        var item = new PracticeItem($"FEQ-{n}-{d}-{k}", "MTH-P4-FEQ",
            $"Fill in the missing number: {n}/{d} = ?/{nd}", ans.ToString(), mis, 2);
        var ex = new[] { (n: 1, d: 2, k: 3), (n: 1, d: 3, k: 2), (n: 2, d: 5, k: 3) }
            .First(e => e.n * e.k != ans && e.d * e.k != nd);
        return new GeneratedItem(item, new[]
        {
            $"How many times bigger is {nd} than {d}?",
            $"The bottom number {d} was multiplied by {k} to make {nd}. To keep the fraction the same size, what must we do to the top number {n}?",
            $"Look at a similar one: {ex.n}/{ex.d} = ?/{ex.d * ex.k}. The bottom was multiplied by {ex.k}, so the top is {ex.n} × {ex.k} = {ex.n * ex.k}, giving {ex.n * ex.k}/{ex.d * ex.k}. Now do {n}/{d} = ?/{nd} the same way."
        }, "Remember: multiply the top and bottom by the same number.");
    }

    private static GeneratedItem FractionAddition(Random rng)
    {
        int d = rng.Next(5, 10), n1 = rng.Next(1, d - 1), n2 = rng.Next(1, d - n1);
        var ans = $"{n1 + n2}/{d}";
        var mis = new Dictionary<string, string> { [$"{n1 + n2}/{2 * d}"] = "added-denominators" };
        var ex = new[] { (n1: 2, n2: 3, d: 7), (n1: 2, n2: 3, d: 9), (n1: 1, n2: 4, d: 6) }
            .First(e => e.d != d);
        var item = new PracticeItem($"FAD-{n1}-{n2}-{d}", "MTH-P4-FAD", $"Work out {n1}/{d} + {n2}/{d}.", ans, mis, 2);
        return new GeneratedItem(item, new[]
        {
            $"Both fractions are cut into {d} equal parts. When we put the parts together, does the size of each part change?",
            $"The parts are still {Ordinal(d)}s. If you have {n1} of them and then {n2} more, how many {Ordinal(d)}s do you have altogether?",
            $"Look at a similar one: {ex.n1}/{ex.d} + {ex.n2}/{ex.d}. The parts stay as {Ordinal(ex.d)}s and we count them: {ex.n1} + {ex.n2} = {ex.n1 + ex.n2}, so the total is {ex.n1 + ex.n2}/{ex.d}. Now try {n1}/{d} + {n2}/{d}."
        }, "Remember: the denominator stays the same; add only the numerators.");
    }

    /// <summary>Picks a worked example whose result differs from the item's answer (prevents leakage).</summary>
    private static (int, int) FirstDifferent(int answer, Func<int, int, int> op, params (int X, int Y)[] candidates) =>
        candidates.First(c => op(c.X, c.Y) != answer && c.X != answer && c.Y != answer);

    private static string Ordinal(int d) => d switch
    {
        2 => "half", 3 => "third", 4 => "quarter", 5 => "fifth", 6 => "sixth", 7 => "seventh", 8 => "eighth", 9 => "ninth", _ => $"{d}th"
    };
}

public enum AnswerOutcome { Correct, KnownMisconception, Incorrect, Unparseable }

public sealed record AnswerCheck(AnswerOutcome Outcome, string? MisconceptionCode, string Normalized);

/// <summary>Normalises free-text answers ("85", " 8 5 ", "eighty five" is not supported, "3/7", "6/14").</summary>
public static class AnswerChecker
{
    private static readonly Regex Fraction = new(@"^\s*(-?\d+)\s*/\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"-?\d[\d,\s]*", RegexOptions.Compiled);

    public static string Normalize(string raw)
    {
        raw = raw.Trim().TrimEnd('.', '!');
        var f = Fraction.Match(raw);
        if (f.Success) return $"{int.Parse(f.Groups[1].Value)}/{int.Parse(f.Groups[2].Value)}";
        var m = Number.Match(raw);
        return m.Success ? m.Value.Replace(",", "").Replace(" ", "") : raw.ToLowerInvariant();
    }

    public static AnswerCheck Check(PracticeItem item, string raw)
    {
        var norm = Normalize(raw);
        if (string.IsNullOrWhiteSpace(norm) || !(char.IsDigit(norm[0]) || norm[0] == '-'))
            return new AnswerCheck(AnswerOutcome.Unparseable, null, norm);
        if (Equivalent(norm, item.CorrectAnswer)) return new AnswerCheck(AnswerOutcome.Correct, null, norm);
        if (item.KnownMisconceptions.TryGetValue(norm, out var code)) return new AnswerCheck(AnswerOutcome.KnownMisconception, code, norm);
        return new AnswerCheck(AnswerOutcome.Incorrect, null, norm);
    }

    private static bool Equivalent(string a, string b)
    {
        if (a == b) return true;
        static (long N, long D)? Rat(string s)
        {
            var p = s.Split('/');
            if (p.Length == 2 && long.TryParse(p[0], out var n) && long.TryParse(p[1], out var d) && d != 0) return (n, d);
            if (p.Length == 1 && long.TryParse(p[0], out var w)) return (w, 1);
            return null;
        }
        var ra = Rat(a); var rb = Rat(b);
        return ra is not null && rb is not null && ra.Value.N * rb.Value.D == rb.Value.N * ra.Value.D;
    }
}
