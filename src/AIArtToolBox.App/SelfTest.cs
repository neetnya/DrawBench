using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AIArtToolBox.Core;
using AIArtToolBox.Core.Logging;
using AIArtToolBox.Modules.Metadata.Services;
using AIArtToolBox.Modules.Metadata.Models;

namespace AIArtToolBox.App;

/// <summary>
/// 开发期自检：AIArtToolBox.exe --selftest &lt;图片目录&gt;
/// 不走界面，直接调用各模块的解析/服务代码，把结果写进日志并输出到 stdout。
/// 用来在没有 ComfyUI、没有模型的情况下验证「解析元数据」这类纯逻辑。
/// </summary>
internal static class SelfTest
{
    public static bool WantsSelfTest(string[] args)
        => args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

    public static string? TargetDir(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    public static int Run(string folder)
    {
        Console.WriteLine("== 参数读取自检 ==");
        Console.WriteLine("目录：" + folder);

        int code = RunMetadata(folder);
        RunBridgeCheck();

        return code;
    }

    /// <summary>验证跨模块投递：抽卡抽到的文本能不能进批量提交队列。</summary>
    private static void RunBridgeCheck()
    {
        Console.WriteLine("\n== 跨模块投递自检 ==");

        var batch = new AIArtToolBox.Modules.Batch.BatchModule();
        batch.CreateView();   // 注册接收器

        const string sample = "1girl, blue eyes, blonde hair, white dress, in garden, sunset";
        bool sent = AIArtToolBox.Modules.AiBridge.SendToBatchQueue(sample, "自检");
        Console.WriteLine(sent ? "投递返回 true" : "投递返回 false");

        if (batch.CreateView() is AIArtToolBox.Modules.Batch.Views.BatchView view)
        {
            string content = view.PromptQueueText;
            bool hit = content.Contains("blue eyes", StringComparison.Ordinal);
            Console.WriteLine(hit ? "队列中已出现投递的提示词：" + Truncate(content, 90)
                                  : "队列里没有投递的文本（当前：" + Truncate(content, 60) + "）");
        }
    }

    private static int RunMetadata(string folder)
    {
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.png").OrderBy(f => f).ToList()
            : new List<string>();

        if (files.Count == 0)
        {
            Console.WriteLine("没有找到 png");
            return 2;
        }

        int ok = 0;
        foreach (string file in files)
        {
            try
            {
                var chunks = ImageContainerReader.ReadTextChunks(file);
                var parsed = ParameterParser.Parse(chunks);

                Console.WriteLine($"\n-- {Path.GetFileName(file)}");
                Console.WriteLine($"   来源     : {parsed.Source}");
                Console.WriteLine($"   模型     : {parsed.Model}");
                Console.WriteLine($"   采样器   : {parsed.Sampler}");
                Console.WriteLine($"   步数/CFG : {parsed.Steps} / {parsed.CfgScale}");
                Console.WriteLine($"   Seed     : {parsed.Seed}");
                Console.WriteLine($"   尺寸     : {parsed.Width}x{parsed.Height}");
                Console.WriteLine($"   正向     : {Truncate(parsed.PositivePrompt, 80)}");
                Console.WriteLine($"   负向     : {Truncate(parsed.NegativePrompt, 60)}");

                if (!string.IsNullOrWhiteSpace(parsed.PositivePrompt)) ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   解析失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"\n共 {files.Count} 张，成功解析 {ok} 张");
        return ok > 0 ? 0 : 1;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        string oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
