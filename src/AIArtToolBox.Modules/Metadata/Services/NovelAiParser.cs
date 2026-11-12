using System.Globalization;
using System.Text.Json.Nodes;
using AIArtToolBox.Modules.Metadata.Models;

namespace AIArtToolBox.Modules.Metadata.Services;

/// <summary>
/// NovelAI 的参数写在 PNG 的 Comment（JSON）里，Description 里通常是可读的提示词文本。
/// </summary>
public static class NovelAiParser
{
    public static bool TryParse(string json, GenerationParameters p, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        var trimmed = json.TrimStart();
        if (!trimmed.StartsWith('{')) return false;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(trimmed) as JsonObject;
        }
        catch
        {
            return false;
        }

        if (root is null) return false;
        if (root["class_type"] is not null) return false; // ComfyUI 的工作流

        bool looksLikeNovelAi =
            root.ContainsKey("noise_schedule") || root.ContainsKey("uc") ||
            (root.ContainsKey("prompt") && (root.ContainsKey("sampler") || root.ContainsKey("scale")));

        if (!looksLikeNovelAi) return false;

        p.Source = "NovelAI";

        p.PositivePrompt = (JsonUtil.Str(root["prompt"]) ?? description ?? "").Trim();
        if (string.IsNullOrWhiteSpace(p.PositivePrompt) && !string.IsNullOrWhiteSpace(description))
            p.PositivePrompt = description!.Trim();

        p.NegativePrompt = (JsonUtil.Str(root["uc"]) ?? "").Trim();
        p.Steps = JsonUtil.Int(root["steps"]);
        p.Sampler = JsonUtil.Str(root["sampler"]);
        p.Seed = JsonUtil.Long(root["seed"]);
        p.CfgScale = JsonUtil.Double(root["scale"]);
        p.Width = JsonUtil.Int(root["width"]);
        p.Height = JsonUtil.Int(root["height"]);

        string? noiseSchedule = JsonUtil.Str(root["noise_schedule"]);
        if (!string.IsNullOrWhiteSpace(noiseSchedule)) p.Scheduler = noiseSchedule;

        AddIfTrue(p, root, "sm", "SMEA");
        AddIfTrue(p, root, "sm_dyn", "SMEA+DYN");
        AddIfPresent(p, root, "dynamic_thresholding", "动态阈值");
        AddIfPresent(p, root, "cfg_rescale", "CFG Rescale");
        AddIfPresent(p, root, "ucPreset", "UC Preset");
        AddIfPresent(p, root, "qualityToggle", "Quality Tags");

        string? model = JsonUtil.Str(root["model"]);
        if (!string.IsNullOrWhiteSpace(model)) p.Model = model;

        return true;
    }

    private static void AddIfPresent(GenerationParameters p, JsonObject root, string key, string? label)
    {
        if (label is null) return;
        var node = root[key];
        if (node is null) return;

        string? value = JsonUtil.Str(node);
        if (string.IsNullOrWhiteSpace(value)) return;
        if (p.Extra.ContainsKey(label)) return;
        p.Extra[label] = value;
    }

    private static void AddIfTrue(GenerationParameters p, JsonObject root, string key, string label)
    {
        var value = JsonUtil.Bool(root[key]);
        if (value == true) p.Extra[label] = "开";
        else if (value == false) p.Extra[label] = "关";
    }
}
