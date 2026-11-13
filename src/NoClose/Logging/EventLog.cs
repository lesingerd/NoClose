using System.Globalization;
using System.Text;
using System.Threading.Channels;
using NoClose.Core.Settings;

namespace NoClose.Logging;

/// <summary>
/// Thread-safe timeline log: %LocalAppData%\NoClose\logs\noclose-yyyyMMdd.log. A background task writes lines and flushes
/// after each one; <see cref="FlushNow"/> drains synchronously (used by the emergency restore path).
/// </summary>
public sealed class TimelineLog : ILogSink, IDisposable
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false });
    private readonly object _writeLock = new();
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly Task _writer;
    private bool _disposed;

    public string Directory => _dir;

    public TimelineLog(string? directory = null, LogSettings? settings = null)
    {
        settings ??= new LogSettings();
        _dir = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoClose", "logs");
        _maxBytes = Math.Max(1, settings.MaxFileMB) * 1024L * 1024L;
        System.IO.Directory.CreateDirectory(_dir);
        DeleteOld(settings.RetainDays);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Pure formatter: <c>2026-11-12T14:03:22.123+01:00 [INFO ] [Session] text</c>.</summary>
    public static string FormatLine(DateTimeOffset time, LogLevel level, LogCategory category, string message)
    {
        var lvl = level switch { LogLevel.Debug => "DEBUG", LogLevel.Info => "INFO ", LogLevel.Warn => "WARN ", _ => "ERROR" };
        var text = message.Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ');
        return $"{time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)} [{lvl}] [{category}] {text}";
    }

    public void Write(LogLevel level, LogCategory category, string message)
    {
        if (_disposed) return;
        _channel.Writer.TryWrite(FormatLine(DateTimeOffset.Now, level, category, message));
    }

    private async Task WriteLoopAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
                DrainOnce();
        }
        catch (ChannelClosedException) { }
    }

    private void DrainOnce()
    {
        lock (_writeLock)
        {
            while (_channel.Reader.TryRead(out var line))
                AppendLine(line);
        }
    }

    /// <summary>Synchronously writes everything queued so far.</summary>
    public void FlushNow()
    {
        try { DrainOnce(); } catch { /* logging must never throw */ }
    }

    private string CurrentPath()
    {
        var baseName = "noclose-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(_dir, baseName + ".log");
        int n = 0;
        while (File.Exists(path) && new FileInfo(path).Length > _maxBytes)
        {
            n++;
            path = System.IO.Path.Combine(_dir, $"{baseName}-{n}.log");
        }
        return path;
    }

    private void AppendLine(string line)
    {
        try
        {
            using var fs = new FileStream(CurrentPath(), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true); // flush after each line: losing the last lines in a crash defeats the purpose
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void DeleteOld(int retainDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retainDays);
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "noclose-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Last non-empty line of the most recent previous log file (for "Previous run ended: ...").</summary>
    public string? LastLineOfPreviousRun()
    {
        try
        {
            var latest = new DirectoryInfo(_dir).EnumerateFiles("noclose-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest == null) return null;
            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? last = null;
            while (sr.ReadLine() is { } l) if (l.Length > 0) last = l;
            return last;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        FlushNow();
        _disposed = true;
        _channel.Writer.TryComplete();
        try { _writer.Wait(500); } catch { }
    }
}
