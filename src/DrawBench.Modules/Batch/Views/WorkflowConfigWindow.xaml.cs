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
/// 工作流 API 可视化配置窗口：使用下拉框与输入框管理底模、LoRA、触发词与采样参数。
/// LoRA 触发词按 LoRA 路径全局存储，不与具体底模/工作流绑定。
/// </summary>
public partial class WorkflowConfigWindow : Window, INotifyPropertyChanged
{
    private readonly string _comfyBaseUrl;
    private readonly ObservableCollection<WorkflowApiConfig> _items = new();
    private readonly ObservableCollection<LoraConfigItem> _currentLoras = new();
    private readonly Dictionary<string, string> _loraTriggers;

    private List<string> _allLoraOptions = new();
    private ComfyOptionsCatalog _lastCatalog = new();
    private WorkflowApiConfig? _current;
    private bool _syncingModeCombo;
    private bool _syncingLoraTrigger;

    public WorkflowConfigWindow(
        IEnumerable<WorkflowApiConfig> initialWorkflows,
        IDictionary<string, string>? initialLoraTriggers,
        string comfyBaseUrl)
    {
        _comfyBaseUrl = comfyBaseUrl;
        _loraTriggers = initialLoraTriggers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(initialLoraTriggers, StringComparer.OrdinalIgnoreCase);

        InitializeComponent();

        foreach (var wf in initialWorkflows)
            _items.Add(wf.Clone(newId: false));

        LstConfigs.ItemsSource = _items;
        LstLoras.ItemsSource = _currentLoras;

        ApplyCatalog(_lastCatalog);

        Loaded += OnLoaded;
    }

    /// <summary>点击「保存并关闭」后返回的工作流列表。</summary>
    public List<WorkflowApiConfig> ResultWorkflows { get; private set; } = new();

    /// <summary>点击「保存并关闭」后返回的全局 LoRA 触发词库。</summary>
    public Dictionary<string, string> ResultLoraTriggers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    // 所有下拉框的稳定数据源集合
    public ObservableCollection<string> CheckpointOptions { get; } = new();
    public ObservableCollection<string> UnetOptions { get; } = new();
    public ObservableCollection<string> WeightDtypeOptions { get; } = new();
    public ObservableCollection<string> ClipOptions { get; } = new();
    public ObservableCollection<string> ClipTypeOptions { get; } = new();
    public ObservableCollection<string> VaeOptions { get; } = new();
    public ObservableCollection<string> FilteredLoraOptions { get; } = new();
    public ObservableCollection<string> SamplerOptions { get; } = new();
    public ObservableCollection<string> SchedulerOptions { get; } = new();

    /// <summary>当前选中工作流是否为 Checkpoint 模式（决定 CLIP 权重输入框是否可用）。</summary>
    public bool IsCurrentCheckpointMode => _current?.LoaderMode == WorkflowLoaderMode.Checkpoint;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (_items.Count > 0)
            LstConfigs.SelectedIndex = 0;
        else
            ShowEditor(null);

        await RefreshCatalogAsync();
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
            ApplyCatalog(catalog);
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
            .Concat(_items.SelectMany(w => w.Loras).Select(l => l.LoraName))
            .Concat(loraSnapshot)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    // ==================== LoRA 行操作 & 全局触发词联动 ====================

    private void BtnAddLora_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var item = new LoraConfigItem
        {
            Enabled = true,
            LoraName = FilteredLoraOptions.FirstOrDefault() ?? string.Empty,
            StrengthModel = 1.0,
            StrengthClip = 1.0
        };
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
            LoadTriggerIntoItem(item);
        }
        else if (e.PropertyName == nameof(LoraConfigItem.TriggerWords) && !_syncingLoraTrigger)
        {
            string key = item.LoraName.Trim();
            if (key.Length == 0) return;

            string val = item.TriggerWords.Trim();
            if (val.Length == 0) _loraTriggers.Remove(key);
            else _loraTriggers[key] = val;

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
            string key = item.LoraName.Trim();
            item.TriggerWords = key.Length > 0 && _loraTriggers.TryGetValue(key, out var trigger)
                ? trigger
                : string.Empty;
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
        ResultLoraTriggers = new Dictionary<string, string>(_loraTriggers, StringComparer.OrdinalIgnoreCase);
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
