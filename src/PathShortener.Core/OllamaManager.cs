using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PathShortener.Core.Abbreviation;

public sealed record OllamaModelInfo(string Name, long SizeBytes);

/// <summary>
/// Керування локальною Ollama: статус сервера, список моделей, довантаження моделі
/// (/api/pull зі стрімом прогресу), пошук встановленого ollama.exe, запуск сервера
/// і завантаження/запуск офіційного інсталятора.
/// </summary>
public sealed class OllamaManager : IDisposable
{
    public const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";

    private readonly HttpClient _http;

    public OllamaManager(string baseUrl = "http://localhost:11434")
        => _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) // локальний сервер — без проксі (WPAD гальмує перший запит)
        { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Чи відповідає сервер Ollama (швидка перевірка, до 5 с).</summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await _http.GetAsync("/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Встановлені моделі.</summary>
    public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var list = new List<OllamaModelInfo>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("/api/tags", cts.Token));
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
                list.Add(new OllamaModelInfo(
                    m.GetProperty("name").GetString() ?? "",
                    m.TryGetProperty("size", out var s) ? s.GetInt64() : 0));
        }
        catch { /* сервер недоступний → порожній список */ }
        return list;
    }

    /// <summary>
    /// Завантажує модель (ollama pull) зі звітуванням прогресу: (статус, відсоток або null).
    /// </summary>
    public async Task PullModelAsync(string model,
        IProgress<(string Status, double? Percent)>? progress = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, stream = true }),
                Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.GetString());

            string status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            double? percent = null;
            if (root.TryGetProperty("total", out var total) && total.GetInt64() > 0 &&
                root.TryGetProperty("completed", out var completed))
                percent = 100.0 * completed.GetInt64() / total.GetInt64();
            progress?.Report((status, percent));
        }
    }

    /// <summary>Шлях до ollama.exe, якщо встановлено (типові місця + PATH).</summary>
    public static string? FindOllamaExe()
    {
        var candidates = new List<string>();
        string? localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
            candidates.Add(Path.Combine(localApp, "Programs", "Ollama", "ollama.exe"));
        string? progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(progFiles))
            candidates.Add(Path.Combine(progFiles, "Ollama", "ollama.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (dir.Trim().Length > 0)
                candidates.Add(Path.Combine(dir.Trim(), "ollama.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsInstalled() => FindOllamaExe() is not null;

    /// <summary>Запускає сервер Ollama у фоні (без вікна).</summary>
    public static void StartServer()
    {
        string exe = FindOllamaExe() ?? throw new InvalidOperationException("Ollama не знайдено.");
        Process.Start(new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    /// <summary>Чекає, поки сервер почне відповідати.</summary>
    public async Task<bool> WaitForServerAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsRunningAsync(ct)) return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    /// <summary>Завантажує офіційний інсталятор Ollama зі звітуванням прогресу (байти, всього).</summary>
    public async Task DownloadInstallerAsync(string destFile,
        IProgress<(long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, InstallerUrl),
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destFile);
        var buf = new byte[1 << 16];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            progress?.Report((done, total));
        }
    }

    /// <summary>
    /// Запускає інсталятор тихо (per-user, без прав адміністратора) і чекає завершення.
    /// Після встановлення Ollama зазвичай стартує сама.
    /// </summary>
    public static async Task RunInstallerAsync(string setupPath, CancellationToken ct = default)
    {
        using var proc = Process.Start(new ProcessStartInfo(setupPath, "/VERYSILENT /NORESTART")
        {
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Не вдалося запустити інсталятор.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Інсталятор завершився з кодом {proc.ExitCode}.");
    }

    public void Dispose() => _http.Dispose();
}
