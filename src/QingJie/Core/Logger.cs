using System.IO;

namespace QingJie.Core;

internal static class Logger
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(AppPaths.DataDirectory, "qingjie.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFile, line);

                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    File.Delete(LogFile);
                }
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
