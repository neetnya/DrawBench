using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using DrawBench.Modules.Batch.Models;

namespace DrawBench.Modules.Batch.Services;

/// <summary>
/// 根据 WorkflowApiConfig 实时生成 ComfyUI API 格式的 JsonObject；
/// 同时提供从旧版 api\*.json 一键导入为配置项的辅助方法。
/// </summary>
public static class WorkflowApiBuilder
{
    private const string PositivePlaceholder = "Positive Prompt";
    private const string NegativePlaceholder = "Negative Prompt";

    /// <summary>
    /// 将可视化工作流配置编译为可直接提交给 ComfyUI /prompt 的 API JSON。
    /// 触发词按 lora.LoraName 从全局 loraTriggerWords 字典读取（跟着 LoRA 走，不跟底模绑死）。
    /// </summary>
    public static JsonObject Build(
        WorkflowApiConfig config,
        IReadOnlyDictionary<string, string>? loraTriggerWords,
        string queuePositivePrompt,
        string overrideNegativePrompt,
        string dateSuffix,
        long seed)
    {
        var root = new JsonObject();
        int nextId = 1;
        string NextNodeId() => (nextId++).ToString();

        JsonArray modelRef;
        JsonArray clipRef;
        JsonArray vaeRef;

        if (config.LoaderMode == WorkflowLoaderMode.Checkpoint)
        {
            string ckptId = NextNodeId();
            root[ckptId] = new JsonObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JsonObject
                {
                    ["ckpt_name"] = config.CheckpointName.Trim()
                },
                ["_meta"] = new JsonObject { ["title"] = "Checkpoint加载器（简易）" }
            };
            modelRef = new JsonArray(ckptId, 0);
            clipRef = new JsonArray(ckptId, 1);
            vaeRef = new JsonArray(ckptId, 2);
        }
        else
        {
            string unetId = NextNodeId();
            root[unetId] = new JsonObject
            {
                ["class_type"] = "UNETLoader",
                ["inputs"] = new JsonObject
                {
                    ["unet_name"] = config.UnetName.Trim(),
                    ["weight_dtype"] = string.IsNullOrWhiteSpace(config.WeightDtype) ? "default" : config.WeightDtype.Trim()
                },
                ["_meta"] = new JsonObject { ["title"] = "UNet加载器" }
            };

            string clipId = NextNodeId();
            root[clipId] = new JsonObject
            {
                ["class_type"] = "CLIPLoader",
                ["inputs"] = new JsonObject
                {
                    ["clip_name"] = config.ClipName.Trim(),
                    ["type"] = string.IsNullOrWhiteSpace(config.ClipType) ? "stable_diffusion" : config.ClipType.Trim(),
                    ["device"] = string.IsNullOrWhiteSpace(config.ClipDevice) ? "default" : config.ClipDevice.Trim()
                },
                ["_meta"] = new JsonObject { ["title"] = "加载CLIP" }
            };

            string vaeId = NextNodeId();
            root[vaeId] = new JsonObject
            {
                ["class_type"] = "VAELoader",
                ["inputs"] = new JsonObject
                {
                    ["vae_name"] = config.VaeName.Trim()
                },
                ["_meta"] = new JsonObject { ["title"] = "加载VAE" }
            };

            modelRef = new JsonArray(unetId, 0);
            clipRef = new JsonArray(clipId, 0);
            vaeRef = new JsonArray(vaeId, 0);
        }

        // 串联所有启用中的 LoRA，并从全局 LoRA 触发词库收集触发词
        var triggerParts = new List<string>();
        foreach (var lora in config.Loras.Where(l => l.Enabled && !string.IsNullOrWhiteSpace(l.LoraName)))
        {
            string loraName = lora.LoraName.Trim();
            string loraId = NextNodeId();
            if (config.LoaderMode == WorkflowLoaderMode.Checkpoint)
            {
                root[loraId] = new JsonObject
                {
                    ["class_type"] = "LoraLoader",
                    ["inputs"] = new JsonObject
                    {
                        ["lora_name"] = loraName,
                        ["strength_model"] = lora.StrengthModel,
                        ["strength_clip"] = lora.StrengthClip,
                        ["model"] = modelRef,
                        ["clip"] = clipRef
                    },
                    ["_meta"] = new JsonObject { ["title"] = "加载LoRA" }
                };
                modelRef = new JsonArray(loraId, 0);
                clipRef = new JsonArray(loraId, 1);
            }
            else
            {
                root[loraId] = new JsonObject
                {
                    ["class_type"] = "LoraLoaderModelOnly",
                    ["inputs"] = new JsonObject
                    {
                        ["lora_name"] = loraName,
                        ["strength_model"] = lora.StrengthModel,
                        ["model"] = modelRef
                    },
                    ["_meta"] = new JsonObject { ["title"] = "LoRA加载器（仅模型）" }
                };
                modelRef = new JsonArray(loraId, 0);
            }

            string rawTrigger = string.Empty;
            if (loraTriggerWords is not null && loraTriggerWords.TryGetValue(loraName, out var globalTrigger))
                rawTrigger = globalTrigger;

            string trigger = CleanPromptFragment(rawTrigger);
            if (trigger.Length > 0) triggerParts.Add(trigger);
        }

