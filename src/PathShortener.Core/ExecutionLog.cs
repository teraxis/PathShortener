using System.Text;

namespace PathShortener.Core.Report;

/// <summary>
/// Журнал виконання операцій (ТЗ п. 28). Пише рядки з міткою часу у файл.
/// НЕ містить API-ключів чи інших чутливих даних — сюди подаються лише події й лічильники.
/// Потокобезпечний, дозаписує; помилки запису ковтаються (журнал не має валити роботу).
/// </summary>
public sealed class ExecutionLog
{
    private readonly string _path;
    private readonly object _lock = new();

    public ExecutionLog(string path) => _path = path;

    public void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{message}{Environment.NewLine}";
        try
        {
            lock (_lock) File.AppendAllText(_path, line, Encoding.UTF8);
        }
        catch { /* журнал не критичний */ }
    }

    public void Write(string format, params object?[] args) => Write(string.Format(format, args));
}
