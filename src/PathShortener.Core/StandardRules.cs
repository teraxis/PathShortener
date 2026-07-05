using System.Text.RegularExpressions;

namespace PathShortener.Core.Abbreviation;

/// <summary>
/// Опції звичайного (безмодельного) аналізу — вмикаються користувачем у налаштуваннях.
/// «Крайні» правила (акроніми, пословне обрізання) за замовчуванням ВИМКНЕНІ (ТЗ п. 8.2).
/// </summary>
public sealed class StandardRuleOptions
{
    public bool UseWordDictionary { get; set; } = true;   // скорочення типових слів за словником
    public bool DropStopWords { get; set; } = true;       // прибирати службові слова
    public bool AllowAcronyms { get; set; } = false;      // акроніми зі слів — лише явно
    public bool CollapseSeparators { get; set; } = true;  // "__" → "_", подвійні пробіли → один
    public bool RemoveDuplicateWords { get; set; } = true;
    public bool NormalizeDates { get; set; } = true;      // «13 червня 2025» → «13.06.2025» (діє в DatePreservingAbbreviator)
    public bool TruncateWords { get; set; } = false;      // пословне обрізання — крайній засіб
    public int WordMaxLength { get; set; } = 5;
}

/// <summary>
/// Ланцюг алгоритмічних правил для звичайного аналізу: нормалізація розділювачів →
/// видалення дублів слів → словник/стоп-слова (RuleBasedAbbreviator) →
/// пословне обрізання (лише якщо результат усе ще довгий). Без жодної ШІ-моделі.
/// Викликається планувальником лише для сегментів, що перевищують ліміти.
/// </summary>
public sealed class RuleChainAbbreviator : IAbbreviationProvider
{
    private static readonly Regex RepeatedSeparator = new(@"([_\-. ])\1+", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    private readonly StandardRuleOptions _o;
    private readonly RuleBasedAbbreviator _rule;

    public RuleChainAbbreviator(StandardRuleOptions? options = null)
    {
        _o = options ?? new StandardRuleOptions();
        _rule = new RuleBasedAbbreviator(
            wordDict: _o.UseWordDictionary ? null : new Dictionary<string, string>(),
            stopWords: _o.DropStopWords ? null : Array.Empty<string>(),
            allowAcronym: _o.AllowAcronyms);
    }

    public string Abbreviate(string core)
    {
        if (string.IsNullOrWhiteSpace(core)) return core;

        string s = core;
        if (_o.CollapseSeparators)
        {
            s = RepeatedSeparator.Replace(s, "$1");
            s = MultiSpace.Replace(s, " ").Trim();
        }
        if (_o.RemoveDuplicateWords) s = DedupWords(s);

        string result = _rule.Abbreviate(s);

        // Пословне обрізання — крайній варіант: лише коли інші правила не допомогли.
        if (_o.TruncateWords && result.Length > 24)
            result = TruncateWords(result, Math.Max(2, _o.WordMaxLength));

        return result;
    }

    /// <summary>Прибирає ПОСЛІДОВНІ дублікати слів (за стем-ключем, відмінки збігаються).</summary>
    private static string DedupWords(string s)
    {
        var tokens = s.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return s;
        var kept = new List<string> { tokens[0] };
        for (int i = 1; i < tokens.Length; i++)
            if (!string.Equals(NameNormalizer.StemKey(tokens[i]), NameNormalizer.StemKey(tokens[i - 1]),
                    StringComparison.Ordinal))
                kept.Add(tokens[i]);
        return kept.Count == tokens.Length ? s : string.Join(' ', kept);
    }

    /// <summary>Обрізає кожне слово до maxLen символів; токени з цифрами (номери, дати) не чіпає.</summary>
    private static string TruncateWords(string s, int maxLen)
    {
        var tokens = s.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
            if (tokens[i].Length > maxLen && !tokens[i].Any(char.IsDigit))
                tokens[i] = tokens[i][..maxLen];
        return string.Join(' ', tokens);
    }
}
