using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DrawBench.Modules.Batch.Models;

/// <summary>底模加载方式：单 Checkpoint（SDXL/Illustrious）或分离式 UNet+CLIP+VAE（Anima/Flux）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowLoaderMode
{
    Checkpoint,
    UnetSplit
}

/// <summary>
/// 全局 LoRA 配置项：按 ComfyUI 相对路径唯一标识，集中保存别名、默认权重与全局触发词。
/// 所有工作流共享同一份 LoRA 元数据；支持改名/重指向时一键同步到所有工作流。
/// </summary>
public sealed class LoraProfile : INotifyPropertyChanged
{
    private string _loraName = string.Empty;
    private string _alias = string.Empty;
    private string _triggerWords = string.Empty;
    private double _defaultStrengthModel = 1.0;
    private double _defaultStrengthClip = 1.0;
    private string _statusBadge = string.Empty;

    public string LoraName
    {
        get => _loraName;
        set
        {
            if (SetField(ref _loraName, value ?? string.Empty))
                OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    /// <summary>可选别名（如「油皮质感」），下拉搜索时同时匹配别名和文件路径。</summary>
    public string Alias
    {
        get => _alias;
        set
        {
            if (SetField(ref _alias, value ?? string.Empty))
                OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    /// <summary>全局触发词，所有引用此 LoRA 的工作流自动生效。</summary>
    public string TriggerWords
    {
        get => _triggerWords;
        set => SetField(ref _triggerWords, value ?? string.Empty);
    }

    public double DefaultStrengthModel
    {
        get => _defaultStrengthModel;
        set => SetField(ref _defaultStrengthModel, value);
    }

    public double DefaultStrengthClip
    {
        get => _defaultStrengthClip;
        set => SetField(ref _defaultStrengthClip, value);
    }

    [JsonIgnore]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Alias)
            ? (string.IsNullOrWhiteSpace(LoraName) ? "(未选择 LoRA)" : LoraName)
            : $"{Alias}  ({System.IO.Path.GetFileNameWithoutExtension(LoraName)})";

    /// <summary>界面展示用的引用数与文件存在状态摘要。</summary>
    [JsonIgnore]
    public string StatusBadge
    {
        get => _statusBadge;
        set => SetField(ref _statusBadge, value ?? string.Empty);
    }

    public LoraProfile Clone() => new()
    {
        LoraName = LoraName,
        Alias = Alias,
        TriggerWords = TriggerWords,
        DefaultStrengthModel = DefaultStrengthModel,
        DefaultStrengthClip = DefaultStrengthClip
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>单条 LoRA 挂载项（仅保存引用与权重；触发词按 LoraName 存在全局 LoraProfiles 中）。</summary>
public sealed class LoraConfigItem : INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _loraName = string.Empty;
    private double _strengthModel = 1.0;
    private double _strengthClip = 1.0;
    private string _triggerWords = string.Empty;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string LoraName
    {
        get => _loraName;
        set
        {
            if (SetField(ref _loraName, value ?? string.Empty))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoraSelected)));
        }
    }

    public double StrengthModel
    {
        get => _strengthModel;
        set => SetField(ref _strengthModel, value);
    }

    /// <summary>仅 Checkpoint 模式（LoraLoader）生效；UNet 分离模式使用 LoraLoaderModelOnly，自动忽略此值。</summary>
    public double StrengthClip
    {
        get => _strengthClip;
        set => SetField(ref _strengthClip, value);
    }

    /// <summary>界面编辑用的触发词视图属性（不随单个工作流落盘，读写同步到全局 LoraProfiles）。</summary>
    [JsonIgnore]
    public string TriggerWords
    {
        get => _triggerWords;
        set => SetField(ref _triggerWords, value ?? string.Empty);
    }

    [JsonIgnore]
    public bool HasLoraSelected => !string.IsNullOrWhiteSpace(_loraName);

    public LoraConfigItem Clone() => new()
    {
        Enabled = Enabled,
        LoraName = LoraName,
        StrengthModel = StrengthModel,
        StrengthClip = StrengthClip,
        TriggerWords = TriggerWords
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>
/// 一套可视化的 ComfyUI 工作流 API 配置，替代旧版手写/手改 .json 文件。
/// 提交时由 WorkflowApiBuilder 实时拼装成标准 ComfyUI API JSON。
/// </summary>
public sealed class WorkflowApiConfig : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "新工作流";
    private string _filenamePrefix = "output/";
    private WorkflowLoaderMode _loaderMode = WorkflowLoaderMode.Checkpoint;

    // Checkpoint 模式
    private string _checkpointName = string.Empty;

    // UNet 分离模式
    private string _unetName = string.Empty;
    private string _weightDtype = "default";
    private string _clipName = string.Empty;
    private string _clipType = "stable_diffusion";
    private string _clipDevice = "default";
    private string _vaeName = string.Empty;

    // 采样与尺寸
    private int _width = 1024;
    private int _height = 1024;
    private int _batchSize = 1;
    private int _steps = 20;
    private double _cfg = 5.0;
    private string _samplerName = "euler_ancestral";
    private string _scheduler = "karras";
    private double _denoise = 1.0;

    // 提示词模板
    private string _positiveTemplate = "masterpiece, best quality";
    private string _negativeTemplate = "bad quality, worst quality";

    public string Id
    {
        get => _id;
        set => SetField(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public string FilenamePrefix
    {
        get => _filenamePrefix;
        set => SetField(ref _filenamePrefix, value ?? string.Empty);
    }

    public WorkflowLoaderMode LoaderMode
    {
        get => _loaderMode;
        set
        {
            if (SetField(ref _loaderMode, value))
            {
                OnPropertyChanged(nameof(IsCheckpointMode));
                OnPropertyChanged(nameof(IsUnetSplitMode));
            }
        }
    }

    [JsonIgnore]
    public bool IsCheckpointMode => LoaderMode == WorkflowLoaderMode.Checkpoint;

    [JsonIgnore]
    public bool IsUnetSplitMode => LoaderMode == WorkflowLoaderMode.UnetSplit;

    public string CheckpointName
    {
        get => _checkpointName;
        set => SetField(ref _checkpointName, value ?? string.Empty);
    }

    public string UnetName
    {
        get => _unetName;
        set => SetField(ref _unetName, value ?? string.Empty);
    }

    public string WeightDtype
    {
        get => _weightDtype;
        set => SetField(ref _weightDtype, string.IsNullOrWhiteSpace(value) ? "default" : value);
    }

    public string ClipName
    {
        get => _clipName;
        set => SetField(ref _clipName, value ?? string.Empty);
    }

    public string ClipType
    {
        get => _clipType;
        set => SetField(ref _clipType, string.IsNullOrWhiteSpace(value) ? "stable_diffusion" : value);
    }

    public string ClipDevice
    {
        get => _clipDevice;
        set => SetField(ref _clipDevice, string.IsNullOrWhiteSpace(value) ? "default" : value);
    }

    public string VaeName
    {
        get => _vaeName;
        set => SetField(ref _vaeName, value ?? string.Empty);
    }

    public int Width
    {
        get => _width;
        set => SetField(ref _width, value > 0 ? value : 1024);
    }

    public int Height
    {
        get => _height;
        set => SetField(ref _height, value > 0 ? value : 1024);
    }

    public int BatchSize
    {
        get => _batchSize;
        set => SetField(ref _batchSize, value > 0 ? value : 1);
    }

    public int Steps
    {
        get => _steps;
        set => SetField(ref _steps, value > 0 ? value : 20);
    }

    public double Cfg
    {
        get => _cfg;
        set => SetField(ref _cfg, value >= 0 ? value : 1.0);
    }

    public string SamplerName
    {
        get => _samplerName;
        set => SetField(ref _samplerName, value ?? string.Empty);
    }

    public string Scheduler
    {
        get => _scheduler;
        set => SetField(ref _scheduler, value ?? string.Empty);
    }

    public double Denoise
    {
        get => _denoise;
        set => SetField(ref _denoise, value);
    }

    public string PositiveTemplate
    {
        get => _positiveTemplate;
        set => SetField(ref _positiveTemplate, value ?? string.Empty);
    }

    public string NegativeTemplate
    {
        get => _negativeTemplate;
        set => SetField(ref _negativeTemplate, value ?? string.Empty);
    }

    /// <summary>挂载的 LoRA 列表（按顺序串联，支持 0 到多个，如加速 LoRA + 风格 LoRA）。</summary>
    public List<LoraConfigItem> Loras { get; set; } = new();

    public WorkflowApiConfig Clone(bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        FilenamePrefix = FilenamePrefix,
        LoaderMode = LoaderMode,
        CheckpointName = CheckpointName,
        UnetName = UnetName,
        WeightDtype = WeightDtype,
        ClipName = ClipName,
        ClipType = ClipType,
        ClipDevice = ClipDevice,
        VaeName = VaeName,
        Width = Width,
        Height = Height,
        BatchSize = BatchSize,
        Steps = Steps,
        Cfg = Cfg,
        SamplerName = SamplerName,
        Scheduler = Scheduler,
        Denoise = Denoise,
        PositiveTemplate = PositiveTemplate,
        NegativeTemplate = NegativeTemplate,
        Loras = Loras.Select(l => l.Clone()).ToList()
    };

    /// <summary>供主列表 Tooltip 显示的摘要。</summary>
    public string BuildSummary()
    {
        string model = LoaderMode == WorkflowLoaderMode.Checkpoint
            ? $"Checkpoint: {(string.IsNullOrWhiteSpace(CheckpointName) ? "(未设置)" : CheckpointName)}"
            : $"UNet: {(string.IsNullOrWhiteSpace(UnetName) ? "(未设置)" : UnetName)}";

        var activeLoras = Loras.Where(l => l.Enabled && !string.IsNullOrWhiteSpace(l.LoraName)).ToList();
        string loraLine = activeLoras.Count == 0
            ? "LoRA: 无"
            : "LoRA: " + string.Join(" + ", activeLoras.Select(l => $"{l.LoraName} ({l.StrengthModel:0.##})"));

        return $"{model}\n{loraLine}\n{Width}×{Height} · Steps {Steps} · CFG {Cfg:0.##} · {SamplerName}/{Scheduler}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
