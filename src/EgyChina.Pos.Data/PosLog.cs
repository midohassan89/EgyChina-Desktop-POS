namespace EgyChina.Pos.Data;

public static class PosLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void SetDirectory(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        lock (Gate)
        {
            _path = Path.Combine(logDirectory, "pos.log");
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.Now:O} {level} {message}";
        if (exception is not null)
            line += $" {exception.GetType().Name}: {exception.Message.Replace('\r', ' ').Replace('\n', ' ')}";

        lock (Gate)
        {
            if (_path is null)
                return;

            var info = new FileInfo(_path);
            if (info.Exists && info.Length > 2_000_000)
            {
                File.Copy(_path, _path + ".1", overwrite: true);
                File.WriteAllText(_path, string.Empty);
            }

            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