        // 组合正向提示词：模板底词 + LoRA 触发词 + 队列提示词
        string combinedQueuePrompt = triggerParts.Count == 0
            ? CleanPromptFragment(queuePositivePrompt)
            : JoinPromptParts(string.Join(", ", triggerParts), CleanPromptFragment(queuePositivePrompt));

        string finalPositive = MergePrompt(config.PositiveTemplate, combinedQueuePrompt, PositivePlaceholder);

        // 负面提示词：主界面负面词为空时保持模板原文，非空时按占位符或追加规则写入
        string finalNegative = string.IsNullOrWhiteSpace(overrideNegativePrompt)
            ? CleanPromptFragment(config.NegativeTemplate)
            : MergePrompt(config.NegativeTemplate, CleanPromptFragment(overrideNegativePrompt), NegativePlaceholder);

        string posId = NextNodeId();
        root[posId] = new JsonObject
        {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JsonObject
            {
                ["text"] = finalPositive,
                ["clip"] = clipRef.DeepClone()
            },
            ["_meta"] = new JsonObject { ["title"] = "Positive Prompt" }
        };

        string negId = NextNodeId();
        root[negId] = new JsonObject
        {
            ["class_type"] = "CLIPTextEncode",
            ["inputs"] = new JsonObject
            {
                ["text"] = finalNegative,
                ["clip"] = clipRef.DeepClone()
            },
            ["_meta"] = new JsonObject { ["title"] = "Negative Prompt" }
        };

        string latentId = NextNodeId();
        root[latentId] = new JsonObject
        {
            ["class_type"] = "EmptyLatentImage",
            ["inputs"] = new JsonObject
            {
                ["width"] = config.Width,
                ["height"] = config.Height,
                ["batch_size"] = config.BatchSize
            },
            ["_meta"] = new JsonObject { ["title"] = "空Latent图像" }
        };

        string samplerId = NextNodeId();
        root[samplerId] = new JsonObject
        {
            ["class_type"] = "KSampler",
            ["inputs"] = new JsonObject
            {
                ["seed"] = seed,
                ["steps"] = config.Steps,
                ["cfg"] = config.Cfg,
                ["sampler_name"] = string.IsNullOrWhiteSpace(config.SamplerName) ? "euler_ancestral" : config.SamplerName.Trim(),
                ["scheduler"] = string.IsNullOrWhiteSpace(config.Scheduler) ? "normal" : config.Scheduler.Trim(),
                ["denoise"] = config.Denoise,
                ["model"] = modelRef,
                ["positive"] = new JsonArray(posId, 0),
                ["negative"] = new JsonArray(negId, 0),
                ["latent_image"] = new JsonArray(latentId, 0)
            },
            ["_meta"] = new JsonObject { ["title"] = "K采样器" }
        };

        string decodeId = NextNodeId();
        root[decodeId] = new JsonObject
        {
            ["class_type"] = "VAEDecode",
            ["inputs"] = new JsonObject
            {
                ["samples"] = new JsonArray(samplerId, 0),
                ["vae"] = vaeRef
            },
            ["_meta"] = new JsonObject { ["title"] = "VAE解码" }
        };

        string prefix = string.IsNullOrWhiteSpace(config.FilenamePrefix)
            ? $"{SanitizePrefix(config.Name)}/"
            : config.FilenamePrefix.Trim();
        if (!string.IsNullOrWhiteSpace(dateSuffix) && !prefix.EndsWith(dateSuffix, StringComparison.Ordinal))
            prefix += dateSuffix;

