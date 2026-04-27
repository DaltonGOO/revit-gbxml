using System.Globalization;
using System.IO;
using System.Text;

namespace RevitGbXmlExporter.Utilities;

public enum LogLevel { Info, Warn, Error }

public record LogEntry(LogLevel Level, string Category, string Message);

/// <summary>
/// Collects diagnostic messages during export so the user can see exactly
/// what was written, skipped, or repaired. Written as a sidecar .log.txt
/// next to the gbXML file.
/// </summary>
public class ExportLog
{
    private readonly List<LogEntry> _entries = [];

    public int InfoCount { get; private set; }
    public int WarnCount { get; private set; }
    public int ErrorCount { get; private set; }

    public IReadOnlyList<LogEntry> Entries => _entries;

    public void Info(string category, string message) => Add(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Add(LogLevel.Warn, category, message);
    public void Error(string category, string message) => Add(LogLevel.Error, category, message);

    private void Add(LogLevel level, string category, string message)
    {
        _entries.Add(new LogEntry(level, category, message));
        switch (level)
        {
            case LogLevel.Info: InfoCount++; break;
            case LogLevel.Warn: WarnCount++; break;
            case LogLevel.Error: ErrorCount++; break;
        }
    }

    public void WriteTo(string filePath)
    {
        var sb = new StringBuilder();
        sb.Append("Revit gbXML Exporter log\n");
        sb.Append("Generated: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append($"Summary: {InfoCount} info, {WarnCount} warnings, {ErrorCount} errors\n");
        sb.Append(new string('-', 60)).Append('\n');

        foreach (var entry in _entries)
        {
            sb.Append('[').Append(entry.Level.ToString().ToUpperInvariant()).Append("] ");
            sb.Append(entry.Category).Append(": ").Append(entry.Message).Append('\n');
        }

        File.WriteAllText(filePath, sb.ToString());
    }
}
