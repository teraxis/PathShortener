using System.Text.Json;
using Avalonia.Platform;

namespace PathShortener.Gui;

/// <summary>
/// Локалізація: усі користувацькі тексти — в Resources/Localization/*.json,
/// жодних жорстко прописаних рядків в UI-коді.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _map = new();

    /// <summary>"uk" або "en".</summary>
    public static string Lang { get; private set; } = "uk";

    public static void Load(string lang)
    {
        Lang = lang == "en" ? "en" : "uk";
        string file = Lang == "en" ? "en-US.json" : "uk-UA.json";
        using var s = AssetLoader.Open(new Uri($"avares://PathShortener/Resources/Localization/{file}"));
        _map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
    }

    /// <summary>Текст за ключем (відсутній ключ повертає сам ключ — видно, що загубили).</summary>
    public static string T(string key) => _map.TryGetValue(key, out var v) ? v : key;

    /// <summary>Текст із підстановками {0}, {1}…</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);
}
