using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrawBench.Core.Logging;

namespace DrawBench.Core.Python;

/// <summary>一次 Python 调用的结果。</summary>
public sealed class PythonResult
{
    public bool Ok => Failure == null && !IsCancelled && ExitCode == 0;

    public bool IsCancelled { get; init; }

    public int ExitCode { get; init; }

    /// <summary>非空 = 进程根本没跑起来或用户取消。</summary>
    public string? Failure { get; init; }

    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;

    public static PythonResult Fail(string message) => new() { Failure = message, ExitCode = -1 };

    public static PythonResult Cancelled() => new() { IsCancelled = true, Failure = "已取消", ExitCode = -1 };
}

/// <summary>
/// 起一个 Python 子进程跑脚本，逐行回传输出，支持取消。
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
        var outEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    outEof.TrySetResult();
                    return;
                }
                lock (stdout) stdout.AppendLine(e.Data);
                InvokeSafe(onLine, e.Data, isError: false);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    errEof.TrySetResult();
                    return;
                }
                lock (stderr) stderr.AppendLine(e.Data);
                InvokeSafe(onLine, e.Data, isError: true);
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
                try
                {
                    using var killWait = new CancellationTokenSource(2000);
                    await process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                }
                catch { }
                return PythonResult.Cancelled();
            }

            // 等待异步输出管道真正收到 EOF，避免丢失退出瞬间的尾部日志
            using (var drainCts = new CancellationTokenSource(3000))
            {
                try
                {
                    await Task.WhenAll(outEof.Task, errEof.Task).WaitAsync(drainCts.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 极端情况下子进程孙进程占用句柄，超时后继续返回已有内容
                }
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
            return PythonResult.Cancelled();
        }
        catch (Exception ex)
        {
            return PythonResult.Fail("启动 Python 失败：" + ex.Message + "\n（当前解释器：" + interpreter.Exe + "）");
        }
    }

    private static void InvokeSafe(Action<string, bool>? onLine, string line, bool isError)
    {
        if (onLine == null) return;
        try
        {
            onLine(line, isError);
        }
        catch (Exception ex)
        {
            AppLog.Error("Python 输出回调异常", ex);
        }
    }
}
