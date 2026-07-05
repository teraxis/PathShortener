using PathShortener.Core.Models;

namespace PathShortener.Core.Planning;

[Flags]
public enum ConflictKind
{
    None = 0,
    DuplicateName = 1,   // два записи → однаковий новий шлях (у тій самій папці)
    InvalidChars = 2,    // недопустимі символи файлової системи
    ReservedName = 4,    // зарезервоване ім'я Windows (CON, PRN, NUL…)
    EmptyName = 8,       // порожній сегмент або лише роздільники
    ExistsOnDisk = 16,   // на диску вже є інший об'єкт із таким шляхом (попередження)
    OverBudget = 32,     // не влазить у бюджет навіть після скорочення (попередження)
}

public sealed record EntryConflict(int Index, ConflictKind Kind);

/// <summary>
/// Виявлення конфліктів у сформованому плані (ТЗ п. 24). Критичні конфлікти
/// (дублікати, недопустимі/порожні/зарезервовані імена) блокують застосування;
/// ExistsOnDisk та OverBudget — попередження.
/// </summary>
public static class ConflictDetector
{
    public const ConflictKind Critical =
        ConflictKind.DuplicateName | ConflictKind.InvalidChars |
        ConflictKind.ReservedName | ConflictKind.EmptyName;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <param name="items">пари (оригінальний відносний шлях, новий відносний шлях)</param>
    /// <param name="destinationRoot">корінь; для перевірки наявних на диску об'єктів (null — не перевіряти)</param>
    public static IReadOnlyList<EntryConflict> Analyze(
        IReadOnlyList<(string OrigRel, string NewRel)> items,
        int budget, string? destinationRoot = null)
    {
        var result = new EntryConflict[items.Count];
        for (int i = 0; i < items.Count; i++) result[i] = new EntryConflict(i, ConflictKind.None);

        // дублікати нових шляхів (без урахування регістру)
        var byPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            string key = items[i].NewRel.TrimEnd('\\', '/');
            (byPath.TryGetValue(key, out var list) ? list : byPath[key] = new List<int>()).Add(i);
        }

        for (int i = 0; i < items.Count; i++)
        {
            var kind = ConflictKind.None;
            string newRel = items[i].NewRel;

            if (byPath[newRel.TrimEnd('\\', '/')].Count > 1) kind |= ConflictKind.DuplicateName;

            foreach (var seg in newRel.Split('\\', '/'))
            {
                if (seg.Length == 0) continue;
                if (seg.IndexOfAny(Invalid) >= 0) kind |= ConflictKind.InvalidChars;
                string bare = Path.GetFileNameWithoutExtension(seg).Trim();
                if (bare.Length == 0 || seg.Trim(' ', '.', '_', '-').Length == 0) kind |= ConflictKind.EmptyName;
                if (Reserved.Contains(bare)) kind |= ConflictKind.ReservedName;
            }
            if (newRel.Trim().Length == 0) kind |= ConflictKind.EmptyName;

            if (destinationRoot is not null &&
                !string.Equals(items[i].OrigRel, newRel, StringComparison.OrdinalIgnoreCase))
            {
                string full = Path.Combine(destinationRoot, newRel);
                string lp = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + Path.GetFullPath(full);
                if (File.Exists(lp) || Directory.Exists(lp)) kind |= ConflictKind.ExistsOnDisk;
            }

            if (budget > 0 && destinationRoot is not null &&
                destinationRoot.TrimEnd('\\', '/').Length + 1 + newRel.Length > budget)
                kind |= ConflictKind.OverBudget;

            result[i] = new EntryConflict(i, kind);
        }
        return result;
    }

    public static bool HasCritical(IEnumerable<EntryConflict> conflicts)
        => conflicts.Any(c => (c.Kind & Critical) != 0);
}
