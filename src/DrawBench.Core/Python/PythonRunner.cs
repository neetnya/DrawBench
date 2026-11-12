using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DrawBench.Core.Python;

/// <summary>一次 Python 调用的结果。</summary>
public sealed class PythonResult
{
    public bool Ok => Failure == null && ExitCode == 0;

    public int ExitCode { get; init; }

    /// <summary>非空 = 进程根本没跑起来或用户取消。</summary>
    public string? Failure { get; init; }

    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;

    public static PythonResult Fail(string message) => new() { Failure = message, ExitCode = -1 };
}

/// <summary>
/// 起一个 Python 子进程跑脚本，逐行回传输出，支持取消。
/// 抽卡（读 JSON）和拼图（读进度标记）都走这里，只是回调不同。
/// </summary>
public static class PythonRunner
{
    /// <param name="interpreter">解释器（<see cref="PythonLocator"/> 探测结果）。</param>
    /// <param name="scriptPath">脚本绝对路径。</param>
    /// <param name="args">脚本参数（不含解释器和脚本路径）。</param>
    /// <param name="onLine">每读到一行调用一次；isError 标明来自 stderr。</param>
    /// <param name="workingDirectory">工作目录，默认脚本所在目录。</param>
    public static async Task<PythonResult> RunAsync(
        PythonInterpreter interpreter,
        string scriptPath,
        IReadOnlyList<string> args,
        Action<string, bool>? onLine = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        if (!interpreter.Found)
            return PythonResult.Fail("找不到 Python。请安装 Python 3.8 以上版本并加入 PATH，或在设置里手动指定解释器。");

        if (!File.Exists(scriptPath))
            return PythonResult.Fail("找不到脚本：" + scriptPath);

        var psi = new ProcessStartInfo
        {
            FileName = interpreter.Exe,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string p in interpreter.Prefix) psi.ArgumentList.Add(p);
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add("utf8");
        psi.ArgumentList.Add("-u");           // 关缓冲，日志才能逐行到达
        psi.ArgumentList.Add(scriptPath);
        foreach (string a in args) psi.ArgumentList.Add(a);

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stdout) stdout.AppendLine(e.Data);
                onLine?.Invoke(e.Data, false);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderr) stderr.AppendLine(e.Data);
                onLine?.Invoke(e.Data, true);
            };

            if (!process.Start())
                return PythonResult.Fail("无法启动 Python 进程：" + interpreter.Exe);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return PythonResult.Fail("已取消");
            }

            string outText, errText;
            lock (stdout) outText = stdout.ToString();
            lock (stderr) errText = stderr.ToString();

            return new PythonResult
            {
                ExitCode = process.ExitCode,
                StdOut = outText,
                StdErr = errText
            };
        }
        catch (OperationCanceledException)
        {
            return PythonResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            return PythonResult.Fail("启动 Python 失败：" + ex.Message + "\n（当前解释器：" + interpreter.Exe + "）");
        }
    }

    /// <summary>把脚本从软件目录 / 源码目录里找出来：先看约定目录，再向上回溯几层。</summary>
    public static string ResolveScript(string fileName, string preferredDir)
    {
        try
        {
            string direct = Path.Combine(preferredDir, fileName);
            if (File.Exists(direct)) return direct;

            string current = Path.Combine(Environment.CurrentDirectory, fileName);
            if (File.Exists(current)) return current;

            var info = new DirectoryInfo(preferredDir);
            for (int i = 0; i < 6 && info != null; i++)
            {
                string candidate = Path.Combine(info.FullName, fileName);
                if (File.Exists(candidate)) return candidate;
                info = info.Parent;
            }

            return direct;
        }
        catch
        {
            return Path.Combine(preferredDir, fileName);
        }
    }
}
