using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DrawBench.Core.Comfy;

namespace DrawBench.Modules.Batch.Services;

/// <summary>从 ComfyUI /object_info 拉取的下拉候选集。</summary>
public sealed class ComfyOptionsCatalog
{
    public List<string> Checkpoints { get; init; } = new();
    public List<string> Unets { get; init; } = new();
    public List<string> WeightDtypes { get; init; } = new() { "default", "fp8_e4m3fn", "fp8_e4m3fn_fast", "fp8_e5m2" };
    public List<string> Clips { get; init; } = new();
    public List<string> ClipTypes { get; init; } = new() { "stable_diffusion", "stable_cascade", "sd3", "stable_audio", "mochi", "ltxv", "pixart", "cosmos", "lumina2", "wan", "hidream", "chroma", "ace", "omnigen2", "qwen_image", "flux" };
    public List<string> Vaes { get; init; } = new();
    public List<string> Loras { get; init; } = new();
    public List<string> Samplers { get; init; } = new() { "euler", "euler_ancestral", "heun", "dpm_2", "dpm_2_ancestral", "lms", "dpmpp_2s_ancestral", "dpmpp_sde", "dpmpp_2m", "dpmpp_2m_sde", "dpmpp_3m_sde", "er_sde", "uni_pc", "res_multistep" };
    public List<string> Schedulers { get; init; } = new() { "normal", "karras", "exponential", "sgm_uniform", "simple", "ddim_uniform", "beta" };
}

/// <summary>
/// 轻量拉取 ComfyUI 的 /object_info 节点元数据，用于填充配置窗口的所有下拉框。
/// 独立于 DrawBench.Core，无需改动 ComfyClient 即可使用。
/// </summary>
public static class ComfyObjectInfoService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<(bool Ok, ComfyOptionsCatalog Catalog, string Message)> FetchAsync(
        string baseUrl,
        CancellationToken ct = default)
    {
        string normalized = ComfyClient.Normalize(baseUrl).TrimEnd('/');
        var fallback = new ComfyOptionsCatalog();

        try
        {
            // 按节点分别请求比拉全量 /object_info 更快、体积更小
            var ckptTask = FetchNodeOptionsAsync(normalized, "CheckpointLoaderSimple", "ckpt_name", ct);
            var unetTask = FetchNodeOptionsAsync(normalized, "UNETLoader", "unet_name", ct);
            var dtypeTask = FetchNodeOptionsAsync(normalized, "UNETLoader", "weight_dtype", ct);
            var clipTask = FetchNodeOptionsAsync(normalized, "CLIPLoader", "clip_name", ct);
            var clipTypeTask = FetchNodeOptionsAsync(normalized, "CLIPLoader", "type", ct);
            var vaeTask = FetchNodeOptionsAsync(normalized, "VAELoader", "vae_name", ct);
            var loraTask = FetchNodeOptionsAsync(normalized, "LoraLoader", "lora_name", ct);
            var samplerTask = FetchNodeOptionsAsync(normalized, "KSampler", "sampler_name", ct);
            var schedulerTask = FetchNodeOptionsAsync(normalized, "KSampler", "scheduler", ct);

            await Task.WhenAll(ckptTask, unetTask, dtypeTask, clipTask, clipTypeTask, vaeTask, loraTask, samplerTask, schedulerTask);

            var catalog = new ComfyOptionsCatalog
            {
                Checkpoints = ckptTask.Result,
                Unets = unetTask.Result,
                WeightDtypes = dtypeTask.Result.Count > 0 ? dtypeTask.Result : fallback.WeightDtypes,
                Clips = clipTask.Result,
                ClipTypes = clipTypeTask.Result.Count > 0 ? clipTypeTask.Result : fallback.ClipTypes,
                Vaes = vaeTask.Result,
                Loras = loraTask.Result,
                Samplers = samplerTask.Result.Count > 0 ? samplerTask.Result : fallback.Samplers,
                Schedulers = schedulerTask.Result.Count > 0 ? schedulerTask.Result : fallback.Schedulers
            };

            int total = catalog.Checkpoints.Count + catalog.Unets.Count + catalog.Loras.Count;
            if (total == 0 && samplerTask.Result.Count == 0)
                return (false, fallback, $"未能从 {normalized} 读取模型列表，请确认 ComfyUI 已启动。");

            return (true, catalog,
                $"已同步：Checkpoint {catalog.Checkpoints.Count} · UNet {catalog.Unets.Count} · LoRA {catalog.Loras.Count}");
        }
        catch (Exception ex)
        {
            return (false, fallback, $"连接 ComfyUI 失败（可手动输入参数）：{ex.Message}");
        }
    }

    private static async Task<List<string>> FetchNodeOptionsAsync(
        string baseUrl,
        string nodeClass,
        string inputField,
        CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"{baseUrl}/object_info/{nodeClass}", ct);
            if (!resp.IsSuccessStatusCode) return new List<string>();

            string json = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(json)?.AsObject();
            var fieldSpec = root?[nodeClass]?["input"]?["required"]?[inputField] as JsonArray;
            if (fieldSpec is null || fieldSpec.Count == 0) return new List<string>();

            // ComfyUI 新版（V3）可能是 ["COMBO", { "options": [...] }]，旧版是 [[...]]
            JsonArray? optionsArr = fieldSpec[0] as JsonArray;
            if (optionsArr is null && fieldSpec.Count >= 2 && fieldSpec[1] is JsonObject meta)
                optionsArr = meta["options"] as JsonArray;

            if (optionsArr is null) return new List<string>();

            return optionsArr
                .Select(n => n?.GetValue<string>() ?? string.Empty)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
