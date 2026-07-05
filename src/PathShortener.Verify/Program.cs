using PathShortener.Core.Abbreviation;
using PathShortener.Core.Cache;
using PathShortener.Core.Models;
using PathShortener.Core.Planning;

int failures = 0;

// Провайдер: словник відомих держ-абревіатур + правила. (У бойовому режимі — CachedAbbreviator(Ollama).)
var phraseDict = new Dictionary<string, string>(StringComparer.Ordinal)
{
    [NameNormalizer.StemKey("Вища рада правосуддя")] = "ВРП",
    [NameNormalizer.StemKey("Вищої ради правосуддя")] = "ВРП",
    [NameNormalizer.StemKey("Кабінет Міністрів України")] = "КМУ",
    [NameNormalizer.StemKey("Міністерство освіти і науки")] = "МОН",
};
var provider = new RuleBasedAbbreviator(phraseDict);
var cache = new InMemoryPatternCache();
var cached = new CachedAbbreviator(provider, cache, "rule");
var planner = new ShorteningPlanner(cached);

// ---------- Сценарій 1: реальний кейс користувача ----------
string destRoot = @"D:\Обмін\Вхідні документи\2026\Липень\Розпаковано";
var deepName = "3.1. Відділ забезпечення реалізації повноважень членів Вищої ради правосуддя";
var entries1 = new List<ArchiveEntry>();
for (int i = 1; i <= 5; i++)
    entries1.Add(new ArchiveEntry(
        $@"{deepName}\{deepName}\Протокол засідання №{i} щодо реалізації повноважень членів Вищої ради правосуддя.docx",
        false));
entries1.Add(new ArchiveEntry($@"{deepName}\{deepName}\Додаток.pdf", false));

var plan1 = planner.Plan(destRoot, entries1, budget: 259);
PrintPlan("Сценарій 1 — реальний кейс ВРП", plan1);
CheckInvariants("Сценарій 1", plan1, ref failures);

// ---------- Сценарій 2: колізії після скорочення ----------
// Дві різні назви, що можуть скоротитись однаково → мусять лишитись унікальними.
var entries2 = new List<ArchiveEntry>
{
    new(@"Відділ організації\Документ протокол рішення.docx", false),
    new(@"Відділ організації\Документ протокол розпорядження.docx", false),
    new(@"Відділ організації\Документ протокол постанова.docx", false),
};
var plan2 = planner.Plan(@"C:\out", entries2, budget: 259);
PrintPlan("Сценарій 2 — контроль колізій", plan2);
CheckInvariants("Сценарій 2", plan2, ref failures);

// ---------- Сценарій 3: екстремальна вкладеність, вимагає обрізання ----------
var seg = "Відділ забезпечення реалізації повноважень членів Вищої ради правосуддя України та інших органів";
var parts3 = new List<string>();
for (int i = 0; i < 6; i++) parts3.Add($"{i + 1}. {seg}");
string rel = string.Join('\\', parts3) + @"\Дуже довгий підсумковий протокол засідання членів ради.docx";
var plan3 = planner.Plan(@"D:\Розпаковано\Архів №2026-0703", new List<ArchiveEntry> { new(rel, false) }, budget: 259);
PrintPlan("Сценарій 3 — екстремальна вкладеність", plan3);
CheckInvariants("Сценарій 3", plan3, ref failures);

// ---------- Сценарій 4: короткі шляхи не чіпаємо ----------
var entries4 = new List<ArchiveEntry>
{
    new(@"Папка\файл.txt", false),
    new(@"Папка\звіт.pdf", false),
};
var plan4 = planner.Plan(@"C:\x", entries4, budget: 259);
PrintPlan("Сценарій 4 — короткі шляхи без змін", plan4);
CheckInvariants("Сценарій 4", plan4, ref failures);
foreach (var e in plan4.Entries)
    if (e.WasShortened) { Console.WriteLine($"  ПОМИЛКА: короткий шлях змінено: {e.OriginalRelativePath}"); failures++; }

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "✔ УСІ ІНВАРІАНТИ ВИКОНАНО"
    : $"�’ ПРОВАЛЕНО ПЕРЕВІРОК: {failures}");
return failures == 0 ? 0 : 1;


// ================= допоміжні =================

static void PrintPlan(string title, ShorteningPlan p)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
    Console.WriteLine($"Кореневий шлях: {p.DestinationRoot}  (бюджет {p.Budget})");
    Console.WriteLine($"Записів: {p.TotalEntries} | скорочено: {p.ShortenedCount} | обрізано: {p.TruncatedCount} | поза бюджетом: {p.OverBudgetCount}");
    foreach (var e in p.Entries)
    {
        string flag = e.WithinBudget ? " " : "!";
        Console.WriteLine($" [{flag}] {e.FinalFullLength,3}  {e.NewRelativePath}");
    }
}

static void CheckInvariants(string name, ShorteningPlan p, ref int failures)
{
    // 1) усе в межах бюджету
    foreach (var e in p.Entries)
        if (!e.WithinBudget)
        {
            Console.WriteLine($"  ПОМИЛКА [{name}]: поза бюджетом ({e.FinalFullLength}): {e.NewRelativePath}");
            failures++;
        }

    // 2) немає колізій серед сиблінгів (однакові повні нові шляхи заборонені)
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in p.Entries)
        if (!seen.Add(e.NewRelativePath))
        {
            Console.WriteLine($"  ПОМИЛКА [{name}]: дублікат нового шляху: {e.NewRelativePath}");
            failures++;
        }

    // 3) розширення файлів збережені
    foreach (var e in p.Entries)
    {
        string oe = System.IO.Path.GetExtension(e.OriginalRelativePath);
        string ne = System.IO.Path.GetExtension(e.NewRelativePath);
        if (oe.Length is > 0 and <= 9 && !string.Equals(oe, ne, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  ПОМИЛКА [{name}]: втрачено розширення '{oe}' → '{ne}': {e.NewRelativePath}");
            failures++;
        }
    }
}
