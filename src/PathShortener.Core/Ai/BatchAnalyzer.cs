using System.Text.Json;
using PathShortener.Core.Abbreviation;
using PathShortener.Core.Cache;
using PathShortener.Core.Models;

namespace PathShortener.Core.Ai;

public sealed class BatchSettings
{
    public int MaxItemsPerBatch { get; set; } = 40;
    public int Retries { get; set; } = 1;
    public int RetryDelayMs { get; set; } = 1500;
}

public sealed record BatchOutcome(int Sent, int Accepted, int Rejected, IReadOnlyList<string> Errors,
    int ModelCalls, int TokensEstimate);

/// <summary>
/// Пакетний інтелектуальний аналіз (ТЗ п. 9, 19, 26). Ключові принципи:
///   • до моделі йдуть ЛИШЕ УНІКАЛЬНІ сегменти, що реально перевищують ліміти
///     (повторювані частини шляхів — один раз; ПІБ, словникові, кешовані — не йдуть);
///   • промпт забороняє штучні абревіатури з ПІБ і вимагає валідний JSON;
///   • відповідь проходить ЛОКАЛЬНУ валідацію; невалідне не застосовується;
///   • прийняте кладеться в кеш із source='llm' (у постійний словник НЕ потрапляє
///     без підтвердження — звичайний аналіз бачить лише dict/manual).
/// Планувальник не змінюється: після префетчу він бере готові скорочення з кешу.
/// </summary>
public sealed class BatchAnalyzer
{
    private const string SystemPrompt =
        "Ти скорочуєш назви папок і файлів української держустанови так, щоб вони лишалися " +
        "КОРОТКИМИ, але ЗРОЗУМІЛИМИ ЛЮДИНІ — щоб із назви одразу було видно вміст.\n" +
        "\n" +
        "ГОЛОВНЕ ПРАВИЛО ЧИТАБЕЛЬНОСТІ:\n" +
        "• Залишай 1–3 найважливіші слова (іменники) ЦІЛИМИ й впізнаваними.\n" +
        "• Прибирай службові слова (дотримання, повідомлення, забезпечення, щодо, про, та, у, для).\n" +
        "• Використовуй сталі абревіатури: Вища рада правосуддя→ВРП, Кабінет Міністрів України→КМУ, " +
        "Вища кваліфікаційна комісія суддів→ВККСУ, Управління→Упр., Відділ→Відд., член→чл., департамент→деп.\n" +
        "• НІКОЛИ не викидай голосні зі слів. \"Дтр Грн Нзл Втрч\" — ЗАБОРОНЕНО: так не можна читати.\n" +
        "• Якщо треба ще коротше — краще ВИКИНУТИ ціле службове слово, ніж покалічити всі слова.\n" +
        "\n" +
        "ЗАБОРОНЕНО:\n" +
        "• штучні абревіатури з прізвищ, імен, ініціалів чи назв сторін;\n" +
        "• чіпати нумерацію (1.), дати, ПІБ та розширення файлів.\n" +
        "Якщо назва вже коротка або зрозуміло скоротити не можна — поверни оригінал, type=\"no_change\".\n" +
        "\n" +
        "ПРИКЛАДИ (як ТРЕБА):\n" +
        "• \"Дотримання гарантій незалежності та повідомлення про втручання у діяльність чл. ВККСУ\" → " +
        "\"Гарантії незалежності (втручання, чл. ВККСУ)\"\n" +
        "• \"Звернення ВККСУ (дотримання гарантій незалежності)\" → \"Звернення ВККСУ (незалежність)\"\n" +
        "• \"Відділ забезпечення реалізації повноважень членів Вищої ради правосуддя\" → \"Відд. повноважень членів ВРП\"\n" +
        "ПОГАНО (НЕ роби так): \"Дтр Грн Нзл Пвд Втрч Дль Чл Вккс\".\n" +
        "\n" +
        "Поверни ВИКЛЮЧНО валідний JSON:\n" +
        "{\"items\":[{\"id\":\"...\",\"suggested\":\"...\",\"type\":\"known_abbreviation|shortened|no_change\",\"reason\":\"...\"}]}";

    private readonly AiChatModel _model;
    private readonly BatchSettings _settings;

    public BatchAnalyzer(AiChatModel model, BatchSettings? settings = null)
    {
        _model = model;
        _settings = settings ?? new BatchSettings();
    }

    /// <summary>
    /// Збирає унікальні «ядра» сегментів уздовж шляхів, що перевищують бюджет або ліміт імені,
    /// і які потребують саме контекстного скорочення (не ПІБ, ще не в кеші/словнику).
    /// </summary>
    public static IReadOnlyList<string> CollectSegments(
        string destinationRoot, IReadOnlyList<ArchiveEntry> entries, int budget,
        SegmentLimits? limits, IPatternCache approvedCache)
    {
        destinationRoot = destinationRoot.TrimEnd('/', '\\');
        var need = new HashSet<string>(StringComparer.Ordinal);
        var seps = new[] { '/', '\\' };

        foreach (var e in entries)
        {
            var parts = e.RelativePath.Split(seps, StringSplitOptions.RemoveEmptyEntries);
            int fullLen = destinationRoot.Length + 1 + e.RelativePath.Length;

            bool overBudget = fullLen > budget;
            for (int i = 0; i < parts.Length; i++)
            {
                bool isDir = i < parts.Length - 1 || e.IsDirectory;
                int nameLimit = limits is null ? 0
                    : isDir ? limits.MaxFolderNameLength : limits.MaxFileNameLength;
                bool overName = nameLimit > 0 && parts[i].Length > nameLimit;
                if (!overBudget && !overName) continue;

                if (PersonNames.ContainsPersonName(parts[i])) continue; // ПІБ — ніколи
                string core = NameParser.Split(parts[i], isDir).Core;
                // дати відрізаємо — модель їх не бачить, а ключ кешу збігається з тим,
                // що шукає DatePreservingAbbreviator під час планування
                var (rest, _) = DateTokens.Extract(core);
                core = rest.Trim();
                if (core.Length < 8) continue;                         // надто короткі не варті моделі
                if (approvedCache.TryGet(core, out _)) continue;        // вже є готове/кешоване
                need.Add(core);
            }
        }
        return need.ToList();
    }

