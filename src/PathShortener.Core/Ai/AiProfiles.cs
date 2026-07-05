using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PathShortener.Core.Ai;

public enum AiProvider { OllamaLocal, OllamaRemote, Claude, OpenAI, Grok, Gemini }

/// <summary>
/// Єдина структура профілю ШІ-провайдера (ТЗ п. 18). Для всіх провайдерів однакова;
/// baseUrl релевантний для Ollama-remote та Grok. Ключ у файлі — завжди зашифрований (DPAPI).
/// </summary>
public sealed class AiProfile
{
    [JsonPropertyName("provider")] public AiProvider Provider { get; set; }
    [JsonPropertyName("profileName")] public string ProfileName { get; set; } = "";
    /// <summary>Зашифроване значення ключа (DPAPI, префікс "dpapi:"). У відкритому вигляді не зберігається.</summary>
    [JsonPropertyName("apiKey")] public string ApiKeyProtected { get; set; } = "";
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("selectedModel")] public string SelectedModel { get; set; } = "";
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("lastConnectionStatus")] public string LastConnectionStatus { get; set; } = "";
    [JsonPropertyName("lastCheckedAt")] public DateTime? LastCheckedAt { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    /// <summary>Ключ у відкритому вигляді — лише в пам'яті, для запиту до API.</summary>
    [JsonIgnore] public string ApiKey
    {
        get => Secret.Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = Secret.Protect(value);
    }
}

/// <summary>Шифрування чутливих значень через Windows DPAPI (прив'язка до облікового запису користувача).</summary>
public static class Secret
{
    private const string Prefix = "dpapi:";

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch { return plain; } // DPAPI недоступна (не-Windows) — зберігаємо як є, краще ніж втратити
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        try
        {
            byte[] dec = ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
    }
}

/// <summary>
/// Сховище профілів усіх провайдерів у одному JSON-файлі. Ключі кожного провайдера
/// зберігаються незалежно; перемикання провайдера не зачіпає чужі профілі (ТЗ п. 14–17, 27).
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    public List<AiProfile> Profiles { get; private set; } = new();
    /// <summary>Активний провайдер (яким виконується інтелектуальний аналіз).</summary>
    public AiProvider ActiveProvider { get; set; } = AiProvider.OllamaLocal;

    public ProfileStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.TryGetProperty("activeProvider", out var ap) &&
                Enum.TryParse<AiProvider>(ap.GetString(), out var p)) ActiveProvider = p;
            if (doc.RootElement.TryGetProperty("profiles", out var arr))
                Profiles = JsonSerializer.Deserialize<List<AiProfile>>(arr.GetRawText(), Opts) ?? new();
        }
        catch { Profiles = new(); }
    }

    public void Save()
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                activeProvider = ActiveProvider.ToString(),
                profiles = Profiles
            }, Opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* збереження профілів не критичне для роботи */ }
    }

    public IEnumerable<AiProfile> For(AiProvider provider) => Profiles.Where(p => p.Provider == provider);

    /// <summary>Активний профіль обраного провайдера (перший позначений IsActive, інакше перший наявний).</summary>
    public AiProfile? ActiveProfile(AiProvider provider)
        => For(provider).FirstOrDefault(p => p.IsActive) ?? For(provider).FirstOrDefault();

    public void SetActive(AiProfile profile)
    {
        foreach (var p in For(profile.Provider)) p.IsActive = ReferenceEquals(p, profile);
    }

    public void Remove(AiProfile profile) => Profiles.Remove(profile);
}
