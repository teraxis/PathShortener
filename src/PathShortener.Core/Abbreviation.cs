using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PathShortener.Core.Abbreviation;

/// <summary>
/// Розкладена назва сегмента: провідна нумерація ("3.1."), змістове ядро, розширення (для файлів).
/// Нумерація та розширення завжди зберігаються — скорочується лише ядро.
/// </summary>
public readonly record struct NameParts(string Prefix, string Core, string Extension)
{
    public string Compose(string newCore) => Prefix + newCore + Extension;
    public override string ToString() => Prefix + Core + Extension;
}

public static class NameParser
{
    // "3.1.", "3.1 ", "12) ", "1.2.3 -" тощо на початку.
    private static readonly Regex LeadingNumber =
        new(@"^\s*\d+(?:[.\-]\d+)*[.\)]?[\s\-–—]*", RegexOptions.Compiled);

    public static NameParts Split(string name, bool isDirectory)
    {
        var prefixMatch = LeadingNumber.Match(name);
        string prefix = prefixMatch.Success ? prefixMatch.Value : "";
        string rest = name.Substring(prefix.Length);

        string ext = "";
        if (!isDirectory)
        {
            int dot = rest.LastIndexOf('.');
            // розширення — лише коротке (1..8) і не порожнє ядро перед ним
            if (dot > 0 && dot < rest.Length - 1 && rest.Length - dot - 1 <= 8)
            {
                ext = rest.Substring(dot);
                rest = rest.Substring(0, dot);
            }
        }
        return new NameParts(prefix, rest, ext);
    }
}

public static class NameNormalizer
{
    private static readonly Regex LeadingNumber =
        new(@"^\s*\d+(?:[.\-]\d+)*[.\)]?\s*", RegexOptions.Compiled);
    private static readonly Regex NonWord = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    // Часті українські відмінкові/родові закінчення — грубе «стемування»,
    // щоб різні відмінки однієї назви мапились на один ключ кешу.
    private static readonly string[] Endings =
    {
        "ими", "ього", "ої", "ій", "их", "ам", "ями", "ах", "ів", "ом",
        "ою", "ей", "ям", "я", "ю", "і", "и", "е", "а", "у", "о", "й"
    };

    /// <summary>Ключ пошуку в кеші: без нумерації, без пунктуації, нижній регістр.</summary>
    public static string NormalizeKey(string name)
    {
        string s = name.ToLower(CultureInfo.GetCultureInfo("uk-UA"));
        s = LeadingNumber.Replace(s, "");
        s = NonWord.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim();
        return s;
    }

    /// <summary>
    /// Значущі слова назви ЗІ ЗБЕРЕЖЕННЯМ РЕГІСТРУ (та сама токенізація, що й NormalizeKey,
    /// але без переведення в нижній регістр) — щоб акроніми (ВККСУ, ВРП) не втрачали великі літери.
    /// </summary>
    public static IReadOnlyList<string> Words(string name)
    {
        string s = LeadingNumber.Replace(name, "");
        s = NonWord.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim();
        return s.Length == 0 ? Array.Empty<string>() : s.Split(' ');
    }

