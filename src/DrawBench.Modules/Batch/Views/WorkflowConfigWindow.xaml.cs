using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DrawBench.Modules.Batch.Models;
using DrawBench.Modules.Batch.Services;
using Microsoft.Win32;

namespace DrawBench.Modules.Batch.Views;

/// <summary>
/// 工作流 API 与全局 LoRA 可视化配置窗口：
/// - 页签 1「工作流 API」：下拉+输入框配置底模、LoRA 挂载、采样器与模板提示词
/// - 页签 2「全局 LoRA 管理」：集中管理所有 LoRA 的别名、默认权重、全局触发词、引用统计与改名迁移
/// </summary>
public partial class WorkflowConfigWindow : Window, INotifyPropertyChanged
{
    private readonly string _comfyBaseUrl;
    private readonly ObservableCollection<WorkflowApiConfig> _items = new();
    private readonly ObservableCollection<LoraConfigItem> _currentLoras = new();
    private readonly List<LoraProfile> _globalLoras = new();
    private readonly ObservableCollection<LoraProfile> _filteredGlobalLoras = new();

    private HashSet<string> _comfyLoraSet = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _allLoraOptions = new();
    private ComfyOptionsCatalog _lastCatalog = new();
    private WorkflowApiConfig? _current;
    private LoraProfile? _currentGlobalLora;
    private bool _syncingModeCombo;
    private bool _syncingLoraTrigger;
    private IReadOnlyDictionary<string, string> _loraAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public WorkflowConfigWindow(
        IEnumerable<WorkflowApiConfig> initialWorkflows,
        IEnumerable<LoraProfile>? initialLoraProfiles,
        string comfyBaseUrl)
    {
        _comfyBaseUrl = comfyBaseUrl;

        InitializeComponent();

        foreach (var wf in initialWorkflows)
            _items.Add(wf.Clone(newId: false));

        if (initialLoraProfiles is not null)
        {
            foreach (var p in initialLoraProfiles)
            {
                if (!string.IsNullOrWhiteSpace(p.LoraName))
                    _globalLoras.Add(p.Clone());
            }
        }

        // 确保工作流里用到的 LoRA 都在全局库中存在记录
        foreach (var lora in _items.SelectMany(w => w.Loras))
        {
            if (!string.IsNullOrWhiteSpace(lora.LoraName))
                EnsureGlobalProfile(lora.LoraName, lora.StrengthModel, lora.StrengthClip);
        }

        foreach (var p in _globalLoras)
            p.PropertyChanged += GlobalProfile_PropertyChanged;

        LstConfigs.ItemsSource = _items;
        LstLoras.ItemsSource = _currentLoras;
        LstGlobalLoras.ItemsSource = _filteredGlobalLoras;

        RebuildAliasMap();
        ApplyCatalog(_lastCatalog);
        RefreshGlobalLoraList();

        Loaded += OnLoaded;
    }

    /// <summary>点击「保存并关闭」后返回的工作流列表。</summary>
    public List<WorkflowApiConfig> ResultWorkflows { get; private set; } = new();

    /// <summary>点击「保存并关闭」后返回的全局 LoRA 库。</summary>
    public List<LoraProfile> ResultLoraProfiles { get; private set; } = new();

    // 所有下拉框的稳定数据源集合
    public ObservableCollection<string> CheckpointOptions { get; } = new();
    public ObservableCollection<string> UnetOptions { get; } = new();
    public ObservableCollection<string> WeightDtypeOptions { get; } = new();
    public ObservableCollection<string> ClipOptions { get; } = new();
    public ObservableCollection<string> ClipTypeOptions { get; } = new();
    public ObservableCollection<string> VaeOptions { get; } = new();
    public ObservableCollection<string> FilteredLoraOptions { get; } = new();
    public ObservableCollection<string> AllComfyLoraOptions { get; } = new();
    public ObservableCollection<string> SamplerOptions { get; } = new();
    public ObservableCollection<string> SchedulerOptions { get; } = new();

