using System.Text.RegularExpressions;

namespace PathShortener.Core.Abbreviation;

/// <summary>
/// Виявлення дат у назвах папок/файлів. Дати — завжди значущі дані:
/// їх НЕ можна віддавати моделі на скорочення і НЕ можна зрізати обрізанням.
/// Захист реалізовано кодом (детерміновано), а не промптом.
/// Підтримувані формати: 09.01.2025 / 09_01_2025 / 09-01-2025 / 09/01/2025,
/// 2025-01-09 (рік попереду), 01.2025 (місяць.рік), «13 червня 2025», самотній рік 1900–2099.
/// </summary>
public static class DateTokens
{
    private const string Months =
        "січня|лютого|березня|квітня|травня|червня|липня|серпня|вересня|жовтня|листопада|грудня";

    private static readonly Regex DatePattern = new(
        @"(?<![0-9])(" +
        @"\d{1,2}[._\-/]\d{1,2}[._\-/](?:19|20)\d{2}" +          // дд.мм.рррр
        @"|(?:19|20)\d{2}[._\-/]\d{1,2}(?:[._\-/]\d{1,2})?" +    // рррр.мм[.дд]
        @"|\d{1,2}[\s_]+(?:" + Months + @")[\s_]+(?:19|20)\d{2}" + // 13 червня 2025 / 13_червня_2025
        @"|\d{1,2}[._\-/](?:19|20)\d{2}" +                       // мм.рррр
        @"|(?:19|20)\d{2}" +                                     // самотній рік
        @")(?![0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Вирізає всі дати; повертає залишок (з пробілами на місці дат) і список дат у порядку появи.</summary>
    public static (string Remainder, IReadOnlyList<string> Dates) Extract(string s)
    {
        var dates = new List<string>();
        string rest = DatePattern.Replace(s, m => { dates.Add(m.Value); return " "; });
        return (rest, dates);
    }

    /// <summary>Провідні роздільники рядка ("_", "-", ".", пробіли) — щоб не втрачати їх при перескладанні.</summary>
    public static string LeadingSeparators(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is '_' or '-' or ' ' or '.')) i++;
        return s[..i];
    }

    /// <summary>Роздільник для приклеювання дат назад: як в оригінальній назві.</summary>
    public static string JoinSeparator(string original) => original.Contains('_') ? "_" : " ";

    private static readonly Regex TextualDate = new(
        @"^(\d{1,2})[\s_]+(" + Months + @")[\s_]+((?:19|20)\d{2})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] MonthList =
    {
        "січня", "лютого", "березня", "квітня", "травня", "червня",
        "липня", "серпня", "вересня", "жовтня", "листопада", "грудня"
    };

    /// <summary>
    /// Нормалізує «текстову» дату до короткого формату: «13 червня 2025» → «13.06.2025».
    /// Дати, що вже короткі (09_01_2025, 21.04.2021…), повертаються без змін.
    /// </summary>
    public static string NormalizeDate(string date)
    {
        var m = TextualDate.Match(date.Trim());
        if (!m.Success) return date;
        int month = Array.FindIndex(MonthList,
            x => x.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        return $"{int.Parse(m.Groups[1].Value):00}.{month:00}.{m.Groups[3].Value}";
    }
}

/// <summary>
/// Обгортка над будь-яким провайдером скорочень, що гарантує збереження дат:
/// дати вирізаються ДО виклику внутрішнього провайдера (модель їх навіть не бачить)
/// і приклеюються назад ПІСЛЯ. Кеш варто ставити ВСЕРЕДИНУ цієї обгортки —
/// тоді однакові назви з різними датами діляться одним записом кешу.
/// </summary>
public sealed class DatePreservingAbbreviator : IAbbreviationProvider
{
    private readonly IAbbreviationProvider _inner;
    private readonly bool _normalizeDates;

    /// <param name="normalizeDates">Скорочувати «текстові» дати до дд.мм.рррр (зміст дати не втрачається).</param>
    public DatePreservingAbbreviator(IAbbreviationProvider inner, bool normalizeDates = false)
    {
        _inner = inner;
        _normalizeDates = normalizeDates;
    }

    public string Abbreviate(string core)
    {
        var (rest, dates) = DateTokens.Extract(core);
        if (dates.Count == 0) return _inner.Abbreviate(core);

        string sep = DateTokens.JoinSeparator(core);
        string lead = DateTokens.LeadingSeparators(core);
        var outDates = _normalizeDates ? dates.Select(DateTokens.NormalizeDate) : dates;
        string joined = string.Join(sep, outDates);
        string abbr = string.IsNullOrWhiteSpace(rest) ? "" : _inner.Abbreviate(rest).Trim();
        return abbr.Length == 0 ? lead + joined : lead + abbr + sep + joined;
    }
}

/// <summary>
/// Обрізання, що ніколи не зрізає дати: обрізається лише «текстова» частина,
/// дати лишаються цілими навіть якщо через це запис не вписався в бюджет
/// (тоді він чесно позначається «поза бюджетом»).
/// </summary>
public static class DateAwareCompaction
{
    public static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;

        var (rest, dates) = DateTokens.Extract(s);
        if (dates.Count == 0) return Compaction.Truncate(s, maxLen);

        string sep = DateTokens.JoinSeparator(s);
        string lead = DateTokens.LeadingSeparators(s);
        string joined = string.Join(sep, dates);

        int room = maxLen - lead.Length - joined.Length - sep.Length;
        string head = rest.Trim(' ', '_', '-', '.');
        if (room < 4 || head.Length == 0)
            return lead + joined; // місця нема — дата важливіша за текст

        return lead + Compaction.Truncate(head, room) + sep + joined;
    }
}
