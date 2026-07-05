using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PathShortener.Core.Abbreviation;

namespace PathShortener.Core.Abbreviation;

/// <summary>
/// Семантичне скорочення через локальну Ollama (модель qwen2.5:3b).
/// ВАЖЛИВО:
///   • temperature = 0 → детермінований вивід для однакового входу.
///   • просимо ЛИШЕ JSON {"short":"..."} → парсимо без евристик.
///   • на будь-яку помилку (Ollama недоступна, кривий JSON) — фолбек на RuleBasedAbbreviator.
///
/// Цей провайдер зазвичай загортається в CachedAbbreviator, тож після прогріву
/// кешу патернів модель викликається рідко. Саме така схема дає «максимальну швидкість».
/// </summary>
public sealed class OllamaAbbreviator : IAbbreviationProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly IAbbreviationProvider _fallback;

    private const string SystemPrompt =
        "Ти скорочуєш назву папки/файлу української держустанови так, щоб вона лишалася " +
        "КОРОТКОЮ, але ЗРОЗУМІЛОЮ ЛЮДИНІ — щоб було видно вміст.\n" +
        "Правила:\n" +
        "1. Залишай 1–3 найважливіші слова (іменники) ЦІЛИМИ й впізнаваними.\n" +
        "2. Прибирай службові слова (дотримання, повідомлення, забезпечення, реалізації, щодо, про, та, у).\n" +
        "3. Усталені абревіатури: Вища рада правосуддя→ВРП, Кабінет Міністрів→КМУ, " +
        "Вища кваліфікаційна комісія суддів→ВККСУ, Міністерство→М-во, Відділ→Відд., Управління→Упр., член→чл.\n" +
        "4. НІКОЛИ не викидай голосні (\"Дтр Грн Нзл\" — заборонено). Краще прибрати ціле службове слово.\n" +
        "5. НЕ чіпай нумерацію, дати, ПІБ, розширення.\n" +
        "Приклад: \"Дотримання гарантій незалежності та повідомлення про втручання у діяльність чл. ВККСУ\" " +
        "-> \"Гарантії незалежності (втручання, чл. ВККСУ)\".\n" +
        "Поверни ВИКЛЮЧНО JSON без пояснень: {\"short\":\"...\"}";

    public OllamaAbbreviator(
        string model = "qwen2.5:3b",
        string baseUrl = "http://localhost:11434",
        IAbbreviationProvider? fallback = null,
        HttpClient? http = null)
    {
        _model = model;
        _http = http ?? new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(baseUrl);
        _fallback = fallback ?? new RuleBasedAbbreviator();
    }

    public string Abbreviate(string core)
    {
        if (string.IsNullOrWhiteSpace(core)) return core;
        try
        {
            var req = new OllamaRequest
            {
                Model = _model,
                Stream = false,
                Format = "json",
                Options = new OllamaOptions { Temperature = 0 },
                Messages = new[]
                {
                    new OllamaMessage { Role = "system", Content = SystemPrompt },
                    new OllamaMessage { Role = "user", Content = $"Назва: \"{core}\"" }
                }
            };

            using var resp = _http.PostAsJsonAsync("/api/chat", req).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var body = resp.Content.ReadFromJsonAsync<OllamaResponse>().GetAwaiter().GetResult();
            string? content = body?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content)) return _fallback.Abbreviate(core);

            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("short", out var shortEl))
            {
                string? s = shortEl.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();
            }
            return _fallback.Abbreviate(core);
        }
        catch
        {
            // Ollama недоступна / таймаут / кривий JSON → детермінований фолбек.
            return _fallback.Abbreviate(core);
        }
    }

    public void Dispose() => _http.Dispose();

    // ---- DTO для /api/chat ----
    private sealed class OllamaRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public OllamaMessage[] Messages { get; set; } = Array.Empty<OllamaMessage>();
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("format")] public string Format { get; set; } = "json";
        [JsonPropertyName("options")] public OllamaOptions Options { get; set; } = new();
    }
    private sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
    }
    private sealed class OllamaMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }
    private sealed class OllamaResponse
    {
        [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
    }
}
