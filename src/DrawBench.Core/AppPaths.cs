using System;
using System.Diagnostics;
using System.IO;
using DrawBench.Core.Logging;

namespace DrawBench.Core;

/// <summary>
/// 便携目录约定：所有数据都在 exe 同级，删掉目录即恢复出厂。
/// </summary>
public sealed class AppPaths
{
    private static readonly Lazy<AppPaths> CurrentLazy = new(() => new AppPaths());

    /// <summary>进程内唯一的路径实例。</summary>
    public static AppPaths Current => CurrentLazy.Value;

    public AppPaths(string? appDir = null)
    {
        AppDir = Path.GetFullPath(appDir ?? ResolveAppDir());

        ApiDir = Path.Combine(AppDir, "api");
        EngineDir = Path.Combine(AppDir, "engine");
        ScriptsDir = Path.Combine(AppDir, "scripts");
        ModelsDir = Path.Combine(AppDir, "models");
        DataDir = Path.Combine(AppDir, "data");

        SettingsPath = Path.Combine(DataDir, "settings.json");
        TaggerCsvPath = Path.Combine(DataDir, "results.csv");
        LogPath = Path.Combine(DataDir, "app.log");
    }

    public string AppDir { get; }
    public string ApiDir { get; }
    public string EngineDir { get; }
    public string ScriptsDir { get; }
    public string ModelsDir { get; }
    public string DataDir { get; }
    public string SettingsPath { get; }
    public string TaggerCsvPath { get; }
    public string LogPath { get; }

    public string ScriptPath(string fileName) => Path.Combine(ScriptsDir, fileName);

    /// <summary>启动时建好所有目录。单项失败不抛，交给调用方按需处理。</summary>
    public void EnsureCreated()
    {
        foreach (string dir in new[] { ApiDir, EngineDir, ScriptsDir, ModelsDir, DataDir })
        {
            try { Directory.CreateDirectory(dir); } catch { /* 只读介质等情况忽略 */ }
        }
    }

    /// <summary>在资源管理器中打开目录（不存在则先创建）。</summary>
    public static void OpenInExplorer(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            AppLog.Error($"创建目录失败（{dir}）", ex);
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Error($"打开资源管理器失败（{dir}）", ex);
        }
    }

    /// <summary>exe 所在目录；找不到时退回当前 AppContext 目录。</summary>
    private static string ResolveAppDir()
    {
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }
}
