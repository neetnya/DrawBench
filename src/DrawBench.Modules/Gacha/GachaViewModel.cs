using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DrawBench.Modules.Gacha;

public sealed record ComboOption(string Value, string Label);

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>抽卡视图模型：统一管理选项绑定、下拉列表、自定义预设与输出状态。</summary>
public sealed class GachaViewModel : INotifyPropertyChanged
{
    private static readonly string[] FallbackHairLengths =
        { "short_hair", "medium_hair", "long_hair", "very_long_hair", "absurdly_long_hair" };

    private static readonly string[] FallbackBreastsSizes =
        { "small_breasts", "breasts", "large_breasts", "huge_breasts", "gigantic_breasts" };

    private static readonly Dictionary<string, string> TagChineseLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["short_hair"] = "短发",
        ["medium_hair"] = "中长发",
        ["long_hair"] = "长发",
        ["very_long_hair"] = "超长发",
        ["absurdly_long_hair"] = "极长发",
        ["small_breasts"] = "贫乳",
        ["breasts"] = "常规",
        ["large_breasts"] = "巨乳",
        ["huge_breasts"] = "爆乳",
        ["gigantic_breasts"] = "超巨乳",
    };

    private string _repeatText = "1";
    private bool _replaceUnderscoreWithSpace = true;
    private bool _includeNsfw;
    private bool _allowMultiPerson;
    private string _prefixTags = "1girl, solo";
    private string _suffixTags = "";

    private bool _hairColor = true;
    private string _selectedHairLength = GachaOptions.RandomChoice;
    private bool _hairStyles = true;
    private bool _hairAccessories;
    private bool _eyes = true;
    private bool _faceAndExpression = true;
    private string _earsMode = GachaOptions.EarsRareChoice;
    private string _selectedBreastsSize = GachaOptions.NoneChoice;
    private bool _bodyDetails;

    private string _attireMode = GachaOptions.RandomChoice;
    private bool _legwear = true;
    private bool _footwear = true;
    private bool _accessories;

    private string _actionMode = GachaOptions.RandomChoice;
    private string _sceneMode = GachaOptions.LocationsChoice;

    private GachaPreset? _selectedPreset;
    private string _presetNameText = "";
    private bool _suppressPresetApply;

    private string _resultText = "";
    private string _logText = "";
    private string _metaText = "";
    private string _statusText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ComboOption> HairLengthOptions { get; }
    public IReadOnlyList<ComboOption> BreastsSizeOptions { get; }
    public IReadOnlyList<ComboOption> AttireModeOptions { get; }
    public IReadOnlyList<ComboOption> ActionModeOptions { get; }
    public IReadOnlyList<ComboOption> SceneModeOptions { get; }
    public IReadOnlyList<ComboOption> EarsModeOptions { get; }

    public ObservableCollection<GachaPreset> Presets { get; } = new();

    public ICommand RollCommand { get; }

    public GachaViewModel(string engineDir, Action onRoll)
    {
        RollCommand = new RelayCommand(onRoll);

        HairLengthOptions = BuildTagComboOptions(engineDir, "hair", "hair_length", FallbackHairLengths);
        BreastsSizeOptions = BuildTagComboOptions(engineDir, "body_parts", "breasts_size", FallbackBreastsSizes);

        AttireModeOptions = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.RandomChoice, "随机"),
            new(GachaOptions.OutfitsChoice, "套装"),
            new(GachaOptions.TopsBottomsChoice, "上下装"),
        };

        ActionModeOptions = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.RandomChoice, "随机"),
            new(GachaOptions.PostureChoice, "全身姿势"),
            new(GachaOptions.GesturesChoice, "手势"),
        };

        SceneModeOptions = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.RandomChoice, "随机"),
            new(GachaOptions.LocationsChoice, "地点环境"),
            new(GachaOptions.BackgroundsChoice, "简单背景"),
        };

        EarsModeOptions = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.EarsRareChoice, "15% 概率"),
            new(GachaOptions.EarsAlwaysChoice, "100% 必出"),
        };
    }

    // ==================== 自定义预设 ====================

    public GachaPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value)) return;
            OnPropertyChanged(nameof(HasSelectedPreset));
            PresetNameText = value?.Name ?? "";
            if (!_suppressPresetApply && value is not null)
            {
                LoadCoreOptions(value.Options);
                StatusText = $"已应用预设「{value.Name}」";
            }
        }
    }

    public bool HasSelectedPreset => _selectedPreset is not null;

    public string PresetNameText
    {
        get => _presetNameText;
        set => SetField(ref _presetNameText, value ?? "");
    }

    // ==================== 通用设置 ====================

    public string RepeatText
    {
        get => _repeatText;
        set => SetField(ref _repeatText, value ?? "");
    }

    public bool ReplaceUnderscoreWithSpace
    {
        get => _replaceUnderscoreWithSpace;
        set => SetField(ref _replaceUnderscoreWithSpace, value);
    }

    public bool IncludeNsfw
    {
        get => _includeNsfw;
        set => SetField(ref _includeNsfw, value);
    }

    public bool AllowMultiPerson
    {
        get => _allowMultiPerson;
        set => SetField(ref _allowMultiPerson, value);
    }

    public string PrefixTags
    {
        get => _prefixTags;
        set => SetField(ref _prefixTags, value ?? "");
    }

    public string SuffixTags
    {
        get => _suffixTags;
        set => SetField(ref _suffixTags, value ?? "");
    }

    // ==================== 人物外貌 ====================

    public bool HairColor
    {
        get => _hairColor;
        set => SetField(ref _hairColor, value);
    }

    public string SelectedHairLength
    {
        get => _selectedHairLength;
        set => SetField(ref _selectedHairLength, ValidateChoice(HairLengthOptions, value));
    }

    public bool HairStyles
    {
        get => _hairStyles;
        set => SetField(ref _hairStyles, value);
    }

    public bool HairAccessories
    {
        get => _hairAccessories;
        set => SetField(ref _hairAccessories, value);
    }

    public bool Eyes
    {
        get => _eyes;
        set => SetField(ref _eyes, value);
    }

    public bool FaceAndExpression
    {
        get => _faceAndExpression;
        set => SetField(ref _faceAndExpression, value);
    }

    public string EarsMode
    {
        get => _earsMode;
        set => SetField(ref _earsMode, ValidateChoice(EarsModeOptions, value));
    }

    public string SelectedBreastsSize
    {
        get => _selectedBreastsSize;
        set => SetField(ref _selectedBreastsSize, ValidateChoice(BreastsSizeOptions, value));
    }

    public bool BodyDetails
    {
        get => _bodyDetails;
        set => SetField(ref _bodyDetails, value);
    }

    // ==================== 穿搭、动作、场景 ====================

    public string AttireMode
    {
        get => _attireMode;
        set => SetField(ref _attireMode, ValidateChoice(AttireModeOptions, value));
    }

    public bool Legwear
    {
        get => _legwear;
        set => SetField(ref _legwear, value);
    }

    public bool Footwear
    {
        get => _footwear;
        set => SetField(ref _footwear, value);
    }

    public bool Accessories
    {
        get => _accessories;
        set => SetField(ref _accessories, value);
    }

    public string ActionMode
    {
        get => _actionMode;
        set => SetField(ref _actionMode, ValidateChoice(ActionModeOptions, value));
    }

    public string SceneMode
    {
        get => _sceneMode;
        set => SetField(ref _sceneMode, ValidateChoice(SceneModeOptions, value));
    }

    // ==================== 输出区 ====================

    public string ResultText
    {
        get => _resultText;
        set
        {
            if (SetField(ref _resultText, value ?? ""))
                OnPropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => !string.IsNullOrWhiteSpace(_resultText);

    public string LogText
    {
        get => _logText;
        set => SetField(ref _logText, value ?? "");
    }

    public string MetaText
    {
        get => _metaText;
        set => SetField(ref _metaText, value ?? "");
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value ?? "");
    }

    // ==================== 配置读写与校验 ====================

    /// <summary>规范化连抽次数（1~99）并回写到 UI 绑定属性。</summary>
    public int NormalizeRepeat()
    {
        int value = int.TryParse(_repeatText.Trim(), out int parsed)
            ? Math.Clamp(parsed, 1, 99)
            : 1;
        RepeatText = value.ToString();
        return value;
    }

    public void LoadOptions(GachaOptions o)
    {
        LoadCoreOptions(o);

        Presets.Clear();
        var presetSource = o.Presets ?? CreateDefaultPresets();
        foreach (var p in presetSource)
            Presets.Add(p.Clone());

        _suppressPresetApply = true;
        try
        {
            GachaPreset? match = null;
            if (!string.IsNullOrWhiteSpace(o.SelectedPresetId))
                match = Presets.FirstOrDefault(p => string.Equals(p.Id, o.SelectedPresetId, StringComparison.OrdinalIgnoreCase));
            SelectedPreset = match ?? Presets.FirstOrDefault();
        }
        finally
        {
            _suppressPresetApply = false;
        }
    }

    private void LoadCoreOptions(GachaOptions o)
    {
        RepeatText = Math.Clamp(o.Repeat, 1, 99).ToString();
        ReplaceUnderscoreWithSpace = o.ReplaceUnderscoreWithSpace;
        IncludeNsfw = o.IncludeNsfw;
        AllowMultiPerson = o.AllowMultiPerson;
        PrefixTags = o.PrefixTags ?? "";
        SuffixTags = o.SuffixTags ?? "";

        HairColor = o.HairColor;
        SelectedHairLength = o.SelectedHairLength;
        HairStyles = o.HairStyles;
        HairAccessories = o.HairAccessories;

        Eyes = o.Eyes;
        FaceAndExpression = o.FaceAndExpression;
        EarsMode = o.EarsMode;

        SelectedBreastsSize = o.SelectedBreastsSize;
        BodyDetails = o.BodyDetails;

        AttireMode = o.AttireMode;
        Legwear = o.Legwear;
        Footwear = o.Footwear;
        Accessories = o.Accessories;

        ActionMode = o.ActionMode;
        SceneMode = o.SceneMode;
    }

    public GachaOptions ToCoreOptions() => new()
    {
        Repeat = NormalizeRepeat(),
        ReplaceUnderscoreWithSpace = ReplaceUnderscoreWithSpace,
        IncludeNsfw = IncludeNsfw,
        AllowMultiPerson = AllowMultiPerson,
        PrefixTags = PrefixTags,
        SuffixTags = SuffixTags,

        HairColor = HairColor,
        SelectedHairLength = SelectedHairLength,
        HairStyles = HairStyles,
        HairAccessories = HairAccessories,

        Eyes = Eyes,
        FaceAndExpression = FaceAndExpression,
        EarsMode = EarsMode,

        SelectedBreastsSize = SelectedBreastsSize,
        BodyDetails = BodyDetails,

        AttireMode = AttireMode,
        Legwear = Legwear,
        Footwear = Footwear,
        Accessories = Accessories,

        ActionMode = ActionMode,
        SceneMode = SceneMode,
    };

    public GachaOptions ToOptions()
    {
        var opts = ToCoreOptions();
        opts.Presets = Presets.Select(p => p.Clone()).ToList();
        opts.SelectedPresetId = SelectedPreset?.Id;
        return opts;
    }

    // ==================== 预设管理（新建 / 保存 / 删除） ====================

    /// <summary>以当前页面配置新建一个预设并选中。</summary>
    public GachaPreset CreateNewPreset()
    {
        string inputName = PresetNameText.Trim();
        bool useInputName = inputName.Length > 0
            && (SelectedPreset is null || !string.Equals(inputName, SelectedPreset.Name, StringComparison.OrdinalIgnoreCase))
            && !Presets.Any(p => string.Equals(p.Name, inputName, StringComparison.OrdinalIgnoreCase));

        string name = useInputName ? inputName : NextAvailablePresetName();
        var preset = new GachaPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Options = ToCoreOptions()
        };

        Presets.Add(preset);
        _suppressPresetApply = true;
        try
        {
            SelectedPreset = preset;
        }
        finally
        {
            _suppressPresetApply = false;
        }

        StatusText = $"已新建预设「{preset.Name}」";
        return preset;
    }

    /// <summary>将当前页面所有配置与名称保存到当前选中的预设；若当前无预设则自动新建。</summary>
    public bool SaveCurrentPreset(out string message)
    {
        string name = PresetNameText.Trim();
        if (name.Length == 0)
        {
            message = "预设名称不能为空。";
            return false;
        }

        if (SelectedPreset is null)
        {
            var created = new GachaPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Options = ToCoreOptions()
            };
            Presets.Add(created);
            _suppressPresetApply = true;
            try
            {
                SelectedPreset = created;
            }
            finally
            {
                _suppressPresetApply = false;
            }
            StatusText = $"已新建并保存预设「{created.Name}」";
            message = StatusText;
            return true;
        }

        SelectedPreset.Name = name;
        SelectedPreset.Options = ToCoreOptions();
        StatusText = $"已保存预设「{SelectedPreset.Name}」";
        message = StatusText;
        return true;
    }

    /// <summary>删除当前选中的预设。</summary>
    public void DeleteCurrentPreset()
    {
        if (SelectedPreset is null) return;
        string removedName = SelectedPreset.Name;
        int idx = Presets.IndexOf(SelectedPreset);

        _suppressPresetApply = true;
        try
        {
            Presets.RemoveAt(idx);
            SelectedPreset = Presets.Count > 0 ? Presets[Math.Min(idx, Presets.Count - 1)] : null;
        }
        finally
        {
            _suppressPresetApply = false;
        }

        StatusText = $"已删除预设「{removedName}」";
    }

    private string NextAvailablePresetName()
    {
        for (int i = 1; i < 1000; i++)
        {
            string candidate = $"预设 {i}";
            if (!Presets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return "新预设";
    }

    private static List<GachaPreset> CreateDefaultPresets() => new()
    {
        BuildPreset("常规全身", o =>
        {
            o.SelectedHairLength = GachaOptions.RandomChoice;
            o.SelectedBreastsSize = GachaOptions.NoneChoice;
            o.AttireMode = GachaOptions.RandomChoice;
            o.ActionMode = GachaOptions.RandomChoice;
            o.SceneMode = GachaOptions.LocationsChoice;
            o.EarsMode = GachaOptions.EarsRareChoice;
            o.HairColor = true;
            o.HairStyles = true;
            o.Eyes = true;
            o.FaceAndExpression = true;
            o.Legwear = true;
            o.Footwear = true;
        }),
        BuildPreset("人设外貌", o =>
        {
            o.SelectedHairLength = GachaOptions.RandomChoice;
            o.SelectedBreastsSize = GachaOptions.RandomChoice;
            o.AttireMode = GachaOptions.TopsBottomsChoice;
            o.ActionMode = GachaOptions.GesturesChoice;
            o.SceneMode = GachaOptions.BackgroundsChoice;
            o.EarsMode = GachaOptions.EarsRareChoice;
            o.HairColor = true;
            o.HairStyles = true;
            o.HairAccessories = true;
            o.Eyes = true;
            o.FaceAndExpression = true;
            o.Legwear = true;
            o.Footwear = true;
        }),
        BuildPreset("服装展示", o =>
        {
            o.SelectedHairLength = GachaOptions.NoneChoice;
            o.SelectedBreastsSize = GachaOptions.NoneChoice;
            o.AttireMode = GachaOptions.RandomChoice;
            o.ActionMode = GachaOptions.PostureChoice;
            o.SceneMode = GachaOptions.RandomChoice;
            o.EarsMode = GachaOptions.EarsRareChoice;
            o.HairColor = true;
            o.HairStyles = true;
            o.Accessories = true;
            o.Legwear = true;
            o.Footwear = true;
        }),
    };

    private static GachaPreset BuildPreset(string name, Action<GachaOptions> configure)
    {
        var opts = new GachaOptions
        {
            HairColor = false,
            SelectedHairLength = GachaOptions.NoneChoice,
            HairStyles = false,
            HairAccessories = false,
            Eyes = false,
            FaceAndExpression = false,
            EarsMode = GachaOptions.NoneChoice,
            SelectedBreastsSize = GachaOptions.NoneChoice,
            BodyDetails = false,
            AttireMode = GachaOptions.NoneChoice,
            Accessories = false,
            Legwear = false,
            Footwear = false,
            ActionMode = GachaOptions.NoneChoice,
            SceneMode = GachaOptions.NoneChoice,
        };
        configure(opts);
        return new GachaPreset { Name = name, Options = opts };
    }

    public void ClearSelections()
    {
        var opts = new GachaOptions
        {
            Repeat = NormalizeRepeat(),
            ReplaceUnderscoreWithSpace = ReplaceUnderscoreWithSpace,
            IncludeNsfw = IncludeNsfw,
            AllowMultiPerson = AllowMultiPerson,
            PrefixTags = PrefixTags,
            SuffixTags = SuffixTags,
            HairColor = false,
            SelectedHairLength = GachaOptions.NoneChoice,
            HairStyles = false,
            HairAccessories = false,
            Eyes = false,
            FaceAndExpression = false,
            EarsMode = GachaOptions.NoneChoice,
            SelectedBreastsSize = GachaOptions.NoneChoice,
            BodyDetails = false,
            AttireMode = GachaOptions.NoneChoice,
            Accessories = false,
            Legwear = false,
            Footwear = false,
            ActionMode = GachaOptions.NoneChoice,
            SceneMode = GachaOptions.NoneChoice,
        };
        LoadCoreOptions(opts);
    }

    public void ClearOutput()
    {
        ResultText = "";
        LogText = "";
        MetaText = "";
        StatusText = "已清空";
    }

    private static List<ComboOption> BuildTagComboOptions(string engineDir, string module, string subCategory, string[] fallback)
    {
        var tags = GachaEngine.GetSlotSfwTags(engineDir, module, subCategory);
        IEnumerable<string> source = tags.Count > 0 ? tags : fallback;

        var items = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.RandomChoice, "随机"),
        };
        foreach (string tag in source)
        {
            string label = TagChineseLabels.TryGetValue(tag, out string? zh) ? zh : tag;
            items.Add(new ComboOption(tag, label));
        }
        return items;
    }

    private static string ValidateChoice(IReadOnlyList<ComboOption> options, string? value)
    {
        string target = value ?? GachaOptions.NoneChoice;
        foreach (var opt in options)
        {
            if (string.Equals(opt.Value, target, StringComparison.Ordinal))
                return opt.Value;
        }
        return GachaOptions.NoneChoice;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