    /// <summary>
    /// Пакетно аналізує сегменти й записує прийняті скорочення в кеш (source='llm').
    /// Повертає зведення. Скасовується через CancellationToken між пакетами.
    /// </summary>
    public async Task<BatchOutcome> WarmCacheAsync(
        IReadOnlyList<string> cores, IPatternCache cache,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        int accepted = 0, rejected = 0, done = 0, calls = 0, tokens = 0;
        var errors = new List<string>();

        foreach (var batch in Chunk(cores, _settings.MaxItemsPerBatch))
        {
            ct.ThrowIfCancellationRequested();
            var idMap = batch.Select((c, i) => (Id: $"s{i}", Core: c)).ToList();
            string user = JsonSerializer.Serialize(new
            {
                items = idMap.Select(x => new { id = x.Id, text = x.Core })
            });

            ChatResult res = await WithRetry(() => _model.CompleteAsync(SystemPrompt, user, ct), ct);
            calls++;
            // приблизна оцінка токенів: (промпт + запит + відповідь) / 4 символи на токен
            tokens += (SystemPrompt.Length + user.Length + res.Content.Length) / 4;
            if (!res.Ok) { errors.Add(res.Error ?? "unknown"); done += batch.Count; progress?.Report((done, cores.Count)); continue; }

            if (!TryParseItems(res.Content, out var suggestions))
            {
                errors.Add("невалідний JSON у відповіді");
                done += batch.Count; progress?.Report((done, cores.Count));
                continue;
            }

            foreach (var (id, core) in idMap)
            {
                done++;
                if (!suggestions.TryGetValue(id, out var suggested)) { rejected++; continue; }
                if (Validate(core, suggested))
                {
                    cache.Put(core, suggested.Trim(), "llm");
                    accepted++;
                }
                else rejected++;
            }
            progress?.Report((done, cores.Count));
        }
        return new BatchOutcome(cores.Count, accepted, rejected, errors, calls, tokens);
    }

    private static readonly System.Text.RegularExpressions.Regex Acronym =
        new(@"^[\p{Lu}]{2,10}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Локальна валідація відповіді моделі (ТЗ п. 9.4, 23).</summary>
    public static bool Validate(string original, string? suggested)
    {
        if (string.IsNullOrWhiteSpace(suggested)) return false;
        string s = suggested.Trim();
        if (s.Length == 0 || s.Length >= original.Trim().Length) return false;   // не зменшує довжину
        if (PersonNames.ContainsPersonName(original)) return false;              // ПІБ — заборонено
        if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;     // недопустимі символи
        // Акронім приймаємо, лише якщо його літери — це справді ініціали слів оригіналу
        // (КСУ ← «Конституційний Суд України»), а не випадковий набір літер (ХЗЩ).
        if (Acronym.IsMatch(s) && !IsGenuineAcronym(original, s)) return false;
        return true;
    }

    /// <summary>Чи є acronym послідовністю перших літер значущих слів оригіналу.</summary>
    private static bool IsGenuineAcronym(string original, string acronym)
    {
        var initials = original
            .Split(new[] { ' ', '_', '.', '-', ',', '(', ')', '\'', 'ʼ', '’' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0 && char.IsLetter(w[0]))
            .Select(w => char.ToUpperInvariant(w[0]))
            .ToArray();
        // acronym має бути підпослідовністю ініціалів (у порядку появи).
        int j = 0;
        foreach (char init in initials)
        {
            if (j < acronym.Length && char.ToUpperInvariant(acronym[j]) == init) j++;
            if (j == acronym.Length) break;
        }
        return j == acronym.Length;
    }

    private static bool TryParseItems(string content, out Dictionary<string, string> map)
    {
        map = new Dictionary<string, string>(StringComparer.Ordinal);
        string json = ExtractJson(content);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return false;
            foreach (var it in items.EnumerateArray())
            {
                if (it.TryGetProperty("id", out var id) && it.TryGetProperty("suggested", out var sug))
                    map[id.GetString() ?? ""] = sug.GetString() ?? "";
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Витягує JSON-обʼєкт, навіть якщо модель додала текст навколо (Claude без json-mode).</summary>
    private static string ExtractJson(string s)
    {
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : s;
    }

    private async Task<ChatResult> WithRetry(Func<Task<ChatResult>> call, CancellationToken ct)
    {
        ChatResult res = await call();
        for (int i = 0; i < _settings.Retries && !res.Ok; i++)
        {
            await Task.Delay(_settings.RetryDelayMs, ct);
            res = await call();
        }
        return res;
    }

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
            yield return src.Skip(i).Take(size).ToList();
    }
}