    /// <summary>Стем-ключ: NormalizeKey + обрізання типових закінчень кожного слова.</summary>
    public static string StemKey(string name)
    {
        var words = NormalizeKey(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            string stem = w;
            foreach (var end in Endings)
            {
                if (stem.Length - end.Length >= 3 && stem.EndsWith(end, StringComparison.Ordinal))
                {
                    stem = stem[..^end.Length];
                    break;
                }
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(stem);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Джерело змістового скорочення одного сегмента.
/// Реальна реалізація: кеш патернів → фолбек Ollama. Цей інтерфейс навмисно
/// відокремлений від детермінованого планувальника: модель відповідає лише
/// на питання «як коротко й зрозуміло», а бюджет і колізії рахує код планувальника.
/// Реалізація ЗОБОВ'ЯЗАНА бути детермінованою для однакового входу (це гарантує кеш).
/// </summary>
public interface IAbbreviationProvider
{
    /// <summary>Найкраще змістове скорочення ядра назви (без нумерації/розширення).</summary>
    string Abbreviate(string core);
}

/// <summary>
/// Офлайн, суто-правиловий провайдер: словник відомих абревіатур + прибирання
/// службових слів + акронім. Використовується і як фолбек в OllamaAbbreviator,
/// і в тестах. Детермінований.
/// </summary>
public sealed class RuleBasedAbbreviator : IAbbreviationProvider
{
    // Фрази: масив стем-слів → скорочення (сортуються за спаданням довжини).
    private readonly List<(string[] stems, string abbr)> _phrases;
    // Корені слів: стем-корінь → скорочення (префіксне матчування).
    private readonly List<(string root, string abbr)> _wordRoots;
    // Службові корені: якщо слово починається з кореня — прибрати.
    private readonly string[] _stopRoots;
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("uk-UA");

    private readonly bool _allowAcronym;

    public RuleBasedAbbreviator(
        IReadOnlyDictionary<string, string>? phraseDict = null,
        IReadOnlyDictionary<string, string>? wordDict = null,
        IEnumerable<string>? stopWords = null,
        bool allowAcronym = true)
    {
        _allowAcronym = allowAcronym;
        _phrases = (phraseDict ?? new Dictionary<string, string>())
            .Select(kv => (stems: kv.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries), abbr: kv.Value))
            .Where(p => p.stems.Length > 0)
            .OrderByDescending(p => p.stems.Length)
            .ToList();

        _wordRoots = (wordDict ?? DefaultWordDict)
            .Select(kv => (root: kv.Key, kv.Value))
            .OrderByDescending(p => p.root.Length)
            .ToList();

        _stopRoots = (stopWords ?? DefaultStopWords).OrderByDescending(s => s.Length).ToArray();
    }

    public string Abbreviate(string core)
    {
        if (string.IsNullOrWhiteSpace(core)) return core;

        // Слова зі збереженням регістру (щоб акроніми ВККСУ/ВРП не ставали «Вкксу»),
        // стеми (нижній регістр) — лише для матчування зі словником/стоп-словами.
        var origWords = NameNormalizer.Words(core).ToArray();
        if (origWords.Length == 0) return core.Trim();
        var stems = origWords.Select(StemWord).ToArray();

        var kept = new List<string>();
        int i = 0;
        while (i < origWords.Length)
        {
            // 1) спроба знайти відому ФРАЗУ (найдовшу) з позиції i
            var phrase = MatchPhrase(stems, i);
            if (phrase is { } ph)
            {
                kept.Add(ph.abbr);
                i += ph.len;
                continue;
            }
            // 2) корінь слова у словнику (префіксно)
            string? wordAbbr = MatchWordRoot(stems[i]);
            if (wordAbbr is not null) { kept.Add(wordAbbr); i++; continue; }
            // 3) службове слово — прибираємо
            if (IsStop(stems[i])) { i++; continue; }
            // 4) інакше лишаємо оригінал
            kept.Add(origWords[i]);
            i++;
        }
        if (kept.Count == 0) kept.AddRange(origWords);

        string result = CapitalizeJoin(kept);

        // 5) якщо все одно задовго — акронім лише зі значущих (уже відфільтрованих) слів
        //    (лише коли акроніми дозволено — ТЗ забороняє «кожне слово до першої літери» без явного правила)
        if (_allowAcronym && result.Length > 24 && kept.Count >= 2)
        {
            var acr = new StringBuilder();
            foreach (var w in kept)
            {
                var t = w.TrimStart('.', ' ');
                if (t.Length <= 2) continue; // пропускаємо дрібні сполучники
                acr.Append(char.ToUpper(t[0], Uk));
            }
            if (acr.Length is >= 2 and <= 8) return acr.ToString();
        }
        return result;
    }

    private (string abbr, int len)? MatchPhrase(string[] stems, int start)
    {
        foreach (var (pstems, abbr) in _phrases) // вже за спаданням довжини
        {
            if (start + pstems.Length > stems.Length) continue;
            bool ok = true;
            for (int k = 0; k < pstems.Length; k++)
                if (!stems[start + k].StartsWith(pstems[k], StringComparison.Ordinal)
                    && !pstems[k].StartsWith(stems[start + k], StringComparison.Ordinal))
                { ok = false; break; }
            if (ok) return (abbr, pstems.Length);
        }
        return null;
    }

    private string? MatchWordRoot(string stem)
    {
        foreach (var (root, abbr) in _wordRoots)
            if (stem.StartsWith(root, StringComparison.Ordinal)) return abbr;
        return null;
    }

    private bool IsStop(string stem)
    {
        foreach (var root in _stopRoots)
        {
            if (root.Length <= 3)
            {
                // короткі сполучники/прийменники — лише точний збіг ("та", а не "таблиця")
                if (string.Equals(stem, root, StringComparison.Ordinal)) return true;
            }
            else if (stem.StartsWith(root, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string StemWord(string w)
    {
        var s = NameNormalizer.StemKey(w);
        return s.Length == 0 ? w : s;
    }

    private static string CapitalizeJoin(IEnumerable<string> words)
    {
        var ci = CultureInfo.GetCultureInfo("uk-UA");
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (w.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToUpper(w[0], ci));
            if (w.Length > 1) sb.Append(w.AsSpan(1));
        }
        return sb.ToString();
    }

    public static readonly IReadOnlyDictionary<string, string> DefaultWordDict =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // корінь (префікс стему) → читабельне скорочення
            ["відділ"] = "Відд.",
            ["управлінн"] = "Упр.",
            ["департамент"] = "Деп.",
            ["міністерств"] = "М-во",
            ["комітет"] = "К-т",
            ["комісі"] = "Коміс.",
            ["секретар"] = "Секр.",
            ["документ"] = "Док.",
            ["протокол"] = "Прот.",
            ["рішенн"] = "Ріш.",
            ["засідан"] = "Засід.",
            ["додаток"] = "Дод.",
            ["розпорядженн"] = "Розп.",
            ["постанов"] = "Пост.",
            // юридично-адміністративні слова — зберігаємо зміст, лише коротшаємо
            ["гаранті"] = "гар.",
            ["незалежн"] = "незал.",
            ["втручанн"] = "втруч.",
            ["звернен"] = "звер.",
            ["повідомленн"] = "повід.",
            ["дотриманн"] = "дотрим.",
            ["діяльніст"] = "діяльн.",
            ["діяльнос"] = "діяльн.",
        };

    public static readonly IReadOnlyList<string> DefaultStopWords = new[]
    {
        // суто службові слова — прибираємо повністю
        "забезпеченн", "реалізаці", "повноважен", "член", "щодо", "стосовно",
        "та", "і", "й", "з", "із", "зі", "для", "про", "від", "до", "на", "у", "в",
        "питанн", "справ", "загальн", "організаці",
        "задля", "внаслідок", "протягом", "згідно", "відповідно", "шляхом",
    };
}

/// <summary>Механічна (несемантична) компакція — застосовується коли словник/модель не дають достатнього скорочення.</summary>
public static class Compaction
{
    private const string Vowels = "аеєиіїоуюяAEIOUYaeiouyАЕЄИІЇОУЮЯ";

    /// <summary>Прибирає голосні всередині довгих слів (перша літера слова лишається). Застаріле — нечитабельно.</summary>
    public static string VowelDrop(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length <= 4) continue;
            var sb = new StringBuilder();
            sb.Append(w[0]);
            for (int j = 1; j < w.Length; j++)
                if (Vowels.IndexOf(w[j]) < 0) sb.Append(w[j]);
            if (sb.Length >= 2) words[i] = sb.ToString();
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Читабельне скорочення: кожне довге слово обрізається до впізнаваного КОРЕНЯ + крапка
    /// («незалежності»→«незал.», «гарантій»→«гар.», «Дотримання»→«Дотрим.»), а не викидає голосні.
    /// Акроніми (ВККСУ), короткі слова, номери й дати лишаються цілими.
    /// Значно зрозуміліше за VowelDrop: назва читається з першого погляду.
    /// </summary>
    public static string ClipWords(string s, int keep = 6)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length <= keep + 1) continue;              // уже короткі — лишаємо
            if (w.Any(char.IsDigit)) continue;               // номери/дати — не чіпаємо
            if (w.All(c => !char.IsLetter(c) || char.IsUpper(c))) continue; // акроніми (ВККСУ) — лишаємо
            string head = w[..keep].TrimEnd('.', '-', '_', '\'', '’');
            if (head.Length >= 2) words[i] = head + ".";
        }
        return string.Join(' ', words);
    }

    /// <summary>Жорстке обрізання до maxLen із коротким детермінованим хеш-суфіксом для унікальності.</summary>
    public static string Truncate(string s, int maxLen)
    {
        if (maxLen < 4) maxLen = 4;
        if (s.Length <= maxLen) return s;
        string hash = ShortHash(s);            // 2 символи
        int keep = Math.Max(1, maxLen - hash.Length - 1);
        return s[..keep] + "~" + hash;
    }

    /// <summary>Детермінований короткий хеш (base36, 2 символи) — для унікальності при обрізанні.</summary>
    public static string ShortHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in s) { h ^= c; h *= 16777619u; }
            const string alpha = "0123456789abcdefghijklmnopqrstuvwxyz";
            return $"{alpha[(int)(h % 36)]}{alpha[(int)((h / 36) % 36)]}";
        }
    }
}
