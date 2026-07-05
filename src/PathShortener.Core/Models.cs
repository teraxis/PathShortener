namespace PathShortener.Core.Models;

/// <summary>Один запис усередині архіву (файл або папка), відносний шлях із роздільниками '/' або '\'.</summary>
public sealed record ArchiveEntry(string RelativePath, bool IsDirectory);

/// <summary>Окремі ліміти довжини ІМЕНІ (сегмента), незалежно від бюджету повного шляху. 0 — вимкнено.</summary>
public sealed record SegmentLimits(int MaxFileNameLength = 0, int MaxFolderNameLength = 0)
{
    public bool Any => MaxFileNameLength > 0 || MaxFolderNameLength > 0;
}

/// <summary>Результат планування для одного запису.</summary>
public sealed record PlanEntry(
    string OriginalRelativePath,
    string NewRelativePath,
    int FinalFullLength,
    bool WithinBudget,
    bool WasShortened,
    bool WasTruncated);

/// <summary>Повний план розпакування: відповідність старий → новий шлях і зведення.</summary>
public sealed class ShorteningPlan
{
    public required string DestinationRoot { get; init; }
    public required int Budget { get; init; }
    public required IReadOnlyList<PlanEntry> Entries { get; init; }

    public int TotalEntries => Entries.Count;
    public int ShortenedCount => Entries.Count(e => e.WasShortened);
    public int TruncatedCount => Entries.Count(e => e.WasTruncated);
    public int OverBudgetCount => Entries.Count(e => !e.WithinBudget);
    public bool AllFit => OverBudgetCount == 0;
}
