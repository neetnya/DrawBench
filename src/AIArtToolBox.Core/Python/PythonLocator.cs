using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace AIArtToolBox.Core.Python;

/// <summary>一个可用的 Python 解释器：exe 路径 + 前缀参数（如 py 启动器要带 -3）。</summary>
public sealed class PythonInterpreter
{
    public PythonInterpreter(string exe, IReadOnlyList<string>? prefix = null, string? version = null)
    {
        Exe = exe;
        Prefix = prefix ?? Array.Empty<string>();
        Version = version;
    }

    public string Exe { get; }
    public IReadOnlyList<string> Prefix { get; }
    public string? Version { get; }

    public bool Found => !string.IsNullOrEmpty(Exe);

    /// <summary>界面上显示的一行说明。</summary>
    public string Describe()
    {
        if (!Found) return "未找到 Python";
        string prefix = Prefix.Count > 0 ? " " + string.Join(" ", Prefix) : "";
        return string.IsNullOrWhiteSpace(Version) ? Exe + prefix : $"{Exe}{prefix}（{Version}）";
    }
}

/// <summary>
/// 全应用唯一的 Python 探测：抽卡引擎和拼图脚本共用同一套逻辑。
/// 顺序：手动指定 → PATH 里的 python.exe（跳过 WindowsApps 占位）→ py -3 → python3。
/// </summary>
public static class PythonLocator
{
    private static readonly object Gate = new();
    private static PythonInterpreter? _cached;
    private static string? _cachedManualPath;

    /// <summary>探测（结果按 manualPath 缓存，界面上改了解释器路径就重新探）。</summary>
    public static PythonInterpreter Resolve(string? manualPath = null)
    {
        lock (Gate)
        {
            if (_cached != null && string.Equals(_cachedManualPath, manualPath, StringComparison.OrdinalIgnoreCase))
                return _cached;

            _cachedManualPath = manualPath;
            _cached = Detect(manualPath);
            return _cached;
        }
    }

    /// <summary>强制重新探测（用户在设置里点了「重新检测」）。</summary>
    public static PythonInterpreter Refresh(string? manualPath = null)
    {
        lock (Gate)
        {
            _cached = null;
            _cachedManualPath = null;
        }
        return Resolve(manualPath);
    }

    private static PythonInterpreter Detect(string? manualPath)
    {
        // 1) 用户手填的路径：可以是 python.exe，也可以是整个目录
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            var manual = TryManual(manualPath.Trim().Trim('"'));
            if (manual.Found) return manual;
        }

        // 2) PATH 里的 python.exe（Microsoft Store 的 WindowsApps 占位不算）
        foreach (string dir in PathDirectories())
        {
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = Path.Combine(dir, "python.exe");
            if (!File.Exists(candidate)) continue;

            var found = Probe(candidate);
            if (found.Found) return found;
        }

        // 3) 系统自带的 py 启动器
        string py = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
        if (File.Exists(py))
        {
            var found = Probe(py, new[] { "-3" });
            if (found.Found) return found;
        }

        // 4) 个别环境里只有 python3.exe
        foreach (string dir in PathDirectories())
        {
            string candidate = Path.Combine(dir, "python3.exe");
            if (!File.Exists(candidate)) continue;

            var found = Probe(candidate);
            if (found.Found) return found;
        }

        return new PythonInterpreter(string.Empty);
    }

    private static PythonInterpreter TryManual(string path)
    {
        try
        {
            if (Directory.Exists(path)) path = Path.Combine(path, "python.exe");
            if (!File.Exists(path)) return new PythonInterpreter(string.Empty);
            return Probe(path);
        }
        catch
        {
            return new PythonInterpreter(string.Empty);
        }
    }

    /// <summary>跑一次 python -V，拿到版本号才算可用（避免探测到失效的 stub）。</summary>
    private static PythonInterpreter Probe(string exe, IReadOnlyList<string>? prefix = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (string p in prefix ?? Array.Empty<string>()) psi.ArgumentList.Add(p);
            psi.ArgumentList.Add("-V");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return new PythonInterpreter(string.Empty);

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(true); } catch { }
                return new PythonInterpreter(string.Empty);
            }
            if (process.ExitCode != 0) return new PythonInterpreter(string.Empty);

            var match = Regex.Match(output, @"Python\s+([\d.]+)", RegexOptions.IgnoreCase);
            return new PythonInterpreter(exe, prefix, match.Success ? "Python " + match.Groups[1].Value : output.Trim());
        }
        catch
        {
            return new PythonInterpreter(string.Empty);
        }
    }

    private static IEnumerable<string> PathDirectories()
    {
        string raw;
        try { raw = Environment.GetEnvironmentVariable("PATH") ?? string.Empty; }
        catch { yield break; }

        foreach (string piece in raw.Split(';'))
        {
            string dir = piece.Trim().Trim('"');
            if (dir.Length == 0) continue;
            yield return dir;
        }
    }
}
