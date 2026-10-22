using Titlape.Core;
using Titlape.Data.Privacy;

namespace Titlape.Orchestration;

/// <summary>
/// School / class / learner registry. Lives on the edge node only: real names and guardian contacts
/// never leave Layer 1–level storage; everything above sees pseudonyms.
/// </summary>
public sealed class Roster
{
    private readonly Dictionary<string, School> _schools = new();
    private readonly Dictionary<string, Teacher> _teachers = new();
    private readonly Dictionary<string, ClassRoom> _classes = new();
    private readonly Dictionary<string, Student> _students = new();
    private readonly Dictionary<string, string> _byPseudonym = new();

    public IReadOnlyCollection<School> Schools => _schools.Values;
    public IReadOnlyCollection<Teacher> Teachers => _teachers.Values;
    public IReadOnlyCollection<ClassRoom> Classes => _classes.Values;
    public IReadOnlyCollection<Student> Students => _students.Values;

    public void Add(School s) => _schools[s.Id] = s;
    public void Add(Teacher t) => _teachers[t.Id] = t;
    public void Add(ClassRoom c) => _classes[c.Id] = c;
    public void Add(Student s) { _students[s.Id] = s; _byPseudonym[s.PseudonymId] = s.Id; }

    public School School(string id) => _schools[id];
    public Teacher? Teacher(string id) => _teachers.GetValueOrDefault(id);
    public ClassRoom? Class(string id) => _classes.GetValueOrDefault(id);
    public Student? Student(string id) => _students.GetValueOrDefault(id);
    public Student? ByPseudonym(string p) => _byPseudonym.TryGetValue(p, out var id) ? _students[id] : null;
    public IEnumerable<Student> InClass(string classId) => _students.Values.Where(s => s.ClassId == classId).OrderBy(s => s.FullName);

    /// <summary>Demonstration roster: two fictional schools in different tenants (Nigeria and Kenya).</summary>
    public static Roster Demo(Pseudonymizer pseudo, PiiGateway pii)
    {
        var r = new Roster();
        r.Add(new School("sch-ng-01", "Sunrise Community Primary School", "Nigeria", "Lagos", HasGridPower: false, HasSolarHub: true, "edge-ng-01"));
        r.Add(new School("sch-ke-01", "Lakeside Primary School", "Kenya", "Kisumu", HasGridPower: true, HasSolarHub: true, "edge-ke-01"));
        r.Add(new Teacher("t-ng-01", "Mrs. Adebayo", "sch-ng-01", new[] { "cls-ng-p4a" }));
        r.Add(new Teacher("t-ke-01", "Mr. Otieno", "sch-ke-01", new[] { "cls-ke-p4b" }));
        r.Add(new ClassRoom("cls-ng-p4a", "sch-ng-01", "Primary 4A", EducationTier.Primary, 4, "t-ng-01"));
        r.Add(new ClassRoom("cls-ke-p4b", "sch-ke-01", "Grade 4B", EducationTier.Primary, 4, "t-ke-01"));

        var ng = new (string Name, LearnerLanguage Lang, string Gender)[]
        {
            ("Adaeze Okafor", LearnerLanguage.English, "F"), ("Tunde Bakare", LearnerLanguage.Yoruba, "M"),
            ("Aisha Bello", LearnerLanguage.Hausa, "F"), ("Chinedu Eze", LearnerLanguage.English, "M"),
            ("Folake Adeyemi", LearnerLanguage.Yoruba, "F"), ("Ibrahim Musa", LearnerLanguage.Hausa, "M"),
            ("Ngozi Nwosu", LearnerLanguage.English, "F"), ("Yusuf Abdullahi", LearnerLanguage.Hausa, "M"),
            ("Kemi Ogunleye", LearnerLanguage.Yoruba, "F"), ("Emeka Obi", LearnerLanguage.English, "M"),
            ("Zainab Lawal", LearnerLanguage.Hausa, "F"), ("Segun Afolabi", LearnerLanguage.Yoruba, "M"),
        };
        var ke = new (string Name, LearnerLanguage Lang, string Gender)[]
        {
            ("Wanjiru Kamau", LearnerLanguage.Swahili, "F"), ("Otieno Ochieng", LearnerLanguage.Swahili, "M"),
            ("Akinyi Odhiambo", LearnerLanguage.English, "F"), ("Baraka Mwangi", LearnerLanguage.Swahili, "M"),
            ("Amina Hassan", LearnerLanguage.Swahili, "F"), ("Kipchumba Ruto", LearnerLanguage.English, "M"),
        };
        int i = 0;
        foreach (var (name, lang, g) in ng) r.AddStudent(pseudo, pii, $"s-ng-{++i:D2}", name, "cls-ng-p4a", "sch-ng-01", lang, g, $"080{31000000 + i * 7919:D8}");
        i = 0;
        foreach (var (name, lang, g) in ke) r.AddStudent(pseudo, pii, $"s-ke-{++i:D2}", name, "cls-ke-p4b", "sch-ke-01", lang, g, $"+254 7{12000000 + i * 104729:D8}");
        return r;
    }

    private void AddStudent(Pseudonymizer pseudo, PiiGateway pii, string id, string name, string cls, string school,
        LearnerLanguage lang, string gender, string phone)
    {
        Add(new Student
        {
            Id = id, PseudonymId = pseudo.Pseudonym(id), FullName = name, ClassId = cls, SchoolId = school,
            HomeLanguage = lang, Gender = gender, GuardianPhone = phone, GuardianConsent = true
        });
        pii.Roster.AddName(name);
    }
}
