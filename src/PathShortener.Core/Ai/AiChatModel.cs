using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PathShortener.Core.Ai;

public sealed record ChatResult(bool Ok, string Content, string? Error);

/// <summary>
/// Єдиний HTTP-клієнт до всіх провайдерів. «Діалекти»:
///   • OpenAI / Grok / Ollama-remote(OpenAI-сумісний) — /chat/completions, Bearer;
///   • Claude — /v1/messages, x-api-key + anthropic-version;
///   • Gemini — /v1beta/models/{model}:generateContent?key=…;
///   • Ollama — /api/chat (без ключа).
/// Скрізь temperature=0 і прохання про JSON — для детермінізму й машинного парсингу.
/// HttpClient можна підмінити (для тестів без мережі).
/// </summary>
public sealed class AiChatModel : IDisposable
{
    private readonly HttpClient _http;
    private readonly AiProvider _provider;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public AiChatModel(AiProvider provider, string model, string apiKey, string? baseUrl, HttpClient? http = null)
    {
        _provider = provider;
        _model = model;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? DefaultBaseUrl(provider)).TrimEnd('/');
        _http = http ?? new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(120) };
    }

    public static string DefaultBaseUrl(AiProvider p) => p switch
    {
        AiProvider.OpenAI => "https://api.openai.com/v1",
        AiProvider.Grok => "https://api.x.ai/v1",
        AiProvider.Claude => "https://api.anthropic.com",
        AiProvider.Gemini => "https://generativelanguage.googleapis.com",
        AiProvider.OllamaRemote => "http://localhost:11434",
        _ => "http://localhost:11434",
    };

    public static IReadOnlyList<string> DefaultModels(AiProvider p) => p switch
    {
        // Claude 5 / Opus 4.8 / Haiku 4.5 / Fable 5 (актуальні на 2026)
        AiProvider.Claude => new[]
        {
            "claude-opus-4-8", "claude-sonnet-5", "claude-haiku-4-5-20251001", "claude-fable-5"
        },
        AiProvider.OpenAI => new[]
        {
            "gpt-4.1", "gpt-4.1-mini", "gpt-4o", "gpt-4o-mini", "o4-mini", "o3"
        },
        AiProvider.Grok => new[] { "grok-4", "grok-3", "grok-3-mini", "grok-2-1212" },
        AiProvider.Gemini => new[]
        {
            "gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash", "gemini-1.5-pro"
        },
        _ => new[] { "qwen2.5:3b" },
    };

    /// <summary>Виконує запит; повертає текстову відповідь (очікувано — JSON).</summary>
    public async Task<ChatResult> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        try
        {
            var (json, path) = BuildRequest(system, user);
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            ApplyAuthHeaders(req);
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new ChatResult(false, "", $"HTTP {(int)resp.StatusCode}: {Truncate(body)}");
            return new ChatResult(true, ExtractContent(body), null);
        }
        catch (Exception ex)
        {
            return new ChatResult(false, "", ex.Message);
        }
    }

    /// <summary>Легка перевірка підключення (той самий шлях, короткий запит).</summary>
    public async Task<ChatResult> TestAsync(CancellationToken ct = default)
        => await CompleteAsync("Reply with JSON.", "Return {\"ok\":true}", ct);

    private (string Json, string Path) BuildRequest(string system, string user)
    {
        switch (_provider)
        {
            case AiProvider.Claude:
                return (JsonSerializer.Serialize(new
                {
                    model = _model,
                    max_tokens = 2048,
                    temperature = 0,
                    system,
                    messages = new[] { new { role = "user", content = user } }
                }), "/v1/messages");

            case AiProvider.Gemini:
                return (JsonSerializer.Serialize(new
                {
                    system_instruction = new { parts = new[] { new { text = system } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
                    generationConfig = new { temperature = 0, responseMimeType = "application/json" }
                }), $"/v1beta/models/{_model}:generateContent?key={_apiKey}");

            case AiProvider.OllamaLocal or AiProvider.OllamaRemote:
                return (JsonSerializer.Serialize(new
                {
                    model = _model,
                    stream = false,
                    format = "json",
                    options = new { temperature = 0 },
                    messages = new[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = user }
                    }
                }), "/api/chat");

            default: // OpenAI, Grok
                return (JsonSerializer.Serialize(new
                {
                    model = _model,
                    temperature = 0,
                    response_format = new { type = "json_object" },
                    messages = new[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = user }
                    }
                }), "/chat/completions");
        }
    }

    private string ExtractContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return _provider switch
        {
            AiProvider.Claude => root.GetProperty("content")[0].GetProperty("text").GetString() ?? "",
            AiProvider.Gemini => root.GetProperty("candidates")[0].GetProperty("content")
                                     .GetProperty("parts")[0].GetProperty("text").GetString() ?? "",
            AiProvider.OllamaLocal or AiProvider.OllamaRemote =>
                root.GetProperty("message").GetProperty("content").GetString() ?? "",
            _ => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "",
        };
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose() => _http.Dispose();

    // Заголовки авторизації для кожного діалекту (викликається у CompleteAsync через обгортку нижче).
    internal void ApplyAuthHeaders(HttpRequestMessage req)
    {
        switch (_provider)
        {
            case AiProvider.Claude:
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case AiProvider.OpenAI or AiProvider.Grok or AiProvider.OllamaRemote:
                if (!string.IsNullOrEmpty(_apiKey))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                break;
            // Gemini — ключ у query; Ollama-local — без ключа
        }
    }
}
