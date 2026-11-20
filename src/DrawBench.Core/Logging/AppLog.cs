using System;
using System.IO;
using System.Text;

namespace DrawBench.Core.Logging;

/// <summary>
/// 统一日志：写到 data\app.log，同时广播给界面（状态栏/日志面板）。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private const int MaxBytes = 2 * 1024 * 1024;

    public static string FilePath => AppPaths.Current.LogPath;

    /// <summary>新日志行（已带时间戳与级别）。</summary>
    public static event Action<string>? Line;

    public static void Info(string message) => Write("INFO", message, message);
    public static void Warn(string message) => Write("WARN", message, message);
    public static void Error(string message) => Write("ERROR", message, message);

    public static void Error(string message, Exception ex)
    {
        string summary = $"{message} {ex.GetType().Name}: {ex.Message}";
        string detail = $"{message}{Environment.NewLine}{ex}";
        Write("ERROR", detail, summary);
    }

    private static void Write(string level, string fileMessage, string broadcastMessage)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string fileLine = $"{timestamp} [{level}] {fileMessage}";
        string uiLine = $"{timestamp} [{level}] {broadcastMessage}";

        try
        {
            lock (Gate)
            {
                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string backup = path + ".1";
                    try { File.Move(path, backup, overwrite: true); } catch { }
                }

                File.AppendAllText(path, fileLine + Environment.NewLine, Utf8NoBom);
            }
        }
        catch
        {
            // 日志写不进去不能影响功能
        }

        var handlers = Line;
        if (handlers != null)
        {
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<string>)d).Invoke(uiLine); } catch { }
            }
        }
    }
}
