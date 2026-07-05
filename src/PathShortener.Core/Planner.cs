using System.Globalization;
using PathShortener.Core.Abbreviation;
using PathShortener.Core.Models;

namespace PathShortener.Core.Planning;

/// <summary>
/// Детермінований планувальник. Будує дерево записів архіву й скорочує назви так,
/// щоб повний шлях кожного запису вписався в бюджет (за замовч. 259 = MAX_PATH-1).
///
/// Порядок скорочення (важливо!):
///   1) Спершу скорочуємо СПІЛЬНІ папки-предки — одне скорочення розвантажує всіх нащадків.
///      Обираємо жадібно ту папку, яка дає найбільше сумарне зменшення для записів поза бюджетом.
///   2) Потім скорочуємо самі ФАЙЛИ, що ще не влізли.
///   3) Останній засіб — жорстке обрізання з хеш-суфіксом (позначається WasTruncated).
///
/// Після КОЖНОГО скорочення довжини перераховуються (перейменування папки змінює
/// шлях усім її нащадкам). Унікальність імен у межах однієї папки гарантується завжди.
/// LLM тут не викликається — лише детермінований IAbbreviationProvider (уже з кешем).
/// </summary>
public sealed class ShorteningPlanner
{
    private const int MaxStage = 3; // 0=оригінал,1=семантика,2=vowel-drop,3=обрізання
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("uk-UA");

    private readonly IAbbreviationProvider _provider;
    private readonly char[] _seps = { '/', '\\' };

    public ShorteningPlanner(IAbbreviationProvider provider) => _provider = provider;

    private sealed class Node
    {
        public string OriginalSegment = "";
        public bool IsDirectory;
        public bool IsEntry;                 // явний запис архіву
        public Node? Parent;
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public int Stage;                    // рівень скорочення сегмента
        public string Current = "";          // поточне ім'я (після скорочення + фікс колізій)
    }

    private SegmentLimits? _segmentLimits;

    public ShorteningPlan Plan(string destinationRoot, IReadOnlyList<ArchiveEntry> entries, int budget = 259,
        SegmentLimits? segmentLimits = null)
    {
        _segmentLimits = segmentLimits is { Any: true } ? segmentLimits : null;
        destinationRoot = destinationRoot.TrimEnd('/', '\\');
        var root = BuildTree(entries);

        // Початковий рендер усіх сегментів (stage 0) з гарантією унікальності серед сиблінгів.
        RenderSubtree(root, destinationRoot, budget);

        // ---- Фаза 1: скорочення папок-предків ----
        int guard = 0, maxIterations = entries.Count * MaxStage + 64;
        while (guard++ < maxIterations)
        {
            var over = CollectOverBudget(root, destinationRoot, budget);
            if (over.Count == 0) break;

            var (bestDir, bestGain) = PickBestDirectory(root, over, destinationRoot, budget);
            if (bestDir is null || bestGain <= 0) break; // папки більше не допомагають → до фаз файлів

            bestDir.Stage++;
            RenderNodeWithSiblings(bestDir, destinationRoot, budget); // сам сегмент папки (серед сиблінгів)
            RenderSubtree(bestDir, destinationRoot, budget); // перерахунок нащадків
        }

        // ---- Фаза 2: скорочення самих файлів ----
        foreach (var leaf in EnumerateEntries(root).Where(n => !n.IsDirectory)
                     .OrderByDescending(n => FullLength(n, destinationRoot)))
        {
            while (FullLength(leaf, destinationRoot) > budget && leaf.Stage < MaxStage - 1)
            {
                leaf.Stage++;
                RenderNodeWithSiblings(leaf, destinationRoot, budget);
            }
        }

        // ---- Фаза 3: жорстке обрізання (останній засіб) ----
        foreach (var leaf in EnumerateEntries(root).Where(n => !n.IsDirectory))
        {
            if (FullLength(leaf, destinationRoot) > budget)
            {
                leaf.Stage = MaxStage;
                RenderNodeWithSiblings(leaf, destinationRoot, budget);
            }
        }

        return BuildPlan(root, destinationRoot, budget);
    }

    // ---------- побудова дерева ----------