    public IReadOnlyDictionary<string, string> LoraAliasMap
    {
        get => _loraAliasMap;
        private set
        {
            _loraAliasMap = value;
            OnPropertyChanged();
        }
    }

    /// <summary>当前选中工作流是否为 Checkpoint 模式（决定 CLIP 权重输入框是否可用）。</summary>
    public bool IsCurrentCheckpointMode => _current?.LoaderMode == WorkflowLoaderMode.Checkpoint;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (_items.Count > 0)
            LstConfigs.SelectedIndex = 0;
        else
            ShowEditor(null);

        if (_filteredGlobalLoras.Count > 0)
            LstGlobalLoras.SelectedIndex = 0;
        else
            ShowGlobalLoraEditor(null);

        await RefreshCatalogAsync();
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs)) return;

        FlushCurrentLorasBack();
        if (MainTabs.SelectedIndex == 1)
        {
            RefreshGlobalLoraStatuses();
            RefreshGlobalLoraList(_currentGlobalLora);
            UpdateCurrentGlobalLoraUsage();
        }
        else if (MainTabs.SelectedIndex == 0)
        {
            // 从全局 LoRA 页切回工作流页时，同步最新触发词与别名
            RebuildAliasMap();
            foreach (var item in _currentLoras)
                LoadTriggerIntoItem(item);
        }
    }

    // ==================== ComfyUI 下拉项拉取 ====================

    private async void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCatalogAsync();
    }

    private async Task RefreshCatalogAsync()
    {
        BtnRefreshCatalog.IsEnabled = false;
        TxtCatalogStatus.Text = $"正在从 {_comfyBaseUrl} 同步模型与 LoRA 列表…";
        try
        {
            var (ok, catalog, message) = await ComfyObjectInfoService.FetchAsync(_comfyBaseUrl);
            _lastCatalog = catalog;
            _comfyLoraSet = new HashSet<string>(catalog.Loras, StringComparer.OrdinalIgnoreCase);
            ApplyCatalog(catalog);
            RefreshGlobalLoraStatuses();
            TxtCatalogStatus.Text = message;
        }
        finally
        {
            BtnRefreshCatalog.IsEnabled = true;
        }
    }

    private void ApplyCatalog(ComfyOptionsCatalog catalog)
    {
        var snapshot = _current?.Clone(newId: false);
        var loraSnapshot = _currentLoras.Select(l => l.LoraName).ToList();

        SyncCollection(CheckpointOptions, catalog.Checkpoints, _items.Select(w => w.CheckpointName));
        SyncCollection(UnetOptions, catalog.Unets, _items.Select(w => w.UnetName));
        SyncCollection(WeightDtypeOptions, catalog.WeightDtypes, _items.Select(w => w.WeightDtype));
        SyncCollection(ClipOptions, catalog.Clips, _items.Select(w => w.ClipName));
        SyncCollection(ClipTypeOptions, catalog.ClipTypes, _items.Select(w => w.ClipType));
        SyncCollection(VaeOptions, catalog.Vaes, _items.Select(w => w.VaeName));
        SyncCollection(SamplerOptions, catalog.Samplers, _items.Select(w => w.SamplerName));
        SyncCollection(SchedulerOptions, catalog.Schedulers, _items.Select(w => w.Scheduler));

        _allLoraOptions = catalog.Loras
            .Concat(_globalLoras.Select(p => p.LoraName))
            .Concat(_items.SelectMany(w => w.Loras).Select(l => l.LoraName))
            .Concat(loraSnapshot)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SyncCollection(AllComfyLoraOptions, catalog.Loras, Enumerable.Empty<string>());
        RebuildFilteredLoraOptions();

        if (_current is not null && snapshot is not null)
        {
            _current.CheckpointName = snapshot.CheckpointName;
            _current.UnetName = snapshot.UnetName;
            _current.WeightDtype = snapshot.WeightDtype;
            _current.ClipName = snapshot.ClipName;
            _current.ClipType = snapshot.ClipType;
            _current.VaeName = snapshot.VaeName;
            _current.SamplerName = snapshot.SamplerName;
            _current.Scheduler = snapshot.Scheduler;

            for (int i = 0; i < _currentLoras.Count && i < loraSnapshot.Count; i++)
            {
                _currentLoras[i].LoraName = loraSnapshot[i];
                LoadTriggerIntoItem(_currentLoras[i]);
            }
        }
    }

    private void ChkAutoFilterLora_Changed(object sender, RoutedEventArgs e)
    {
        RebuildFilteredLoraOptions();
    }

    /// <summary>
    /// 按当前工作流的底模架构自动过滤 LoRA 列表：
    /// - Checkpoint 模式优先筛选路径含 illustrious 的项（否则排除含 anima 的项）
    /// - UNet 分离模式优先筛选路径含 anima 的项（否则排除含 illustrious 的项）
    /// </summary>
    private void RebuildFilteredLoraOptions()
    {
        if (ChkAutoFilterLora is null) return;

        bool autoFilter = ChkAutoFilterLora.IsChecked == true && _current is not null;
        IEnumerable<string> source = _allLoraOptions;

        if (autoFilter && _allLoraOptions.Count > 0)
        {
            if (_current!.LoaderMode == WorkflowLoaderMode.Checkpoint)
            {
                var ilMatches = _allLoraOptions
                    .Where(s => s.Contains("illustrious", StringComparison.OrdinalIgnoreCase)
                             && !s.Contains("anima", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                source = ilMatches.Count > 0
                    ? ilMatches
                    : _allLoraOptions.Where(s => !s.Contains("anima", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var animaMatches = _allLoraOptions
                    .Where(s => s.Contains("anima", StringComparison.OrdinalIgnoreCase)
                             && !s.Contains("illustrious", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                source = animaMatches.Count > 0
                    ? animaMatches
                    : _allLoraOptions.Where(s => !s.Contains("illustrious", StringComparison.OrdinalIgnoreCase));
            }
        }

        var result = source.ToList();
        FilteredLoraOptions.Clear();
        foreach (var item in result)
            FilteredLoraOptions.Add(item);
    }

    private static void SyncCollection(
        ObservableCollection<string> target,
        IEnumerable<string> fetched,
        IEnumerable<string> existingValues)
    {
        var merged = fetched
            .Concat(existingValues)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        target.Clear();
        foreach (var item in merged)
            target.Add(item);
    }

    // ==================== 左侧工作流 CRUD ====================

    private void LstConfigs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlushCurrentLorasBack();
        ShowEditor(LstConfigs.SelectedItem as WorkflowApiConfig);
    }

    private void ShowEditor(WorkflowApiConfig? cfg)
    {
        DetachCurrentLoraHandlers();

        _current = cfg;
        PnlEditor.DataContext = cfg;

        if (cfg is null)
        {
            EditorScroll.Visibility = Visibility.Collapsed;
            TxtEmptyHint.Visibility = Visibility.Visible;
            _currentLoras.Clear();
            OnPropertyChanged(nameof(IsCurrentCheckpointMode));
            return;
        }

        TxtEmptyHint.Visibility = Visibility.Collapsed;
        EditorScroll.Visibility = Visibility.Visible;

        _syncingModeCombo = true;
        try
        {
            CmbLoaderMode.SelectedIndex = cfg.LoaderMode == WorkflowLoaderMode.Checkpoint ? 0 : 1;
        }
        finally
        {
            _syncingModeCombo = false;
        }

        UpdateLoaderPanels(cfg.LoaderMode);
        RebuildFilteredLoraOptions();

        _currentLoras.Clear();
        foreach (var lora in cfg.Loras)
        {
            LoadTriggerIntoItem(lora);
            lora.PropertyChanged += LoraItem_PropertyChanged;
            _currentLoras.Add(lora);
        }
        UpdateNoLoraHint();
        OnPropertyChanged(nameof(IsCurrentCheckpointMode));
    }

    private void DetachCurrentLoraHandlers()
    {
        foreach (var lora in _currentLoras)
            lora.PropertyChanged -= LoraItem_PropertyChanged;
    }

    private void FlushCurrentLorasBack()
    {
        if (_current is null) return;
        _current.Loras = _currentLoras.ToList();
    }

    private void CmbLoaderMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingModeCombo || _current is null) return;
        if (CmbLoaderMode.SelectedItem is not ComboBoxItem item) return;

        var mode = string.Equals(item.Tag?.ToString(), "UnetSplit", StringComparison.Ordinal)
            ? WorkflowLoaderMode.UnetSplit
            : WorkflowLoaderMode.Checkpoint;

        _current.LoaderMode = mode;
        UpdateLoaderPanels(mode);
        RebuildFilteredLoraOptions();
        OnPropertyChanged(nameof(IsCurrentCheckpointMode));
    }

    private void UpdateLoaderPanels(WorkflowLoaderMode mode)
    {
        bool isCkpt = mode == WorkflowLoaderMode.Checkpoint;
        PnlCheckpoint.Visibility = isCkpt ? Visibility.Visible : Visibility.Collapsed;
        PnlUnetSplit.Visibility = isCkpt ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnAddWorkflow_Click(object sender, RoutedEventArgs e)
    {
        FlushCurrentLorasBack();
        var cfg = new WorkflowApiConfig
        {
            Name = $"新工作流 {_items.Count + 1}",
            FilenamePrefix = $"wf{_items.Count + 1}/",
            CheckpointName = CheckpointOptions.FirstOrDefault() ?? string.Empty,
            UnetName = UnetOptions.FirstOrDefault() ?? string.Empty,
            ClipName = ClipOptions.FirstOrDefault() ?? string.Empty,
            VaeName = VaeOptions.FirstOrDefault() ?? string.Empty
        };
        _items.Add(cfg);
        LstConfigs.SelectedItem = cfg;
    }

    private void BtnDuplicateWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        FlushCurrentLorasBack();

        var copy = _current.Clone(newId: true);
        copy.Name = _current.Name + " - 副本";
        copy.FilenamePrefix = _current.FilenamePrefix.TrimEnd('/') + "_copy/";

        int idx = _items.IndexOf(_current);
        _items.Insert(idx + 1, copy);
        LstConfigs.SelectedItem = copy;
    }

    private void BtnDeleteWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (!ConfirmDialog.ConfirmDelete(this, $"确定删除工作流「{_current.Name}」吗？", "删除工作流"))
            return;

        int idx = _items.IndexOf(_current);
        _current = null;
        _items.RemoveAt(idx);

        if (_items.Count > 0)
            LstConfigs.SelectedIndex = Math.Min(idx, _items.Count - 1);
        else
            ShowEditor(null);
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        int idx = _items.IndexOf(_current);
        if (idx <= 0) return;

        var target = _current;
        _items.Move(idx, idx - 1);
        LstConfigs.SelectedItem = target;
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        int idx = _items.IndexOf(_current);
        if (idx < 0 || idx >= _items.Count - 1) return;

        var target = _current;
        _items.Move(idx, idx + 1);
        LstConfigs.SelectedItem = target;
    }

    // ==================== 工作流内 LoRA 行操作 ====================

    private void BtnAddLora_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        string defaultLora = FilteredLoraOptions.FirstOrDefault() ?? string.Empty;
        var profile = FindGlobalProfile(defaultLora);

        var item = new LoraConfigItem
        {
            Enabled = true,
            LoraName = defaultLora,
            StrengthModel = profile?.DefaultStrengthModel ?? 1.0,
            StrengthClip = profile?.DefaultStrengthClip ?? 1.0
        };
        if (!string.IsNullOrWhiteSpace(defaultLora))
            EnsureGlobalProfile(defaultLora, item.StrengthModel, item.StrengthClip);

        LoadTriggerIntoItem(item);
        item.PropertyChanged += LoraItem_PropertyChanged;
        _currentLoras.Add(item);
        FlushCurrentLorasBack();
        UpdateNoLoraHint();
    }

    private void BtnRemoveLora_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LoraConfigItem item)
        {
            item.PropertyChanged -= LoraItem_PropertyChanged;
            _currentLoras.Remove(item);
            FlushCurrentLorasBack();
            UpdateNoLoraHint();
        }
    }

    private void LoraItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LoraConfigItem item) return;

        if (e.PropertyName == nameof(LoraConfigItem.LoraName))
        {
            string key = item.LoraName.Trim();
            if (key.Length > 0)
            {
                var profile = FindGlobalProfile(key);
                if (profile is not null)
                {
                    item.StrengthModel = profile.DefaultStrengthModel;
                    item.StrengthClip = profile.DefaultStrengthClip;
                }
                else
                {
                    EnsureGlobalProfile(key, item.StrengthModel, item.StrengthClip);
                }
            }
            LoadTriggerIntoItem(item);
        }
        else if (e.PropertyName == nameof(LoraConfigItem.TriggerWords) && !_syncingLoraTrigger)
        {
            string key = item.LoraName.Trim();
            if (key.Length == 0) return;

            var profile = EnsureGlobalProfile(key, item.StrengthModel, item.StrengthClip);
            profile.TriggerWords = item.TriggerWords.Trim();

            _syncingLoraTrigger = true;
            try
            {
                foreach (var other in _currentLoras)
                {
                    if (!ReferenceEquals(other, item) &&
                        string.Equals(other.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        other.TriggerWords = item.TriggerWords;
                    }
                }
            }
            finally
            {
                _syncingLoraTrigger = false;
            }
        }
    }

    private void LoadTriggerIntoItem(LoraConfigItem item)
    {
        _syncingLoraTrigger = true;
        try
        {
            var profile = FindGlobalProfile(item.LoraName);
            item.TriggerWords = profile?.TriggerWords ?? string.Empty;
        }
        finally
        {
            _syncingLoraTrigger = false;
        }
    }

    private void UpdateNoLoraHint()
    {
        TxtNoLoraHint.Visibility = _currentLoras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 全局 LoRA 管理页 ====================

    private LoraProfile? FindGlobalProfile(string? loraName)
    {
        string key = (loraName ?? string.Empty).Trim();
        if (key.Length == 0) return null;
        return _globalLoras.FirstOrDefault(p =>
            string.Equals(p.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    private LoraProfile EnsureGlobalProfile(string loraName, double defaultModel = 1.0, double defaultClip = 1.0)
    {
        string key = loraName.Trim();
        var existing = FindGlobalProfile(key);
        if (existing is not null) return existing;

        var profile = new LoraProfile
        {
            LoraName = key,
            DefaultStrengthModel = defaultModel,
            DefaultStrengthClip = defaultClip
        };
        profile.PropertyChanged += GlobalProfile_PropertyChanged;
        _globalLoras.Add(profile);
        return profile;
    }

    private void GlobalProfile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoraProfile.Alias) || e.PropertyName == nameof(LoraProfile.LoraName))
            RebuildAliasMap();
    }

    private void RebuildAliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _globalLoras)
        {
            string key = p.LoraName.Trim();
            string alias = p.Alias.Trim();
            if (key.Length > 0 && alias.Length > 0)
                map[key] = alias;
        }
        LoraAliasMap = map;
    }

    private void RefreshGlobalLoraStatuses()
    {
        FlushCurrentLorasBack();
        bool hasComfyList = _comfyLoraSet.Count > 0;

        foreach (var profile in _globalLoras)
        {
            string key = profile.LoraName.Trim();
            int refCount = _items.Count(w =>
                w.Loras.Any(l => string.Equals(l.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase)));

            bool missing = hasComfyList && key.Length > 0 && !_comfyLoraSet.Contains(key);
            string usage = refCount > 0 ? $"被 {refCount} 个工作流引用" : "未被工作流引用";
            profile.StatusBadge = missing ? $"⚠ ComfyUI 中未找到 · {usage}" : usage;
        }
    }

    private void RefreshGlobalLoraList(LoraProfile? keepSelected = null)
    {
        RefreshGlobalLoraStatuses();

        string query = (TxtGlobalLoraSearch?.Text ?? string.Empty).Trim();
        var tokens = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        var matched = _globalLoras
            .Where(p =>
            {
                if (tokens.Length == 0) return true;
                string hay = $"{p.LoraName} {p.Alias} {p.TriggerWords}";
                return tokens.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(p => p.LoraName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _filteredGlobalLoras.Clear();
        foreach (var item in matched)
            _filteredGlobalLoras.Add(item);

        if (keepSelected is not null && _filteredGlobalLoras.Contains(keepSelected))
            LstGlobalLoras.SelectedItem = keepSelected;
        else if (_filteredGlobalLoras.Count > 0 && LstGlobalLoras.SelectedItem is null)
            LstGlobalLoras.SelectedIndex = 0;
        else if (_filteredGlobalLoras.Count == 0)
            ShowGlobalLoraEditor(null);
    }

    private void TxtGlobalLoraSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtGlobalLoraSearchHint.Visibility = string.IsNullOrEmpty(TxtGlobalLoraSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshGlobalLoraList(_currentGlobalLora);
    }

    private void LstGlobalLoras_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowGlobalLoraEditor(LstGlobalLoras.SelectedItem as LoraProfile);
    }

    private void ShowGlobalLoraEditor(LoraProfile? profile)
    {
        _currentGlobalLora = profile;
        PnlGlobalLoraEditor.DataContext = profile;

        if (profile is null)
        {
            GlobalLoraScroll.Visibility = Visibility.Collapsed;
            TxtGlobalLoraEmptyHint.Visibility = Visibility.Visible;
            return;
        }

        TxtGlobalLoraEmptyHint.Visibility = Visibility.Collapsed;
        GlobalLoraScroll.Visibility = Visibility.Visible;
        PickerRenameTarget.SelectedLora = string.Empty;
        UpdateCurrentGlobalLoraUsage();
    }

    private void UpdateCurrentGlobalLoraUsage()
    {
        if (_currentGlobalLora is null) return;
        FlushCurrentLorasBack();

        string key = _currentGlobalLora.LoraName.Trim();
        var users = _items
            .Where(w => w.Loras.Any(l => string.Equals(l.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase)))
            .Select(w => w.Name)
            .ToList();

        if (users.Count == 0)
        {
            TxtGlobalLoraUsage.Text =
                "当前没有工作流引用此 LoRA（仅保存在全局库中）。\n" +
                "在工作流里移除某个 LoRA 不会删除这里的触发词与别名；如果以后不再使用，可点左侧「删除」彻底清理。";
        }
        else
        {
            TxtGlobalLoraUsage.Text = $"当前有 {users.Count} 个工作流正在使用此 LoRA：\n" + string.Join("、", users);
        }
    }

    private void BtnAddGlobalLora_Click(object sender, RoutedEventArgs e)
    {
        // 从 ComfyUI 里找一个尚未建档的 LoRA；如果全建过则允许选任意项再改
        string candidate = AllComfyLoraOptions.FirstOrDefault(l => FindGlobalProfile(l) is null)
            ?? AllComfyLoraOptions.FirstOrDefault()
            ?? string.Empty;

        if (candidate.Length == 0)
        {
            ConfirmDialog.Warn(this, "尚未从 ComfyUI 获取到 LoRA 列表，请先确保 ComfyUI 已启动并点击顶部「刷新 ComfyUI 模型列表」。", "无法添加");
            return;
        }

        var existing = FindGlobalProfile(candidate);
        if (existing is not null)
        {
            RefreshGlobalLoraList(existing);
            ConfirmDialog.Warn(this, $"所有 ComfyUI 中的 LoRA 都已在全局库里。当前已定位到「{existing.LoraName}」。", "提示");
            return;
        }

        var profile = EnsureGlobalProfile(candidate);
        RefreshGlobalLoraList(profile);
        LstGlobalLoras.SelectedItem = profile;
    }

    private void BtnDeleteGlobalLora_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGlobalLora is null) return;
        FlushCurrentLorasBack();

        string key = _currentGlobalLora.LoraName.Trim();
        var users = _items
            .Where(w => w.Loras.Any(l => string.Equals(l.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        string msg = users.Count == 0
            ? $"确定从全局 LoRA 库彻底删除「{_currentGlobalLora.DisplayTitle}」吗？"
            : $"有 {users.Count} 个工作流正在使用「{_currentGlobalLora.DisplayTitle}」（{string.Join("、", users.Select(u => u.Name))}）。\n删除后将同时从这些工作流中移除该 LoRA，确定继续吗？";

        if (!ConfirmDialog.ConfirmDelete(this, msg, "删除全局 LoRA"))
            return;

        // 同步从所有工作流及当前编辑区移除该 LoRA
        foreach (var wf in users)
            wf.Loras.RemoveAll(l => string.Equals(l.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase));

        if (_current is not null)
        {
            var toRemove = _currentLoras
                .Where(l => string.Equals(l.LoraName.Trim(), key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var r in toRemove)
            {
                r.PropertyChanged -= LoraItem_PropertyChanged;
                _currentLoras.Remove(r);
            }
            UpdateNoLoraHint();
        }

        _currentGlobalLora.PropertyChanged -= GlobalProfile_PropertyChanged;
        _globalLoras.Remove(_currentGlobalLora);
        _currentGlobalLora = null;

        RebuildAliasMap();
        RefreshGlobalLoraList();
        if (_filteredGlobalLoras.Count > 0)
            LstGlobalLoras.SelectedIndex = 0;
        else
            ShowGlobalLoraEditor(null);
    }

    /// <summary>
    /// LoRA 文件在硬盘改名或移动目录后，一键重新指向新文件并同步更新所有工作流引用。
    /// </summary>
    private void BtnRenameGlobalLora_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGlobalLora is null) return;
        string oldPath = _currentGlobalLora.LoraName.Trim();
        string newPath = (PickerRenameTarget.SelectedLora ?? string.Empty).Trim();

        if (newPath.Length == 0)
        {
            ConfirmDialog.Warn(this, "请先在左侧下拉框中选择新的 LoRA 文件。", "未选择目标文件");
            return;
        }

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            ConfirmDialog.Warn(this, "新旧文件路径相同，无需迁移。", "提示");
            return;
        }

        var conflict = FindGlobalProfile(newPath);
        if (conflict is not null && !ReferenceEquals(conflict, _currentGlobalLora))
        {
            bool merge = ConfirmDialog.ConfirmDelete(
                this,
                $"全局库中已存在「{newPath}」。\n继续将把当前 LoRA 的别名、权重与触发词覆盖到目标项，并同步更新所有工作流，是否合并？",
                "目标 LoRA 已存在",
                "合并并同步");
            if (!merge) return;

            conflict.Alias = string.IsNullOrWhiteSpace(_currentGlobalLora.Alias) ? conflict.Alias : _currentGlobalLora.Alias;
            conflict.TriggerWords = string.IsNullOrWhiteSpace(_currentGlobalLora.TriggerWords) ? conflict.TriggerWords : _currentGlobalLora.TriggerWords;
            conflict.DefaultStrengthModel = _currentGlobalLora.DefaultStrengthModel;
            conflict.DefaultStrengthClip = _currentGlobalLora.DefaultStrengthClip;

            _currentGlobalLora.PropertyChanged -= GlobalProfile_PropertyChanged;
            _globalLoras.Remove(_currentGlobalLora);
            _currentGlobalLora = conflict;
        }
        else
        {
            _currentGlobalLora.LoraName = newPath;
        }

        // 批量同步所有工作流里的旧路径
        int updatedWorkflows = 0;
        FlushCurrentLorasBack();
        foreach (var wf in _items)
        {
            bool touched = false;
            foreach (var lora in wf.Loras)
            {
                if (string.Equals(lora.LoraName.Trim(), oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    lora.LoraName = newPath;
                    touched = true;
                }
            }
            if (touched) updatedWorkflows++;
        }

        // 同步当前正在编辑的工作流 LoRA 集合
        foreach (var lora in _currentLoras)
        {
            if (string.Equals(lora.LoraName.Trim(), oldPath, StringComparison.OrdinalIgnoreCase))
            {
                lora.LoraName = newPath;
                LoadTriggerIntoItem(lora);
            }
        }

        RebuildAliasMap();
        ApplyCatalog(_lastCatalog);
        RefreshGlobalLoraList(_currentGlobalLora);
        ShowGlobalLoraEditor(_currentGlobalLora);

        ConfirmDialog.Warn(
            this,
            $"已将 LoRA 迁移为：\n{newPath}\n\n并同步更新了 {updatedWorkflows} 个工作流的引用。",
            "改名迁移完成");
    }

    // ==================== 一键导入旧版 JSON ====================

    private void BtnImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要导入的旧版 ComfyUI API 工作流 JSON",
            Filter = "ComfyUI API JSON (*.json)|*.json",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;

        FlushCurrentLorasBack();
        int imported = 0;
        var failed = new List<string>();
        WorkflowApiConfig? lastImported = null;

        foreach (var file in dlg.FileNames)
        {
            if (WorkflowApiBuilder.TryImportFromJsonFile(file, out var cfg, out var err) && cfg is not null)
            {
                _items.Add(cfg);
                foreach (var l in cfg.Loras)
                {
                    if (!string.IsNullOrWhiteSpace(l.LoraName))
                        EnsureGlobalProfile(l.LoraName, l.StrengthModel, l.StrengthClip);
                }
                lastImported = cfg;
                imported++;
            }
            else
            {
                failed.Add($"{System.IO.Path.GetFileName(file)}: {err}");
            }
        }

        if (imported > 0)
        {
            ApplyCatalog(_lastCatalog);
            RefreshGlobalLoraList(_currentGlobalLora);
            if (lastImported is not null)
                LstConfigs.SelectedItem = lastImported;
        }

        if (failed.Count > 0)
            ConfirmDialog.Warn(this, $"成功导入 {imported} 个，失败 {failed.Count} 个：\n" + string.Join("\n", failed), "导入结果");
        else if (imported > 0)
            TxtCatalogStatus.Text = $"已从旧版 JSON 导入 {imported} 个工作流配置（保存后即彻底脱离 JSON 文件）";
    }

    // ==================== 保存 / 取消 ====================

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        FlushCurrentLorasBack();

        var errors = _items
            .Select(WorkflowApiBuilder.Validate)
            .Where(msg => msg.Length > 0)
            .ToList();

        if (errors.Count > 0)
        {
            ConfirmDialog.Warn(this, "请先补全以下必填项：\n" + string.Join("\n", errors), "配置不完整");
            return;
        }

        ResultWorkflows = _items.Select(i => i.Clone(newId: false)).ToList();
        ResultLoraProfiles = _globalLoras
            .Where(p => !string.IsNullOrWhiteSpace(p.LoraName))
            .Select(p => p.Clone())
            .ToList();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
