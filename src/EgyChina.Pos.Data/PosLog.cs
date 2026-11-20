using System.Diagnostics;
using System.Globalization;

namespace EgyChina.Pos.Data;

/// <summary>
/// Local rolling log. Secrets are redacted. Old files are pruned after 14 days.
/// Full trail: <c>pos.log</c>. Errors only (for support paste): <c>problems.log</c>.
/// </summary>
public static class PosLog
{
    public const string FullLogFileName = "pos.log";
    public const string ProblemsLogFileName = "problems.log";
    public const long RotateBytes = 2_000_000;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private static readonly object Gate = new();
    private static string? _directory;
    private static string? _path;
    private static string? _problemsPath;

    public static void SetDirectory(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        lock (Gate)
        {
            _directory = logDirectory;
            _path = Path.Combine(logDirectory, FullLogFileName);
            _problemsPath = Path.Combine(logDirectory, ProblemsLogFileName);
        }

        PurgeOldFiles();
    }

    /// <summary>Absolute path of the support paste file, or null before startup.</summary>
    public static string? ProblemsLogPath
    {
        get
        {
            lock (Gate)
                return _problemsPath;
        }
    }

    /// <summary>Absolute path of the full rolling log, or null before startup.</summary>
    public static string? FullLogPath
    {
        get
        {
            lock (Gate)
                return _path;
        }
    }

    /// <summary>Clears the log target. Used by tests so a temp folder can be deleted safely.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _directory = null;
            _path = null;
            _problemsPath = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>
    /// Times one operation and writes <c>slow-op</c> when it exceeds <paramref name="thresholdMs"/>.
    /// The line is a name and a duration only — do not put PINs, payloads, or tokens in <paramref name="name"/>.
    /// </summary>
    public static SlowOp Time(string name, int thresholdMs = SlowOp.DefaultThresholdMs) =>
        new(name, thresholdMs);

    public static int PurgeOldFiles(DateTimeOffset? now = null)
    {
        string? directory;
        lock (Gate)
            directory = _directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return 0;

        var cutoff = (now ?? DateTimeOffset.Now) - Retention;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "pos*.log*"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff.UtcDateTime)
                {
                    info.Delete();
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static void Write(string level, string message, Exception? exception)
    {
        message = LogRedactor.Redact(message);
        var line = $"{DateTimeOffset.Now:O} {level} {message}";
        if (exception is not null)
        {
            var detail = LogRedactor.Redact(exception.GetType().Name + ": " + exception.Message);
            line += " " + detail;
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                var stack = LogRedactor.Redact(exception.StackTrace);
                if (stack.Length > 1200)
                    stack = stack[..1200] + "…";
                line += Environment.NewLine + "  STACK " + stack.Replace("\r", string.Empty).Replace("\n", " | ");
            }

            if (exception.InnerException is { } inner)
            {
                var innerText = LogRedactor.Redact(inner.GetType().Name + ": " + inner.Message);
                line += Environment.NewLine + "  INNER " + innerText;
            }
        }

        lock (Gate)
        {
            if (_path is null || _directory is null)
                return;

            try
            {
                Directory.CreateDirectory(_directory);
                RotateIfNeeded(_path, _directory, "pos");
                File.AppendAllText(_path, line + Environment.NewLine);

                if (string.Equals(level, "ERROR", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(_problemsPath))
                {
                    RotateIfNeeded(_problemsPath, _directory, "problems");
                    File.AppendAllText(_problemsPath, line + Environment.NewLine + Environment.NewLine);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RotateIfNeeded(string path, string directory, string prefix)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= RotateBytes)
            return;

        var archive = Path.Combine(
            directory,
            prefix + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        try
        {
            if (File.Exists(archive))
                File.Delete(archive);
            File.Move(path, archive);
        }
        catch (IOException)
        {
            try
            {
                File.Copy(path, path + ".1", overwrite: true);
                File.WriteAllText(path, string.Empty);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Stopwatch scope for <see cref="PosLog.Time"/>. Logs only when the threshold is crossed.</summary>
public sealed class SlowOp : IDisposable
{
    public const int DefaultThresholdMs = 100;

    private readonly string _name;
    private readonly int _thresholdMs;
    private readonly long _start;
    private string _detail = string.Empty;
    private int _disposed;

    internal SlowOp(string name, int thresholdMs)
    {
        _name = name;
        _thresholdMs = thresholdMs;
        _start = Stopwatch.GetTimestamp();
    }

    /// <summary>Extra counters appended to the slow-op line. Not written when the call is fast.</summary>
    public void SetDetail(string detail) => _detail = detail ?? string.Empty;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var elapsedMs = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        if (elapsedMs < _thresholdMs)
            return;

        var ms = ((long)elapsedMs).ToString(CultureInfo.InvariantCulture);
        var suffix = string.IsNullOrEmpty(_detail) ? string.Empty : " " + _detail.Trim();
        PosLog.Info($"slow-op name={_name} ms={ms}{suffix}");
    }
}
