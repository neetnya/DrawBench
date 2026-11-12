using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AIArtToolBox.Core.Cli;

/// <summary>
/// 无界面入口。原来的 WD14Tagger.exe 支持 <c>--cli</c> 批量反推，整合后仍要能用：
///
/// <code>
/// AIArtToolBox.exe tagger --cli &lt;目录或图片...&gt; [参数]    新写法
/// AIArtToolBox.exe --cli &lt;目录或图片...&gt; [参数]            旧写法（等同 tagger）
/// </code>
///
/// 各模块把自己的处理函数注册进来；没注册的子命令给出明确提示。
/// </summary>
public static class CliDispatcher
{
    /// <summary>子命令 → 处理函数（参数数组含子命令本身，返回进程退出码）。</summary>
    private static readonly Dictionary<string, Func<string[], AppPaths, int>> Handlers =
        new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    public static void Register(string subCommand, Func<string[], AppPaths, int> handler)
    {
        if (string.IsNullOrWhiteSpace(subCommand) || handler == null) return;
        Handlers[subCommand] = handler;
    }

    /// <summary>有 --cli 标记即视为命令行模式（避免带参启动界面时误判）。</summary>
    public static bool WantsCli(string[] args)
    {
        return args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把控制台接回调用它的终端，否则 Console.WriteLine 看不到。</summary>
    public static void AttachConsole()
    {
        try
        {
            AttachConsole(AttachParentProcess);
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 没有父控制台时忽略（比如从资源管理器双击）
        }
    }

    public static int Run(string[] args, AppPaths paths)
    {
        // 第一个非选项参数且已注册 → 当作子命令
        string? sub = args.FirstOrDefault(a => !a.StartsWith('-') && Handlers.ContainsKey(a));

        if (sub != null)
        {
            return Handlers[sub](args, paths);
        }

        // 旧写法：--cli 直接跟路径，默认走 tagger
        if (Handlers.TryGetValue("tagger", out var tagger))
        {
            return tagger(args, paths);
        }

        Console.Error.WriteLine("没有可用的命令行模块。");
        return 2;
    }

    /// <summary>给用户看的帮助文本。</summary>
    public static string HelpText(IEnumerable<string> available)
    {
        string subs = string.Join(" / ", available);
        return $"AIArtToolBox 命令行：\n  AIArtToolBox.exe <{subs}> --cli <参数>\n";
    }
}
