using PathShortener.Core.Abbreviation;

namespace PathShortener.Core.Cache;

/// <summary>
/// Постійний кеш патернів «нормалізована назва → скорочення».
/// Реальна реалізація — JSON-файл (див. JsonPatternCache.cs).
/// Пошук іде за стем-ключем, щоб різні відмінки мапились на один запис.
/// </summary>
public interface IPatternCache
{
    /// <summary>Повертає скорочення, якщо воно вже відоме (за стем-ключем core).</summary>
    bool TryGet(string core, out string shortForm);

    /// <summary>Зберігає скорочення для майбутніх запитів.</summary>
    void Put(string core, string shortForm, string source);
}

/// <summary>Реалізація в пам'яті — для тестів і як приклад контракту.</summary>
public sealed class InMemoryPatternCache : IPatternCache
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public bool TryGet(string core, out string shortForm)
        => _map.TryGetValue(NameNormalizer.StemKey(core), out shortForm!);

    public void Put(string core, string shortForm, string source)
        => _map[NameNormalizer.StemKey(core)] = shortForm;

    public int Count => _map.Count;
}

/// <summary>
/// Провайдер, що загортає будь-який «повільний» провайдер (Ollama) кешем.
/// Логіка: кеш → інакше внутрішній провайдер → записати в кеш.
/// Саме ця обгортка дає «максимальну швидкість»: після прогріву кешу
/// модель майже не викликається.
/// </summary>
public sealed class CachedAbbreviator : IAbbreviationProvider
{
    private readonly IAbbreviationProvider _inner;
    private readonly IPatternCache _cache;
    private readonly string _source;

    public CachedAbbreviator(IAbbreviationProvider inner, IPatternCache cache, string source = "llm")
    {
        _inner = inner;
        _cache = cache;
        _source = source;
    }

    public string Abbreviate(string core)
    {
        if (_cache.TryGet(core, out var cached))
            return cached;
        string result = _inner.Abbreviate(core);
        _cache.Put(core, result, _source);
        return result;
    }
}
