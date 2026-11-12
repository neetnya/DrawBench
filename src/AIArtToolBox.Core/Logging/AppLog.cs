using System;
using System.IO;
using System.Text;

namespace AIArtToolBox.Core.Logging;

/// <summary>
/// 统一日志：写到 data\app.log，同时广播给界面（状态栏/日志面板）。
/// 每个模块原本各自弹窗或写自己的日志，这里收成一处，出问题只要一个文件。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly int MaxBytes = 2 * 1024 * 1024;

    public static string FilePath => AppPaths.Current.LogPath;

    /// <summary>新日志行（已带时间戳与级别）。</summary>
    public static event Action<string>? Line;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex)
        => Write("ERROR", message + " " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";

        try
        {
            lock (Gate)
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // 简单滚动：超过 2 MB 就把旧文件挪成 .1，不要无限长
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string backup = path + ".1";
                    try { File.Move(path, backup, overwrite: true); } catch { }
                }

                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写不进去不能影响功能
        }

        try { Line?.Invoke(line); } catch { }
    }
}
