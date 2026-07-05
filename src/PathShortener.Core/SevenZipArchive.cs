using System.Diagnostics;
using System.Text;
using PathShortener.Core.Models;

namespace PathShortener.Core.Archives;

/// <summary>
/// Обгортка над 7-Zip CLI (7z.exe) для ZIP/7z/RAR.
/// Крок 1: Read — дістати список записів (структуру) без розпакування.
/// Крок 2: (планування скорочення робить ShorteningPlanner)
/// Крок 3: ExtractWithPlan — розпакувати кожен запис у новий (скорочений) шлях.
///
/// Розпакування з перейменуванням реалізовано як витягання конкретного запису
/// у тимчасове місце й переміщення на цільовий шлях. Це надійніше, ніж покладатися
/// на перейменування всередині 7-Zip, і працює однаково для всіх форматів.
/// </summary>
public sealed class SevenZipArchive
{
    private readonly string _sevenZipExe;

    public SevenZipArchive(string sevenZipExe = "7z")
        => _sevenZipExe = sevenZipExe; // або повний шлях: @"C:\Program Files\7-Zip\7z.exe"

    /// <summary>Читає структуру архіву (технічний список -slt).</summary>
    public IReadOnlyList<ArchiveEntry> Read(string archivePath)
    {
        // 7z l -slt -sccUTF-8 <archive>  (-sccUTF-8 — інакше вивід іде в OEM-кодуванні й кирилиця ламається)
        var output = Run(new[] { "l", "-slt", "-sccUTF-8", archivePath });
        var entries = new List<ArchiveEntry>();

        string? path = null;
        bool isDir = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                // новий блок — зафіксувати попередній
                Flush(entries, ref path, ref isDir);
                path = line.Substring("Path = ".Length);
                isDir = false;
            }
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal))
            {
                isDir = line.Contains('D'); // D = directory
            }
            else if (line.StartsWith("Folder = ", StringComparison.Ordinal))
            {
                if (line.EndsWith("+", StringComparison.Ordinal)) isDir = true;
            }
        }
        Flush(entries, ref path, ref isDir);

        // Перший "Path =" — сам архів; 7z друкує його так, як передано в команді (може бути абсолютним шляхом).
        string archiveName = Path.GetFileName(archivePath);
        return entries.Where(e => !Path.IsPathRooted(e.RelativePath) &&
                                  !string.Equals(e.RelativePath, archivePath, StringComparison.OrdinalIgnoreCase) &&
                                  !string.Equals(e.RelativePath, archiveName, StringComparison.OrdinalIgnoreCase))
                      .ToList();

        static void Flush(List<ArchiveEntry> list, ref string? p, ref bool d)
        {
            if (p is not null) list.Add(new ArchiveEntry(p, d));
            p = null; d = false;
        }
    }

    /// <summary>
    /// Розпаковує архів згідно з планом (старий відносний шлях → новий).
    /// Один прохід: увесь архів розпаковується ОДНИМ викликом 7z у тимчасову теку
    /// на тому ж томі, що й призначення, а далі файли миттєво переміщуються
    /// (перейменування в межах тому) на скорочені шляхи. Це в рази швидше,
    /// ніж окремий процес 7z на кожен файл.
    /// </summary>
    public void ExtractWithPlan(string archivePath, ShorteningPlan plan,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        // Тимчасова тека поряд із призначенням (той самий том) — Move не копіює дані.
        string destParent = Path.GetDirectoryName(plan.DestinationRoot) ?? plan.DestinationRoot;
        Directory.CreateDirectory(LongPath(destParent));
        string tempRoot = Path.Combine(destParent, "~psx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(LongPath(tempRoot));

        // Атрибути «папка/файл» беремо з самого архіву — евристика за розширенням
        // ламається на назвах із крапками ("3.1. Відділ…").
        var dirPaths = Read(archivePath)
            .Where(en => en.IsDirectory)
            .Select(en => en.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int total = plan.Entries.Count + 1, done = 0;
        try
        {
            Run(new[] { "x", archivePath, $"-o{tempRoot}", "-y" });
            progress?.Report((++done, total));

            foreach (var e in plan.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string finalPath = Path.Combine(plan.DestinationRoot, e.NewRelativePath);

                if (IsDirectoryEntry(e, plan, dirPaths))
                {
                    Directory.CreateDirectory(LongPath(finalPath));
                }
                else
                {
                    Directory.CreateDirectory(LongPath(Path.GetDirectoryName(finalPath)!));
                    string extracted = Path.Combine(tempRoot, e.OriginalRelativePath);
                    if (File.Exists(LongPath(extracted)))
                    {
                        if (File.Exists(LongPath(finalPath))) File.Delete(LongPath(finalPath));
                        File.Move(LongPath(extracted), LongPath(finalPath));
                    }
                }
                progress?.Report((++done, total));
            }
        }
        finally
        {
            try { Directory.Delete(LongPath(tempRoot), recursive: true); } catch { /* ignore */ }
        }
    }

    private static bool IsDirectoryEntry(PlanEntry e, ShorteningPlan plan, HashSet<string> dirPaths)
        => dirPaths.Contains(e.OriginalRelativePath) ||
           // фолбек для архівів без атрибутів: якщо є діти з таким префіксом — це папка
           plan.Entries.Any(o => o != e &&
               o.OriginalRelativePath.StartsWith(e.OriginalRelativePath + "\\", StringComparison.Ordinal));

    /// <summary>Префікс \\?\ дозволяє Windows працювати зі шляхами > 260 навіть під час переміщення.</summary>
    private static string LongPath(string p)
        => p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p : @"\\?\" + Path.GetFullPath(p);

    private string Run(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _sevenZipExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не вдалося запустити 7-Zip");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0 && proc.ExitCode != 1) // 1 = попередження (напр. пропущені файли)
            throw new InvalidOperationException($"7-Zip помилка ({proc.ExitCode}): {stderr}");
        return stdout;
    }
}
