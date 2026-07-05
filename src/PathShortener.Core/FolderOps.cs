using PathShortener.Core.Models;

namespace PathShortener.Core.Folders;

/// <summary>
/// Сканер папки на диску: перетворює дерево на список ArchiveEntry — той самий вхід,
/// що й у архівів, тож планувальник працює без змін.
/// Обрана папка входить у список САМА (перший запис), шляхи відносні до її БАТЬКІВСЬКОЇ
/// папки — тобто скорочуватися може й ім'я самої обраної папки.
/// </summary>
public static class FolderScanner
{
    public static IReadOnlyList<ArchiveEntry> Scan(string rootFolder)
    {
        rootFolder = Path.GetFullPath(rootFolder).TrimEnd('\\', '/');
        string parent = Path.GetDirectoryName(rootFolder)
            ?? throw new ArgumentException("Корінь диска обробляти не можна — оберіть вкладену папку.");

        var list = new List<ArchiveEntry> { new(Path.GetFileName(rootFolder), true) };
        var root = new DirectoryInfo(rootFolder);
        foreach (var fsi in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
            list.Add(new ArchiveEntry(Path.GetRelativePath(parent, fsi.FullName), isDir));
        }
        return list;
    }
}

/// <summary>
/// Застосовує ShorteningPlan до реальної файлової системи.
/// Два режими: перейменування на місці та копіювання у нову теку.
/// Журнал відповідностей слід записати ДО виклику цих методів.
/// </summary>
public static class FolderPlanApplier
{
    /// <summary>
    /// Перейменовує папки/файли на місці згідно з планом (plan.DestinationRoot —
    /// батьківська папка обраного кореня). Порядок: від найглибших до кореня, щоб
    /// шлях кожного вузла на момент перейменування ще складався зі старих імен предків.
    /// На кожному рівні — у дві фази (через тимчасові імена), щоб нове ім'я не
    /// зіткнулося зі старим ім'ям сусіда, який сам чекає перейменування.
    /// Повертає кількість перейменованих вузлів.
    /// </summary>
    public static int ApplyInPlace(ShorteningPlan plan, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        string root = plan.DestinationRoot;

        // Операція потрібна лише вузлам, у яких змінився ВЛАСНИЙ (останній) сегмент.
        var ops = new List<(int Depth, string DirRel, string FromName, string ToName)>();
        foreach (var e in plan.Entries)
        {
            var origParts = e.OriginalRelativePath.Split('\\');
            var newParts = e.NewRelativePath.Split('\\');
            if (string.Equals(origParts[^1], newParts[^1], StringComparison.Ordinal)) continue;
            ops.Add((origParts.Length, string.Join('\\', origParts[..^1]), origParts[^1], newParts[^1]));
        }

        int total = ops.Count * 2, done = 0;
        foreach (var level in ops.GroupBy(o => o.Depth).OrderByDescending(g => g.Key))
        {
            var pending = new List<(string Dir, string TmpName, string ToName)>();
            foreach (var op in level)
            {
                ct.ThrowIfCancellationRequested();
                string dir = op.DirRel.Length == 0 ? root : Path.Combine(root, op.DirRel);
                // Тимчасове ім'я, гарантовано вільне в цій папці.
                string tmp; int k = 0;
                do { tmp = op.ToName + "~ps" + k++; }
                while (Exists(Path.Combine(dir, tmp)));

                Move(Path.Combine(dir, op.FromName), Path.Combine(dir, tmp));
                pending.Add((dir, tmp, op.ToName));
                progress?.Report((++done, total));
            }
            foreach (var p in pending)
            {
                Move(Path.Combine(p.Dir, p.TmpName), Path.Combine(p.Dir, p.ToName));
                progress?.Report((++done, total));
            }
        }
        return ops.Count;
    }

    /// <summary>
    /// Копіює дерево у нове місце зі скороченими іменами (оригінал недоторканий).
    /// sourceParent — батьківська папка обраного кореня (відносні шляхи плану рахуються від неї),
    /// plan.DestinationRoot — обрана папка призначення.
    /// </summary>
    public static void ApplyCopy(
        ShorteningPlan plan,
        string sourceParent,
        IReadOnlyList<ArchiveEntry> sourceEntries,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var dirPaths = sourceEntries.Where(e => e.IsDirectory)
            .Select(e => e.RelativePath.Replace('/', '\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int total = plan.Entries.Count, done = 0;
        // Папки спершу (за глибиною), потім файли — батьківські теки завжди готові.
        foreach (var e in plan.Entries.OrderBy(e => dirPaths.Contains(e.OriginalRelativePath) ? 0 : 1)
                     .ThenBy(e => e.NewRelativePath.Count(c => c == '\\')))
        {
            ct.ThrowIfCancellationRequested();
            string src = Path.Combine(sourceParent, e.OriginalRelativePath);
            string dst = Path.Combine(plan.DestinationRoot, e.NewRelativePath);
            if (dirPaths.Contains(e.OriginalRelativePath))
            {
                Directory.CreateDirectory(LongPath(dst));
            }
            else
            {
                Directory.CreateDirectory(LongPath(Path.GetDirectoryName(dst)!));
                File.Copy(LongPath(src), LongPath(dst), overwrite: true);
            }
            progress?.Report((++done, total));
        }
    }

    private static void Move(string from, string to)
    {
        string f = LongPath(from), t = LongPath(to);
        if (Directory.Exists(f)) Directory.Move(f, t);
        else File.Move(f, t);
    }

    private static bool Exists(string p)
        => File.Exists(LongPath(p)) || Directory.Exists(LongPath(p));

    /// <summary>Префікс \\?\ — робота зі шляхами понад 260 символів.</summary>
    private static string LongPath(string p)
        => p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p : @"\\?\" + Path.GetFullPath(p);
}
