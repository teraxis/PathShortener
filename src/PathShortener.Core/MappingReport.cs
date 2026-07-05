using System.Text;
using System.Text.Json;
using PathShortener.Core.Models;

namespace PathShortener.Core.Report;

/// <summary>
/// Журнал відповідностей «оригінальний шлях → новий шлях».
/// Для держдокументів це обов'язково: робить скорочення прозорим, аудитованим і зворотним.
/// </summary>
public static class MappingReport
{
    public static void WriteCsv(ShorteningPlan plan, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("original_relative;new_relative;full_length;within_budget;shortened;truncated");
        foreach (var e in plan.Entries)
            sb.AppendLine(string.Join(';',
                Csv(e.OriginalRelativePath),
                Csv(e.NewRelativePath),
                e.FinalFullLength,
                e.WithinBudget,
                e.WasShortened,
                e.WasTruncated));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void WriteJson(ShorteningPlan plan, string path)
    {
        var payload = new
        {
            plan.DestinationRoot,
            plan.Budget,
            plan.TotalEntries,
            plan.ShortenedCount,
            plan.TruncatedCount,
            plan.OverBudgetCount,
            entries = plan.Entries
        };
        File.WriteAllText(path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
    }

    private static string Csv(string s)
        => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
