using System.Globalization;

namespace DrawBench.Modules.Metadata.Models;

/// <summary>
/// 一张图片里解析出来的全部生成参数（与具体生成工具无关的中间模型）。
/// </summary>
public sealed class GenerationParameters
{
    /// <summary>生成工具：ComfyUI / Stable Diffusion WebUI / NovelAI / 未知。</summary>
    public string Source { get; set; } = "";

    public string PositivePrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";

    public long? Seed { get; set; }
    public int? Steps { get; set; }
    public string? Sampler { get; set; }
    public string? Scheduler { get; set; }
    public double? CfgScale { get; set; }
    public double? Denoise { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    public string? Model { get; set; }
    public string? ModelHash { get; set; }
    public string? Vae { get; set; }
    public string? ClipSkip { get; set; }

    /// <summary>额外键值（LoRA、扩图、Hires、工作流节点里的杂项等）。</summary>
    public Dictionary<string, string> Extra { get; } = new();

    /// <summary>原始元数据文本，供“原始数据”面板显示。</summary>
    public string RawText { get; set; } = "";

    /// <summary>ComfyUI 的 workflow（界面图）原文，可能为空。</summary>
    public string? WorkflowJson { get; set; }

    public bool HasAnyData =>
        !string.IsNullOrWhiteSpace(PositivePrompt) ||
        !string.IsNullOrWhiteSpace(NegativePrompt) ||
        Seed.HasValue || Steps.HasValue || Width.HasValue;

    public string ResolutionText => Width.HasValue && Height.HasValue ? $"{Width} × {Height}" : "";

    /// <summary>给 DataGrid 用的展示键值对。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ParameterItems
    {
        get
        {
            var list = new List<KeyValuePair<string, string>>();

            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    list.Add(new KeyValuePair<string, string>(key, value));
            }

            Add("生成工具", Source);
            Add("模型", Model);
            Add("模型哈希", ModelHash);
            Add("VAE", Vae);
            Add("采样器", Sampler);
            Add("调度器", Scheduler);
            Add("步数", Steps?.ToString(CultureInfo.InvariantCulture));
            Add("CFG", CfgScale?.ToString("0.##", CultureInfo.InvariantCulture));
            Add("种子", Seed?.ToString(CultureInfo.InvariantCulture));
            Add("尺寸", ResolutionText);
            Add("降噪强度", Denoise?.ToString("0.##", CultureInfo.InvariantCulture));
            Add("Clip Skip", ClipSkip);

            foreach (var kv in Extra)
                Add(kv.Key, kv.Value);

            if (list.Count == 0)
                list.Add(new KeyValuePair<string, string>("提示", "未在文件里找到可解析的参数"));

            return list;
        }
    }
}
