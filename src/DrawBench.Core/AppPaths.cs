using System;
using System.Collections.Generic;
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

    /// <summary>
    /// 旧版本数据迁移：把五个独立工具留下的配置/结果搬进 data\。
    /// 只在目标不存在时复制，复制成功后旧文件改名为 *.migrated 避免二次迁移。
    /// </summary>
    public List<string> MigrateLegacy()
    {
        var notes = new List<string>();
        IReadOnlyList<string> roots = LegacyRoots();

        // 反推结果：旧版在 exe 同级 data\results.csv（或 data\ 下），搬进新的 data\results.csv
        foreach (string root in roots)
        {
            MigrateFile(Path.Combine(root, "data", "results.csv"), TaggerCsvPath, "反推结果", notes);
            MigrateFile(Path.Combine(root, "results.csv"), TaggerCsvPath, "反推结果", notes);
        }

        return notes;
    }

    private void MigrateFile(string legacyPath, string targetPath, string label, List<string> notes)
    {
        try
        {
            if (!File.Exists(legacyPath)) return;
            if (File.Exists(targetPath)) return;
            if (IsSamePath(legacyPath, targetPath)) return;

            File.Copy(legacyPath, targetPath, overwrite: false);
            try { File.Move(legacyPath, legacyPath + ".migrated", overwrite: true); } catch { }
            notes.Add($"已迁移{label}：{legacyPath} → {targetPath}");
        }
        catch (Exception ex)
        {
            notes.Add($"迁移{label}失败（{legacyPath}）：{ex.Message}");
        }
    }

    private static bool IsSamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>可能留有旧数据的几个根目录：软件目录、上一个工作目录、用户 AppData。</summary>
    private static List<string> LegacyRoots()
    {
        var list = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!list.Contains(dir, StringComparer.OrdinalIgnoreCase)) list.Add(dir);
        }

        Add(Current?.AppDir);
        try { Add(Environment.CurrentDirectory); } catch { }
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ComfyCollage"));
        return list;
    }
}
