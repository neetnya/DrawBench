using System.Text;
using AIArtToolBox.Modules.Metadata.Models;

namespace AIArtToolBox.Modules.Metadata.Services;

/// <summary>
/// 把容器里抓到的一堆文本块，按“谁最像哪种生成工具”依次尝试解析。
/// </summary>
public static class ParameterParser
{
    private const int MaxRawChars = 200_000;

    public static GenerationParameters Parse(IReadOnlyDictionary<string, string> chunks)
    {
        var p = new GenerationParameters { RawText = BuildRawText(chunks) };

        if (chunks.Count == 0)
        {
            p.Source = "无元数据";
            return p;
        }

        // 1. Stable Diffusion WebUI / Forge —— parameters 块
        if (TryGet(chunks, "parameters", out var parametersText) && WebUiParameterParser.TryParse(parametersText, p))
            return Finish(p);

        // 2. ComfyUI —— prompt 块（API 格式工作流）
        if (TryGet(chunks, "prompt", out var comfyText) && ComfyUiParser.TryParse(comfyText, p))
        {
            if (TryGet(chunks, "workflow", out var workflow)) p.WorkflowJson = workflow;
            return Finish(p);
        }

        // 3. NovelAI —— Comment(JSON) + Description(文本)
        if (TryGet(chunks, "Comment", out var comment) &&
            NovelAiParser.TryParse(comment, p, TryGet(chunks, "Description", out var description) ? description : null))
        {
            return Finish(p);
        }

        // 4. 各种包一层的写法：EXIF UserComment / XMP / 自定义 key
        foreach (var kv in chunks)
        {
            string value = kv.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;

            string trimmed = value.Trim();

            if (trimmed.Contains("\"class_type\"", StringComparison.Ordinal) &&
                ComfyUiParser.TryParse(StripJsonPrefix(trimmed), p))
            {
                return Finish(p);
            }

            if (trimmed.StartsWith('{') && NovelAiParser.TryParse(trimmed, p)) return Finish(p);

            if (value.Contains("Negative prompt:", StringComparison.Ordinal) &&
                WebUiParameterParser.TryParse(value, p))
            {
                return Finish(p);
            }

            if (trimmed.StartsWith("parameters:", StringComparison.OrdinalIgnoreCase) &&
                WebUiParameterParser.TryParse(trimmed["parameters:".Length..], p))
            {
                return Finish(p);
            }
        }

        // 5. 只有纯文本提示词的情况
        if (TryGet(chunks, "Description", out var descText) && !string.IsNullOrWhiteSpace(descText))
        {
            p.Source = "NovelAI";
            p.PositivePrompt = descText.Trim();
            return Finish(p);
        }

        foreach (var kv in chunks)
        {
            string value = kv.Value?.Trim() ?? "";
            if (value.Length < 8) continue;
            if (IsNonPromptKey(kv.Key)) continue;

            p.PositivePrompt = value;
            p.Source = "未知格式（按原文显示）";
            return Finish(p);
        }

        p.Source = "有元数据但未识别";
        return Finish(p);
    }

    private static bool IsNonPromptKey(string key) => key.ToLowerInvariant() is
        "software" or "exif:software" or "exif:make" or "exif:model" or
        "exif:datetime" or "exif:artist" or "xmp" or "comment";

    private static GenerationParameters Finish(GenerationParameters p)
    {
        if (string.IsNullOrWhiteSpace(p.Source)) p.Source = "未知";
        return p;
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> chunks, string key, out string value)
    {
        if (chunks.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value)) return true;

        foreach (var kv in chunks)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kv.Value))
            {
                value = kv.Value;
                return true;
            }
        }

        value = "";
        return false;
    }

    private static string StripJsonPrefix(string text)
    {
        int index = text.IndexOf('{');
        return index > 0 ? text[index..] : text;
    }

    private static string BuildRawText(IReadOnlyDictionary<string, string> chunks)
    {
        var builder = new StringBuilder();
        foreach (var kv in chunks)
        {
            builder.Append('[').Append(kv.Key).Append(']').Append('\n');
            string value = kv.Value;
            if (value.Length > MaxRawChars)
                value = value[..MaxRawChars] + "\n…（已截断）";
            builder.Append(value).Append("\n\n");
        }
        return builder.ToString().TrimEnd();
    }
}
