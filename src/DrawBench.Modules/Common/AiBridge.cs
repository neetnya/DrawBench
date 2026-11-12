using System;
using DrawBench.Core;

namespace DrawBench.Modules;

/// <summary>模块标识常量。外壳、设置、命令行都用这些字符串。</summary>
public static class ModuleIds
{
    public const string Gacha = "gacha";
    public const string Batch = "batch";
    public const string Metadata = "metadata";
    public const string Tagger = "tagger";
    public const string Collage = "collage";
}

/// <summary>
/// 模块之间投递文本的便捷入口。内部走 <see cref="SharedContext"/> 的投递表，
/// 目标模块没创建时不报错，返回 false 由调用方给提示。
/// </summary>
public static class AiBridge
{
    /// <summary>把正向提示词送进批量提交模块的队列。</summary>
    public static bool SendToBatchQueue(string text, string source)
        => Send(ModuleIds.Batch, text, source);

    /// <summary>把负向提示词送进批量提交模块。</summary>
    public static bool SendToBatchNegative(string text, string source)
        => Deliver(ModuleIds.Batch, "negative:" + text, source);

    public static bool Send(string targetModuleId, string text, string source)
        => Deliver(targetModuleId, text, source);

    private static bool Deliver(string moduleId, string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            return SharedContext.Current.Deliver(moduleId, text, source);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
