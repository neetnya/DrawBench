using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DrawBench.Core.Python;

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
/// 全应用唯一的 Python 探测：拼图等需要 Python 的脚本共用同一套逻辑。
/// 顺序：手动指定 → PATH 里的 python.exe（跳过 WindowsApps 占位）→ py -3 → python3。
/// </summary>
public static class PythonLocator
{
    private const int ProbeTimeoutMs = 8000;

    private static readonly object CacheLock = new();
    private static PythonInterpreter? _cached;
    private static string? _cachedManualPath;

    /// <summary>同步探测（兼容旧调用；结果按 manualPath 缓存，锁内不执行子进程）。</summary>
    public static PythonInterpreter Resolve(string? manualPath = null)
    {
        if (TryGetCached(manualPath, out var cached))
            return cached;

        var detected = DetectAsync(manualPath, CancellationToken.None).GetAwaiter().GetResult();
        UpdateCache(manualPath, detected);
        return detected;
    }

    /// <summary>异步探测（推荐在 UI 中调用，不阻塞主线程）。</summary>
    public static async Task<PythonInterpreter> ResolveAsync(string? manualPath = null, CancellationToken ct = default)
    {
        if (TryGetCached(manualPath, out var cached))
            return cached;

        var detected = await Task.Run(() => DetectAsync(manualPath, ct), ct).ConfigureAwait(false);
        UpdateCache(manualPath, detected);
        return detected;
    }

    /// <summary>强制重新探测（同步版）。</summary>
    public static PythonInterpreter Refresh(string? manualPath = null)
    {
        ClearCache();
        return Resolve(manualPath);
    }

    /// <summary>强制重新探测（异步版）。</summary>
    public static Task<PythonInterpreter> RefreshAsync(string? manualPath = null, CancellationToken ct = default)
    {
        ClearCache();
        return ResolveAsync(manualPath, ct);
    }

    private static bool TryGetCached(string? manualPath, out PythonInterpreter interpreter)
    {
        lock (CacheLock)
        {
            if (_cached != null && string.Equals(_cachedManualPath, manualPath, StringComparison.OrdinalIgnoreCase))
            {
                interpreter = _cached;
                return true;
            }
        }
        interpreter = null!;
        return false;
    }

    private static void UpdateCache(string? manualPath, PythonInterpreter detected)
    {
        lock (CacheLock)
        {
            _cachedManualPath = manualPath;
            _cached = detected;
        }
    }

    private static void ClearCache()
    {
        lock (CacheLock)
        {
            _cached = null;
            _cachedManualPath = null;
        }
    }

    private static async Task<PythonInterpreter> DetectAsync(string? manualPath, CancellationToken ct)
    {
        // 1) 用户手填的路径：可以是 python.exe，也可以是整个目录
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            var manual = await TryManualAsync(manualPath.Trim().Trim('"'), ct).ConfigureAwait(false);
            if (manual.Found) return manual;
        }

        // 2) PATH 里的 python.exe（Microsoft Store 的 WindowsApps 占位不算）
        foreach (string dir in PathDirectories())
        {
            ct.ThrowIfCancellationRequested();
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = Path.Combine(dir, "python.exe");
            if (!File.Exists(candidate)) continue;

            var found = await ProbeAsync(candidate, null, ct).ConfigureAwait(false);
            if (found.Found) return found;
        }

        // 3) 系统自带的 py 启动器
        string py = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
        if (File.Exists(py))
        {
            var found = await ProbeAsync(py, new[] { "-3" }, ct).ConfigureAwait(false);
            if (found.Found) return found;
        }

        // 4) 个别环境里只有 python3.exe（同样跳过 WindowsApps 占位）
        foreach (string dir in PathDirectories())
        {
            ct.ThrowIfCancellationRequested();
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            string candidate = Path.Combine(dir, "python3.exe");
            if (!File.Exists(candidate)) continue;

            var found = await ProbeAsync(candidate, null, ct).ConfigureAwait(false);
            if (found.Found) return found;
        }

        return new PythonInterpreter(string.Empty);
    }

    private static async Task<PythonInterpreter> TryManualAsync(string path, CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(path)) path = Path.Combine(path, "python.exe");
            if (!File.Exists(path)) return new PythonInterpreter(string.Empty);
            return await ProbeAsync(path, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new PythonInterpreter(string.Empty);
        }
    }

    /// <summary>跑一次 python -V，并发读双流 + 真实 8 秒超时，避免管道死锁与挂起。</summary>
    private static async Task<PythonInterpreter> ProbeAsync(string exe, IReadOnlyList<string>? prefix, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string p in prefix ?? Array.Empty<string>()) psi.ArgumentList.Add(p);
            psi.ArgumentList.Add("-V");

            using var process = Process.Start(psi);
            if (process == null) return new PythonInterpreter(string.Empty);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeoutMs);

            var outTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return new PythonInterpreter(string.Empty);
            }

            if (process.ExitCode != 0) return new PythonInterpreter(string.Empty);

            string output = (outTask.Result ?? "") + (errTask.Result ?? "");
            var match = Regex.Match(output, @"Python\s+([\d.]+)", RegexOptions.IgnoreCase);
            return new PythonInterpreter(exe, prefix, match.Success ? "Python " + match.Groups[1].Value : output.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
