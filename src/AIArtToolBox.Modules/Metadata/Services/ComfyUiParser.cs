using System.Globalization;
using System.Text.Json.Nodes;
using AIArtToolBox.Modules.Metadata.Models;

namespace AIArtToolBox.Modules.Metadata.Services;

/// <summary>
/// ComfyUI 的 prompt（API 格式工作流 JSON）解析：
/// 顺着 KSampler 的 positive / negative 连线递归找到 CLIPTextEncode 的 text，
/// 顺便把 seed / steps / cfg / sampler / 尺寸 / 模型 / LoRA 都挖出来。
/// </summary>
public static class ComfyUiParser
{
    private static readonly HashSet<string> NonConditioningInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "clip", "vae", "latent_image", "samples", "image", "images",
        "clip_vision", "control_net", "style_model", "ipadapter", "mask",
        "upscale_model", "lora", "sample", "pixels"
    };

    public static bool TryParse(string json, GenerationParameters p)
    {
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(json);
        }
        catch
        {
            return false;
        }

        if (rootNode is not JsonObject root) return false;

        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var kv in root)
        {
            if (kv.Value is JsonObject node && node["class_type"] is not null)
                nodes[kv.Key] = node;
        }

        if (nodes.Count == 0) return false;

        p.Source = "ComfyUI";

        // 1. 找到带 positive / negative 连线的采样节点（KSampler 优先）
        JsonObject? host = nodes.Values.FirstOrDefault(n =>
            ClassName(n).Contains("KSampler", StringComparison.OrdinalIgnoreCase) &&
            IsLink(Input(n, "positive")) && IsLink(Input(n, "negative")));

        host ??= nodes.Values.FirstOrDefault(n => IsLink(Input(n, "positive")) && IsLink(Input(n, "negative")));

        // 2. 提示词
        var positiveTexts = new List<string>();
        var negativeTexts = new List<string>();

        if (host is not null)
        {
            CollectTexts(nodes, LinkId(Input(host, "positive")), positiveTexts, new HashSet<string>());
            CollectTexts(nodes, LinkId(Input(host, "negative")), negativeTexts, new HashSet<string>());
        }

        if (positiveTexts.Count > 0)
            p.PositivePrompt = string.Join("\n", positiveTexts.Distinct());
        if (negativeTexts.Count > 0)
            p.NegativePrompt = string.Join("\n", negativeTexts.Distinct());

        if (positiveTexts.Count == 0 && negativeTexts.Count == 0)
        {
            // 退化情况：连线断掉或非标准节点，按节点顺序取文本
            var all = new List<string>();
            foreach (var node in nodes.Values)
            {
                if (Input(node, "text") is JsonValue v && v.TryGetValue<string>(out var t) &&
                    !string.IsNullOrWhiteSpace(t) && !all.Contains(t))
                {
                    all.Add(t);
                }
            }

            if (all.Count == 1) p.PositivePrompt = all[0];
            else if (all.Count >= 2)
            {
                p.PositivePrompt = all[0];
                p.NegativePrompt = all[1];
            }
        }

        // 3. 采样参数
        p.Seed = JsonUtil.Long(Input(host, "seed"))
                 ?? JsonUtil.Long(Input(host, "noise_seed"))
                 ?? JsonUtil.Long(Input(FindNode(nodes, "RandomNoise"), "noise_seed"))
                 ?? JsonUtil.Long(Input(FindNode(nodes, "RandomNoise"), "seed"));

        p.Steps = JsonUtil.Int(Input(host, "steps"))
                  ?? JsonUtil.Int(Input(FindNode(nodes, "BasicScheduler"), "steps"))
                  ?? JsonUtil.Int(Input(FindNode(nodes, "Scheduler"), "steps"));

        p.Sampler = JsonUtil.Str(Input(host, "sampler_name"))
                    ?? JsonUtil.Str(Input(FindNode(nodes, "KSamplerSelect"), "sampler_name"));

        p.Scheduler = JsonUtil.Str(Input(host, "scheduler"))
                      ?? JsonUtil.Str(Input(FindNode(nodes, "BasicScheduler"), "scheduler"));

        p.CfgScale = JsonUtil.Double(Input(host, "cfg"))
                     ?? JsonUtil.Double(Input(FindNode(nodes, "CFGGuider"), "cfg"))
                     ?? JsonUtil.Double(Input(FindNode(nodes, "Guider"), "cfg"));

        p.Denoise = JsonUtil.Double(Input(host, "denoise"))
                    ?? JsonUtil.Double(Input(FindNode(nodes, "BasicScheduler"), "denoise"));

        // 4. 尺寸：顺着 latent_image 往上追
        ResolveSize(nodes, host, p);

        // 5. 模型 / VAE
        foreach (var node in nodes.Values)
        {
            var ckpt = JsonUtil.Str(Input(node, "ckpt_name"));
            if (!string.IsNullOrWhiteSpace(ckpt))
            {
                p.Model ??= ckpt;
                continue;
            }

            var unet = JsonUtil.Str(Input(node, "unet_name")) ?? JsonUtil.Str(Input(node, "model_name"));
            if (!string.IsNullOrWhiteSpace(unet)) p.Model ??= unet;

            var vae = JsonUtil.Str(Input(node, "vae_name"));
            if (!string.IsNullOrWhiteSpace(vae)) p.Vae ??= vae;
        }

        // 6. Clip Skip / LoRA / 输出前缀等杂项
        var loras = new List<string>();
        foreach (var node in nodes.Values)
        {
            string cls = ClassName(node);

            if (cls.Contains("CLIPSetLastLayer", StringComparison.OrdinalIgnoreCase))
            {
                var stopAt = JsonUtil.Int(Input(node, "stop_at_clip_layer"));
                if (stopAt.HasValue) p.ClipSkip = Math.Abs(stopAt.Value).ToString(CultureInfo.InvariantCulture);
            }

            if (cls.Contains("LoraLoader", StringComparison.OrdinalIgnoreCase))
            {
                var name = JsonUtil.Str(Input(node, "lora_name"));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var strength = JsonUtil.Double(Input(node, "strength_model"));
                    loras.Add(strength.HasValue
                        ? $"{name} ({strength.Value.ToString("0.##", CultureInfo.InvariantCulture)})"
                        : name!);
                }
            }
        }

        if (loras.Count > 0) p.Extra["LoRA"] = string.Join(", ", loras.Distinct());

        var prefix = JsonUtil.Str(Input(FindNode(nodes, "SaveImage"), "filename_prefix"));
        if (!string.IsNullOrWhiteSpace(prefix)) p.Extra["输出前缀"] = prefix!;

        p.Extra["节点数"] = nodes.Count.ToString(CultureInfo.InvariantCulture);

        return true;
    }

    // ------------------------------------------------------------------ 帮助方法

    private static string ClassName(JsonObject node)
        => JsonUtil.Str(node["class_type"]) ?? "";

    private static JsonObject? Inputs(JsonObject node)
        => node["inputs"] as JsonObject;

    private static JsonNode? Input(JsonObject? node, string key)
        => node is null ? null : (Inputs(node)?[key]);

    private static bool IsLink(JsonNode? node)
        => node is JsonArray array && array.Count >= 2 && array[0] is JsonValue value && value.TryGetValue<string>(out _);

    private static string? LinkId(JsonNode? node)
        => node is JsonArray array && array.Count >= 2 && array[0] is JsonValue value && value.TryGetValue<string>(out var id)
            ? id
            : null;

    private static JsonObject? FindNode(Dictionary<string, JsonObject> nodes, string classKeyword)
        => nodes.Values.FirstOrDefault(n => ClassName(n).Contains(classKeyword, StringComparison.OrdinalIgnoreCase));

    private static void CollectTexts(Dictionary<string, JsonObject> nodes, string? id, List<string> output, HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(id) || !visited.Add(id)) return;
        if (!nodes.TryGetValue(id, out var node)) return;
        if (Inputs(node) is not JsonObject inputs) return;

        // 文本叶子节点（text / SDXL 的 text_g、text_l）
        foreach (var key in new[] { "text", "text_g", "text_l", "prompt" })
        {
            if (!inputs.TryGetPropertyValue(key, out var textNode)) continue;
            string? text = JsonUtil.Str(textNode);
            if (!string.IsNullOrWhiteSpace(text) && textNode is JsonValue)
            {
                output.Add(text);
                return;
            }
        }

        // 其他字符串型“文本”节点（有些自定义节点用 string / value 作为输入名）
        if (inputs.TryGetPropertyValue("string", out var stringNode) &&
            JsonUtil.Str(stringNode) is { Length: > 0 } stringText &&
            inputs.Count <= 2)
        {
            output.Add(stringText);
            return;
        }

        foreach (var kv in inputs)
        {
            if (NonConditioningInputs.Contains(kv.Key)) continue;
            string? childId = LinkId(kv.Value);
            if (childId is not null) CollectTexts(nodes, childId, output, visited);
        }
    }

    private static void ResolveSize(Dictionary<string, JsonObject> nodes, JsonObject? host, GenerationParameters p)
    {
        // 首选：顺着 latent_image 往下追
        string? latentId = LinkId(Input(host, "latent_image"));
        if (latentId is not null)
        {
            var queue = new Queue<(string Id, int Depth)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue((latentId, 0));

            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                if (depth > 10 || string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                if (!nodes.TryGetValue(id, out var node)) continue;

                int? w = JsonUtil.Int(Input(node, "width"));
                int? h = JsonUtil.Int(Input(node, "height"));
                if (w is > 0 && h is > 0)
                {
                    p.Width = w;
                    p.Height = h;
                    return;
                }

                if (Inputs(node) is JsonObject inputs)
                {
                    foreach (var kv in inputs)
                    {
                        string? childId = LinkId(kv.Value);
                        if (childId is not null) queue.Enqueue((childId, depth + 1));
                    }
                }
            }
        }

        // 其次：EmptyLatent 系列节点
        if (TrySizeFromLatentNodes(nodes, p)) return;

        // 最后兜底：任何一个节点写了宽高
        foreach (var node in nodes.Values)
        {
            if (JsonUtil.Int(Input(node, "width")) is int w && JsonUtil.Int(Input(node, "height")) is int h &&
                w >= 64 && h >= 64)
            {
                p.Width = w;
                p.Height = h;
                return;
            }
        }
    }

    private static bool TrySizeFromLatentNodes(Dictionary<string, JsonObject> nodes, GenerationParameters p)
    {
        foreach (var node in nodes.Values)
        {
            if (!ClassName(node).Contains("Latent", StringComparison.OrdinalIgnoreCase)) continue;

            if (JsonUtil.Int(Input(node, "width")) is int w && JsonUtil.Int(Input(node, "height")) is int h &&
                w >= 64 && h >= 64)
            {
                p.Width = w;
                p.Height = h;
                return true;
            }
        }

        return false;
    }
}