        string saveId = NextNodeId();
        root[saveId] = new JsonObject
        {
            ["class_type"] = "SaveImage",
            ["inputs"] = new JsonObject
            {
                ["filename_prefix"] = prefix,
                ["images"] = new JsonArray(decodeId, 0)
            },
            ["_meta"] = new JsonObject { ["title"] = "保存图像" }
        };

        return root;
    }

    /// <summary>
    /// 校验必填底模字段，返回空字符串表示通过。
    /// </summary>
    public static string Validate(WorkflowApiConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return "工作流名称不能为空。";

        if (config.LoaderMode == WorkflowLoaderMode.Checkpoint)
        {
            if (string.IsNullOrWhiteSpace(config.CheckpointName))
                return $"[{config.Name}] 未选择 Checkpoint 模型。";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.UnetName))
                return $"[{config.Name}] 未选择 UNet 模型。";
            if (string.IsNullOrWhiteSpace(config.ClipName))
                return $"[{config.Name}] 未选择 CLIP 模型。";
            if (string.IsNullOrWhiteSpace(config.VaeName))
                return $"[{config.Name}] 未选择 VAE 模型。";
        }

        return string.Empty;
    }

    /// <summary>
    /// 从旧版 ComfyUI API .json 文件解析并转换为 WorkflowApiConfig，方便首次平滑迁移。
    /// </summary>
    public static bool TryImportFromJsonFile(string jsonPath, out WorkflowApiConfig? config, out string error)
    {
        try
        {
            string jsonText = File.ReadAllText(jsonPath);
            string defaultName = Path.GetFileNameWithoutExtension(jsonPath);
            return TryImportFromJson(jsonText, defaultName, out config, out error);
        }
        catch (Exception ex)
        {
            config = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 从 ComfyUI API 格式 JSON 字符串解析并转换为 WorkflowApiConfig（支持从图片元数据直接导入）。
    /// </summary>
    public static bool TryImportFromJson(string jsonText, string defaultName, out WorkflowApiConfig? config, out string error)
    {
        config = null;
        error = string.Empty;

        try
        {
            var root = JsonNode.Parse(jsonText)?.AsObject();
            if (root is null)
            {
                error = "不是有效的 JSON 对象";
                return false;
            }

            string safeName = string.IsNullOrWhiteSpace(defaultName) ? "导入工作流" : defaultName.Trim();
            var cfg = new WorkflowApiConfig
            {
                Name = safeName,
                FilenamePrefix = SanitizePrefix(safeName) + "/"
            };

            JsonObject? FindByClass(string classType) =>
                root.Select(kv => kv.Value as JsonObject)
                    .FirstOrDefault(n => string.Equals(n?["class_type"]?.GetValue<string>(), classType, StringComparison.OrdinalIgnoreCase));

            var ckpt = FindByClass("CheckpointLoaderSimple");
            var unet = FindByClass("UNETLoader");

            if (unet is not null)
            {
                cfg.LoaderMode = WorkflowLoaderMode.UnetSplit;
                cfg.UnetName = ReadString(unet, "unet_name");
                cfg.WeightDtype = ReadString(unet, "weight_dtype", "default");

                var clip = FindByClass("CLIPLoader");
                if (clip is not null)
                {
                    cfg.ClipName = ReadString(clip, "clip_name");
                    cfg.ClipType = ReadString(clip, "type", "stable_diffusion");
                    cfg.ClipDevice = ReadString(clip, "device", "default");
                }

                var vae = FindByClass("VAELoader");
                if (vae is not null)
                    cfg.VaeName = ReadString(vae, "vae_name");
            }
            else if (ckpt is not null)
            {
                cfg.LoaderMode = WorkflowLoaderMode.Checkpoint;
                cfg.CheckpointName = ReadString(ckpt, "ckpt_name");
            }
            else
            {
                error = "未找到 CheckpointLoaderSimple 或 UNETLoader 节点";
                return false;
            }

            // 按模型连线顺序提取 LoRA 链
            var sampler = FindByClass("KSampler") ?? FindByClass("KSamplerAdvanced");
            if (sampler is not null)
            {
                cfg.Steps = ReadInt(sampler, "steps", 20);
                cfg.Cfg = ReadDouble(sampler, "cfg", 5.0);
                cfg.SamplerName = ReadString(sampler, "sampler_name", "euler_ancestral");
                cfg.Scheduler = ReadString(sampler, "scheduler", "normal");
                cfg.Denoise = ReadDouble(sampler, "denoise", 1.0);

                var loraChain = new List<LoraConfigItem>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                string? curNodeId = ReadLinkNodeId(sampler["inputs"]?["model"]);
                while (!string.IsNullOrEmpty(curNodeId) && visited.Add(curNodeId))
                {
                    if (root[curNodeId] is not JsonObject node) break;
                    string ct = node["class_type"]?.GetValue<string>() ?? string.Empty;
                    if (ct is "LoraLoader" or "LoraLoaderModelOnly")
                    {
                        loraChain.Add(new LoraConfigItem
                        {
                            Enabled = true,
                            LoraName = ReadString(node, "lora_name"),
                            StrengthModel = ReadDouble(node, "strength_model", 1.0),
                            StrengthClip = ct == "LoraLoader" ? ReadDouble(node, "strength_clip", 1.0) : 1.0
                        });
                    }
                    curNodeId = ReadLinkNodeId(node["inputs"]?["model"]);
                }
                loraChain.Reverse();
                cfg.Loras = loraChain;
            }

            var latent = FindByClass("EmptyLatentImage");
            if (latent is not null)
            {
                cfg.Width = ReadInt(latent, "width", 1024);
                cfg.Height = ReadInt(latent, "height", 1024);
                cfg.BatchSize = ReadInt(latent, "batch_size", 1);
            }

            var save = FindByClass("SaveImage");
            if (save is not null)
            {
                string prefix = ReadString(save, "filename_prefix");
                if (prefix.Length > 0) cfg.FilenamePrefix = prefix;
            }

            foreach (var kv in root)
            {
                if (kv.Value is not JsonObject node) continue;
                if (!string.Equals(node["class_type"]?.GetValue<string>(), "CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
                    continue;

                string title = node["_meta"]?["title"]?.GetValue<string>() ?? string.Empty;
                string text = CleanPromptFragment(ReadString(node, "text"));
                if (title.Contains("Positive", StringComparison.OrdinalIgnoreCase))
                    cfg.PositiveTemplate = text;
                else if (title.Contains("Negative", StringComparison.OrdinalIgnoreCase))
                    cfg.NegativeTemplate = text;
            }

            config = cfg;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string MergePrompt(string template, string incoming, string placeholder)
    {
        string cleanTemplate = CleanPromptFragment(template);
        string cleanIncoming = CleanPromptFragment(incoming);

        if (cleanTemplate.Length == 0) return cleanIncoming;
        if (cleanIncoming.Length == 0) return cleanTemplate;

        int index = cleanTemplate.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return JoinPromptParts(cleanTemplate, cleanIncoming);

        var sb = new StringBuilder(cleanTemplate.Length + cleanIncoming.Length);
        int start = 0;
        while (index >= 0)
        {
            sb.Append(cleanTemplate, start, index - start);
            sb.Append(cleanIncoming);
            start = index + placeholder.Length;
            index = cleanTemplate.IndexOf(placeholder, start, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(cleanTemplate, start, cleanTemplate.Length - start);
        return CleanPromptFragment(sb.ToString());
    }

    private static string CleanPromptFragment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text
            .Replace("\u200B", string.Empty)
            .Replace("\uFEFF", string.Empty)
            .Trim()
            .Trim(',', '，')
            .Trim();
    }

    private static string JoinPromptParts(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first}, {second}";
    }

    private static string SanitizePrefix(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "output";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 0 ? cleaned : "output";
    }

    private static string ReadString(JsonObject node, string key, string fallback = "")
    {
        try { return node["inputs"]?[key]?.GetValue<string>() ?? fallback; }
        catch { return fallback; }
    }

    private static int ReadInt(JsonObject node, string key, int fallback)
    {
        try { return node["inputs"]?[key]?.GetValue<int>() ?? fallback; }
        catch { return fallback; }
    }

    private static double ReadDouble(JsonObject node, string key, double fallback)
    {
        try { return node["inputs"]?[key]?.GetValue<double>() ?? fallback; }
        catch { return fallback; }
    }

    private static string? ReadLinkNodeId(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count == 0) return null;
        try { return arr[0]?.GetValue<string>(); }
        catch { return arr[0]?.ToString(); }
    }
}
