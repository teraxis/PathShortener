using System.Text.Json;
using System.Text.Json.Serialization;
using PathShortener.Core.Abbreviation;

namespace PathShortener.Core.Cache;

/// <summary>
/// Постійний кеш патернів «нормалізована назва → скорочення» в одному JSON-файлі.
/// Заміна DuckDB: для кешу ключ→значення на кілька тисяч записів СУБД не потрібна,
/// а дистрибутив легшає на ~35 МБ. Семантика та сама:
///   • пошук за стем-ключем (відмінки однієї назви мапляться на один запис);
///   • записи source='dict'/'manual' НЕ перезаписуються скороченнями від LLM;
///   • файл читабельний і редагований вручну (UTF-8, з відступами).
/// Запис атомарний: тимчасовий файл + заміна.
/// </summary>
public sealed class JsonPatternCache : IPatternCache, IDisposable
{
    public sealed record Entry(
        [property: JsonPropertyName("original")] string Original,
        [property: JsonPropertyName("short")] string ShortForm,
        [property: JsonPropertyName("source")] string Source);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly Dictionary<string, Entry> _map;
    private readonly object _lock = new();
    private bool _dirty;

    public JsonPatternCache(string path = "patterns.json")
    {
        _path = path;
        _map = Load(path);
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOpts);
            return map ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
        catch
        {
            // пошкоджений файл — стартуємо з порожнього кешу, не валимо програму
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    public bool TryGet(string core, out string shortForm)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(NameNormalizer.StemKey(core), out var e))
            {
                shortForm = e.ShortForm;
                return true;
            }
        }
        shortForm = "";
        return false;
    }

    /// <summary>
    /// Пошук лише серед затверджених джерел (dict/manual) — для звичайного аналізу
    /// без ШІ, щоб LLM-скорочення з кешу не застосовувались як словникові.
    /// </summary>
    public bool TryGetApproved(string core, out string shortForm)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(NameNormalizer.StemKey(core), out var e) &&
                e.Source is "dict" or "manual")
            {
                shortForm = e.ShortForm;
                return true;
            }
        }
        shortForm = "";
        return false;
    }

    public void Put(string core, string shortForm, string source)
    {
        lock (_lock)
        {
            PutNoSave(core, shortForm, source);
            SaveNoLock();
        }
    }

    private void PutNoSave(string core, string shortForm, string source)
    {
        string key = NameNormalizer.StemKey(core);
        // Еталонні/ручні записи мають пріоритет — LLM їх не перезаписує.
        if (_map.TryGetValue(key, out var existing) &&
            existing.Source is "dict" or "manual" && source is not ("dict" or "manual"))
            return;
        _map[key] = new Entry(core, shortForm, source);
        _dirty = true;
    }

    /// <summary>Разове заливання еталонного словника з CSV (original;short_form), одним записом на диск.</summary>
    public int SeedFromCsv(string csvPath)
    {
        int n = 0;
        lock (_lock)
        {
            foreach (var line in File.ReadLines(csvPath))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                var parts = t.Split(';', 2);
                if (parts.Length != 2) continue;
                PutNoSave(parts[0].Trim(), parts[1].Trim(), "dict");
                n++;
            }
            SaveNoLock();
        }
        return n;
    }

    private void SaveNoLock()
    {
        if (!_dirty) return;
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_map, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
        _dirty = false;
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    // ---- керування словником (ТЗ п. 22) ----

    /// <summary>Знімок усіх записів (ключ + запис) для перегляду/редагування.</summary>
    public IReadOnlyList<(string Key, Entry Entry)> Snapshot()
    {
        lock (_lock) return _map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Додає/оновлює запис примусово (ручне редагування — має пріоритет над правилами LLM).</summary>
    public void Upsert(string original, string shortForm, string source)
    {
        lock (_lock)
        {
            _map[NameNormalizer.StemKey(original)] = new Entry(original, shortForm, source);
            _dirty = true;
            SaveNoLock();
        }
    }

    /// <summary>Видаляє запис за стем-ключем.</summary>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            if (!_map.Remove(key)) return false;
            _dirty = true;
            SaveNoLock();
            return true;
        }
    }

    /// <summary>Затверджує LLM-скорочення: змінює source на 'manual' (тепер його бачить і звичайний аналіз).</summary>
    public bool Approve(string key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var e)) return false;
            _map[key] = e with { Source = "manual" };
            _dirty = true;
            SaveNoLock();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock) SaveNoLock();
    }
}
