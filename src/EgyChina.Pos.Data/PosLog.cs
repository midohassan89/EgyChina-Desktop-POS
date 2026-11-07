namespace EgyChina.Pos.Data;

/// <summary>
/// Local rolling log. Secrets are redacted. Old files are pruned after 14 days.
/// </summary>
public static class PosLog
{
    public const long RotateBytes = 2_000_000;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private static readonly object Gate = new();
    private static string? _directory;
    private static string? _path;

    public static void SetDirectory(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        lock (Gate)
        {
            _directory = logDirectory;
            _path = Path.Combine(logDirectory, "pos.log");
        }

        PurgeOldFiles();
    }

    /// <summary>Clears the log target. Used by tests so a temp folder can be deleted safely.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _directory = null;
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

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
        }

        lock (Gate)
        {
            if (_path is null || _directory is null)
                return;

            try
            {
                Directory.CreateDirectory(_directory);
                RotateIfNeeded(_path, _directory);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RotateIfNeeded(string path, string directory)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= RotateBytes)
            return;

        var archive = Path.Combine(
            directory,
            "pos-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + ".log");
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
