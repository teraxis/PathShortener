using System.Text.RegularExpressions;

namespace PathShortener.Core.Abbreviation;

/// <summary>
/// Виявлення ПІБ (прізвищ з ініціалами) у назвах. Створювати абревіатури з імен,
/// прізвищ та ініціалів ЗАБОРОНЕНО — такі сегменти не скорочуються і не
/// передаються моделі взагалі (гарантія коду, а не промпту).
/// </summary>
public static class PersonNames
{
    // Одиночна велика літера (ініціал) — не частина слова.
    private const string Init = @"(?<![\p{L}])\p{Lu}(?![\p{L}])";

    private static readonly Regex[] Patterns =
    {
        // ВЕЛИКЕ ПРІЗВИЩЕ + 1–2 ініціали: "ЛЕВЧУК_О_О", "ЛЕМІЩЕНКО О. О."
        new(@"[\p{Lu}ʼ'’]{4,}[\s_.,-]+" + Init + @"([\s_.,-]+" + Init + @")?", RegexOptions.Compiled),
        // Прізвище + ініціали: "Чаричанський П.О.", "Кандзюба_О_О"
        new(@"\p{Lu}[\p{Ll}ʼ'’]{2,}[\s_]+" + Init + @"[._\s]{0,2}" + Init, RegexOptions.Compiled),
        // Ініціали + прізвище: "П_О_Лукʼянов", "О.О. Кандзюба"
        new(Init + @"[._\s]{1,2}" + Init + @"[\s_.,-]+\p{Lu}[\p{Ll}ʼ'’]{2,}", RegexOptions.Compiled),
    };

    public static bool ContainsPersonName(string s)
    {
        foreach (var p in Patterns)
            if (p.IsMatch(s)) return true;
        return false;
    }
}

/// <summary>
/// Валідатор скорочень поверх будь-якого провайдера (правила п. 3.2/3.3/23 ТЗ):
///   • сегмент із ПІБ → оригінал без змін, внутрішній провайдер НЕ викликається;
///   • порожній результат або результат, що не зменшує довжину, → оригінал;
///   • «сирий акронім» (суцільні великі літери), якщо він не із затвердженого
///     словника і акроніми не дозволені явно, → оригінал.
/// Затверджені скорочення (dict/manual) пропускаються без обмежень.
/// </summary>
public sealed class SafeAbbreviator : IAbbreviationProvider
{
    private static readonly Regex RawAcronym = new(@"^[\p{Lu}]{2,10}$", RegexOptions.Compiled);

    private readonly IAbbreviationProvider _inner;
    private readonly Func<string, string, bool>? _isApproved; // (оригінал, скорочення) → чи зі словника
    private readonly bool _allowAcronyms;

    public SafeAbbreviator(IAbbreviationProvider inner,
        Func<string, string, bool>? isApproved = null, bool allowAcronyms = false)
    {
        _inner = inner;
        _isApproved = isApproved;
        _allowAcronyms = allowAcronyms;
    }

    public string Abbreviate(string core)
    {
        if (string.IsNullOrWhiteSpace(core)) return core;
        if (PersonNames.ContainsPersonName(core)) return core;

        string result = _inner.Abbreviate(core)?.Trim() ?? "";
        if (result.Length == 0 || result.Length >= core.Trim().Length) return core;
        if (_isApproved?.Invoke(core, result) == true) return result;
        if (!_allowAcronyms && RawAcronym.IsMatch(result)) return core;
        return result;
    }
}
