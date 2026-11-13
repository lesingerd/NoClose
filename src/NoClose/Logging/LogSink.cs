namespace NoClose.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public enum LogCategory { App, Engine, Guard, Journal, Session, Power, Display, Presence, EndSession, Diag, Cli, Audio }

public interface ILogSink
{
    void Write(LogLevel level, LogCategory category, string message);
}

public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();
    public void Write(LogLevel level, LogCategory category, string message) { }
}

/// <summary>In-memory sink (used by tests and for the "recent events" view).</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly List<string> _lines = new();
    public void Write(LogLevel level, LogCategory category, string message)
    {
        lock (_lines) _lines.Add($"[{level}] [{category}] {message}");
    }
    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }
}

public static class LogSinkExtensions
{
    public static void Info(this ILogSink s, LogCategory c, string m) => s.Write(LogLevel.Info, c, m);
    public static void Warn(this ILogSink s, LogCategory c, string m) => s.Write(LogLevel.Warn, c, m);
    public static void Error(this ILogSink s, LogCategory c, string m) => s.Write(LogLevel.Error, c, m);
    public static void Debug(this ILogSink s, LogCategory c, string m) => s.Write(LogLevel.Debug, c, m);
}