    private Node BuildTree(IReadOnlyList<ArchiveEntry> entries)
    {
        var root = new Node { OriginalSegment = "", IsDirectory = true };
        foreach (var e in entries)
        {
            var parts = e.RelativePath.Split(_seps, StringSplitOptions.RemoveEmptyEntries);
            var cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                bool dir = !last || e.IsDirectory;
                if (!cur.Children.TryGetValue(parts[i], out var child))
                {
                    child = new Node { OriginalSegment = parts[i], IsDirectory = dir, Parent = cur };
                    cur.Children[parts[i]] = child;
                }
                if (dir) child.IsDirectory = true; // проміжний вузол — завжди папка
                cur = child;
            }
            cur.IsEntry = true;
        }
        return root;
    }

    // ---------- рендеринг сегментів ----------

    private string RenderBase(Node n, string destRoot, int budget)
    {
        var parts = NameParser.Split(n.OriginalSegment, n.IsDirectory);
        string core = n.Stage switch
        {
            0 => parts.Core,
            1 => _provider.Abbreviate(parts.Core),
            // етап 2 — читабельне скорочення слів до кореня (а не викидання голосних)
            2 => Compaction.ClipWords(_provider.Abbreviate(parts.Core)),
            _ => TruncateToFit(n, parts, destRoot, budget)
        };
        return EnforceSegmentLimit(n, SanitizeSegment(parts.Compose(core)));
    }

    /// <summary>
    /// Окремий ліміт довжини ІМЕНІ (незалежно від бюджету шляху): якщо сегмент задовгий —
    /// спершу семантичне скорочення (через кеш це дешево), потім vowel-drop,
    /// в крайньому разі — обрізання, яке ніколи не зрізає дату й розширення.
    /// Імена в межах ліміту не змінюються (жодних скорочень «про запас»).
    /// </summary>
    private string EnforceSegmentLimit(Node n, string name)
    {
        int limit = n.IsDirectory
            ? _segmentLimits?.MaxFolderNameLength ?? 0
            : _segmentLimits?.MaxFileNameLength ?? 0;
        if (limit <= 0 || name.Length <= limit) return name;

        var parts = NameParser.Split(n.OriginalSegment, n.IsDirectory);
        string semantic = _provider.Abbreviate(parts.Core);
        string candidate = SanitizeSegment(parts.Compose(semantic));
        if (candidate.Length <= limit) return candidate;

        semantic = Compaction.ClipWords(semantic);
        candidate = SanitizeSegment(parts.Compose(semantic));
        if (candidate.Length <= limit) return candidate;

        int room = limit - parts.Prefix.Length - parts.Extension.Length;
        return SanitizeSegment(parts.Compose(DateAwareCompaction.Truncate(semantic, Math.Max(4, room))));
    }

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Windows-валідність імені сегмента: без кінцевих крапок/пробілів (ОС їх мовчки зрізає,
    /// і фактичний шлях розходиться з планом) і без заборонених символів (модель може
    /// повернути будь-що). Довжина від цього лише зменшується — бюджет не порушується.
    /// </summary>
    private static string SanitizeSegment(string name)
    {
        if (name.IndexOfAny(InvalidNameChars) >= 0)
        {
            var arr = name.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(InvalidNameChars, arr[i]) >= 0) arr[i] = '_';
            name = new string(arr);
        }
        string t = name.TrimEnd('.', ' ');
        return t.Length == 0 ? name : t;
    }

    private string TruncateToFit(Node n, NameParts parts, string destRoot, int budget)
    {
        // Скільки місця лишається під ядро цього сегмента, щоб повний шлях вписався.
        int fixedLen = destRoot.Length + 1
                       + AncestorsCurrentLength(n)          // предки (їхні Current)
                       + parts.Prefix.Length + parts.Extension.Length
                       + SeparatorsCount(n);
        int room = budget - fixedLen;
        string semantic = Compaction.ClipWords(_provider.Abbreviate(parts.Core));
        // Дати не зрізаються ніколи — обрізається лише текстова частина.
        return DateAwareCompaction.Truncate(semantic, Math.Max(4, room));
    }

    /// <summary>Рендер усіх сегментів піддерева з кореня node, з унікальністю серед сиблінгів.</summary>
    private void RenderSubtree(Node node, string destRoot, int budget)
    {
        // children цього вузла
        AssignUniqueChildren(node, destRoot, budget);
        foreach (var c in node.Children.Values)
            RenderSubtree(c, destRoot, budget);
    }

    private void RenderNodeWithSiblings(Node n, string destRoot, int budget)
    {
        if (n.Parent is null) return;
        AssignUniqueChildren(n.Parent, destRoot, budget);
    }

    /// <summary>Присвоює Current усім дітям вузла, гарантуючи унікальність (case-insensitive).</summary>
    private void AssignUniqueChildren(Node parent, string destRoot, int budget)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Стабільний порядок для детермінованості.
        foreach (var child in parent.Children.Values.OrderBy(c => c.OriginalSegment, StringComparer.Ordinal))
        {
            string baseName = RenderBase(child, destRoot, budget);
            string name = baseName;
            if (used.Contains(name))
            {
                // Спершу пробуємо ~1..~9, потім base36-хеш оригіналу.
                var np = NameParser.Split(baseName, child.IsDirectory);
                int k = 1;
                do
                {
                    string suffix = k <= 9 ? $"~{k}" : "~" + Compaction.ShortHash(child.OriginalSegment + k);
                    name = np.Compose(np.Core + suffix);
                    k++;
                } while (used.Contains(name) && k < 200);
            }
            used.Add(name);
            child.Current = name;
        }
    }

    // ---------- вимірювання ----------

    private static int SeparatorsCount(Node n)
    {
        int depth = 0;
        for (var p = n; p?.Parent is not null; p = p.Parent) depth++;
        return Math.Max(0, depth - 1); // роздільники МІЖ сегментами відносного шляху
    }

    private int AncestorsCurrentLength(Node n)
    {
        int sum = 0;
        for (var p = n.Parent; p?.Parent is not null; p = p.Parent) // до кореня-дерева (порожній сегмент) не рахуємо
            sum += (p.Current.Length == 0 ? p.OriginalSegment.Length : p.Current.Length);
        return sum;
    }

    private string CurrentRelPath(Node n)
    {
        var stack = new Stack<string>();
        for (var p = n; p?.Parent is not null; p = p.Parent)
            stack.Push(p.Current.Length == 0 ? p.OriginalSegment : p.Current);
        return string.Join('\\', stack);
    }

    private int FullLength(Node n, string destRoot)
        => destRoot.Length + 1 + CurrentRelPath(n).Length;

    // ---------- вибір папки для скорочення ----------

    private List<Node> CollectOverBudget(Node root, string destRoot, int budget)
    {
        var list = new List<Node>();
        foreach (var n in EnumerateEntries(root))
            if (FullLength(n, destRoot) > budget)
                list.Add(n);
        return list;
    }

    /// <summary>
    /// Обирає папку-предка, скорочення якої (на 1 стадію) дасть найбільше сумарне
    /// зменшення довжин серед записів поза бюджетом.
    /// </summary>
    private (Node? dir, int gain) PickBestDirectory(Node root, List<Node> over, string destRoot, int budget)
    {
        // Кандидати: усі папки, що є предками хоча б одного over-запису і ще можуть скорочуватись.
        var candidates = new Dictionary<Node, int>(); // dir → сумарний виграш
        foreach (var leaf in over)
        {
            for (var d = leaf.IsDirectory ? leaf : leaf.Parent; d?.Parent is not null; d = d.Parent)
            {
                if (d.Stage >= MaxStage - 1) continue; // папки труамо лише до vowel-drop, без хеш-обрізання
                int before = (d.Current.Length == 0 ? d.OriginalSegment.Length : d.Current.Length);
                int after = RenderBase(d, destRoot, budget).Length; // якою стане при +1 стадії
                // тимчасово порахувати ефект наступної стадії:
                int savedPer = before - PeekNextLength(d, destRoot, budget);
                if (savedPer <= 0) continue;
                candidates.TryGetValue(d, out var acc);
                candidates[d] = acc + savedPer;
            }
        }
        Node? best = null; int bestGain = 0;
        foreach (var kv in candidates)
            if (kv.Value > bestGain) { best = kv.Key; bestGain = kv.Value; }
        return (best, bestGain);
    }

    /// <summary>Якою була б довжина сегмента папки, якби її стадію збільшили на 1 (без застосування).</summary>
    private int PeekNextLength(Node d, string destRoot, int budget)
    {
        int saved = d.Stage;
        d.Stage++;
        int len = RenderBase(d, destRoot, budget).Length;
        d.Stage = saved;
        return len;
    }

    // ---------- обхід ----------

    private static IEnumerable<Node> EnumerateEntries(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsEntry) yield return n;
            foreach (var c in n.Children.Values) stack.Push(c);
        }
    }

    // ---------- підсумковий план ----------

    private ShorteningPlan BuildPlan(Node root, string destRoot, int budget)
    {
        var list = new List<PlanEntry>();
        foreach (var n in EnumerateEntries(root).OrderBy(n => OriginalRelPath(n), StringComparer.Ordinal))
        {
            string orig = OriginalRelPath(n);
            string neu = CurrentRelPath(n);
            int full = FullLength(n, destRoot);
            list.Add(new PlanEntry(
                OriginalRelativePath: orig,
                NewRelativePath: neu,
                FinalFullLength: full,
                WithinBudget: full <= budget,
                WasShortened: !string.Equals(orig, neu, StringComparison.Ordinal),
                WasTruncated: n.Stage >= MaxStage));
        }
        return new ShorteningPlan { DestinationRoot = destRoot, Budget = budget, Entries = list };
    }

    private static string OriginalRelPath(Node n)
    {
        var stack = new Stack<string>();
        for (var p = n; p?.Parent is not null; p = p.Parent) stack.Push(p.OriginalSegment);
        return string.Join('\\', stack);
    }
}
