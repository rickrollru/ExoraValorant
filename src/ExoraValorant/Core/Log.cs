namespace Exora.Core;

public static class Log
{
    private static readonly object Gate = new();
    private const long MaxSize = 2 * 1024 * 1024;

    public static void Init()
    {
        try
        {
            var file = new FileInfo(AppInfo.LogFile);
            if (file.Exists && file.Length > MaxSize)
            {
                File.Copy(file.FullName, file.FullName + ".old", overwrite: true);
                File.WriteAllText(file.FullName, "");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(AppInfo.LogFile, line);
            }
            catch
            {
            }
        }
#if DEBUG
        System.Diagnostics.Debug.Write(line);
#endif
    }
}
