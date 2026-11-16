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
/// 模块之间投递文本的便捷入口。内部走 <see cref="SharedContext"/> 的投递表。
/// 「发送到」先清空目标框再填入；「追加到」保留已有内容往后追加。
/// </summary>
public static class AiBridge
{
    /// <summary>清空批量提交模块的正向队列，并填入新的正向提示词。</summary>
    public static bool SendToBatchQueue(string text, string source)
        => Deliver(ModuleIds.Batch, "replace:" + text, source);

    /// <summary>把正向提示词追加到批量提交模块的正向队列末尾。</summary>
    public static bool AppendToBatchQueue(string text, string source)
        => Deliver(ModuleIds.Batch, "append:" + text, source);

    /// <summary>替换批量提交模块的负向提示词。</summary>
    public static bool SendToBatchNegative(string text, string source)
        => Deliver(ModuleIds.Batch, "negative:" + text, source);

    /// <summary>把负向提示词追加到批量提交模块的负向提示词末尾。</summary>
    public static bool AppendToBatchNegative(string text, string source)
        => Deliver(ModuleIds.Batch, "negative-append:" + text, source);

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
