using System;
using System.IO;

namespace DrawBench.Core;

/// <summary>
/// 便携目录约定：所有数据都在 exe 同级，删掉目录即恢复出厂。
///
/// <code>
/// DrawBench.exe
/// api\         ComfyUI 工作流 JSON（批量提交模块）
/// engine\      抽卡引擎 Python 脚本
/// scripts\     拼图脚本 gen_sheets_cli.py
/// models\      反推模型 onnx + csv
/// data\        全部配置与结果（settings.json / results.csv / 日志）
/// </code>
///
/// 开发期程序在 build\App\Debug\... 下，会向上找带 api 目录的仓库根，
/// 保证源码树里的 api\ 和 engine\ 与发布包行为一致。
/// </summary>
public sealed class AppPaths
{
    private static AppPaths? _current;

    /// <summary>进程内唯一的路径实例。</summary>
    public static AppPaths Current => _current ??= new AppPaths();

    public AppPaths(string? appDir = null)
    {
        AppDir = Path.GetFullPath(appDir ?? ResolveAppDir());
        ToolDir = ResolveToolDir(AppDir);

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
    public string ToolDir { get; }
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
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // 创建失败也继续尝试打开，交给 explorer 报错
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{dir}\"",
            UseShellExecute = true
        });
    }

    /// <summary>exe 所在目录；找不到时退回当前 AppContext 目录。</summary>
    private static string ResolveAppDir()
    {
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// 开发期修正：如果 exe 同级没有 api/engine/models 这类数据目录，
    /// 就向上最多 6 层找「有 api 或 engine 子目录」的仓库根，用它当数据根。
    /// 发布包里 exe 同级就有这些目录，这段不会生效。
    /// </summary>
    private static string ResolveToolDir(string appDir)
    {
        if (HasDataDirs(appDir)) return appDir;

        var info = new DirectoryInfo(appDir);
        for (int i = 0; i < 6 && info?.Parent != null; i++)
        {
            info = info.Parent;
            if (info != null && HasDataDirs(info.FullName)) return info.FullName;
        }

        return appDir;
    }

    private static bool HasDataDirs(string dir)
    {
        try
        {
            return Directory.Exists(Path.Combine(dir, "api")) ||
                   Directory.Exists(Path.Combine(dir, "engine"));
        }
        catch
        {
            return false;
        }
    }

}
