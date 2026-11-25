using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawBench.Core;
using DrawBench.Core.Comfy;
using DrawBench.Core.Logging;
using DrawBench.Core.Ui;
using DrawBench.Modules.Batch.Models;
using DrawBench.Modules.Batch.Services;

namespace DrawBench.Modules.Batch.Views;

/// <summary>
/// 批量提交模块：按配置菜单中维护的工作流 API 参数，动态生成 ComfyUI API JSON 并逐条提交，
/// 同时通过 ComfyUI WebSocket 实时显示总张数进度与单张采样步数。
/// </summary>
public partial class BatchView : UserControl
{
    private readonly ObservableCollection<WorkflowItem> _workflows = new();

    private readonly SettingsStore _store;
    private BatchSettings _settings;
    private static readonly Lazy<Encoding> Gb18030Lazy = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936);
    });
    private static readonly char[] InvalidDateSuffixChars = { ':', '*', '?', '"', '<', '>', '|' };

    private bool _isRunning;
    private bool _suppressSelectionSave;
    private bool _uiReady;
    private CancellationTokenSource? _runCts;
    private ComfyClient? _activeClient;
    private ComfyProgressMonitor? _activeMonitor;

    public BatchView(SettingsStore store)
    {
        _store = store;
        _settings = store.Get<BatchSettings>(ModuleIds.Batch) ?? new BatchSettings();
        _settings.Normalize();

        InitializeComponent();

        LstWorkflows.ItemsSource = _workflows;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    // ==================== 启动与清理 ====================

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnViewLoaded;

        TxtBaseUrl.Text = ComfyClient.Normalize(_settings.ComfyBaseUrl);
        TxtCount.Text = "1";
        TxtDate.Text = DateTime.Now.ToString("yyMMdd");
        TxtSeed.Text = "-1";
        ChkLockSeed.IsChecked = true;
        ChkGlobalLockSeed.IsChecked = false;

        UpdateSeedControls();
        ReloadWorkflows();

        _uiReady = true;
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        // 切换模块页时保留运行中的监听，仅在宿主窗口关闭时清理
        if (Application.Current?.MainWindow is null || !Application.Current.MainWindow.IsLoaded)
            CleanupMonitor();
    }

    private void CleanupMonitor()
    {
        try { _runCts?.Cancel(); } catch { }
        try { _activeMonitor?.Dispose(); } catch { }
        try { _runCts?.Dispose(); } catch { }
        _activeMonitor = null;
        _runCts = null;
        _activeClient = null;
    }

    /// <summary>把设置写回并落盘。</summary>
    public void SaveSettings() => Persist();

    private void TxtBaseUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        TxtBaseUrl.Text = ComfyClient.Normalize(TxtBaseUrl.Text);
        Persist();
    }

    private void Persist()
    {
        if (!_uiReady) return;
        _settings.ComfyBaseUrl = ComfyClient.Normalize(TxtBaseUrl.Text);
        _store.Set(ModuleIds.Batch, _settings);
    }

    // ==================== 工作流列表与配置菜单 ====================

    private void ReloadWorkflows()
    {
        _suppressSelectionSave = true;
        try
        {
            LoadWorkflows();
        }
        finally
        {
            _suppressSelectionSave = false;
        }

        SaveSelection();
    }

    private void LoadWorkflows()
    {
        foreach (var old in _workflows)
            old.PropertyChanged -= Workflow_PropertyChanged;
        _workflows.Clear();

        if (_settings.Workflows.Count == 0)
        {
            SetStatus("暂无工作流 API，请点击左侧「配置工作流」添加（也可在配置窗口一键导入旧 JSON）");
            return;
        }

        foreach (var cfg in _settings.Workflows)
        {
            var item = new WorkflowItem(cfg);
            if (_settings.HasSavedSelection)
                item.IsEnabled = _settings.IsEnabled(item.Id);

            item.PropertyChanged += Workflow_PropertyChanged;
            _workflows.Add(item);
        }

        SetStatus(_settings.HasSavedSelection
            ? $"已加载 {_workflows.Count} 个工作流 API，已恢复上次勾选"
            : $"已加载 {_workflows.Count} 个工作流 API，默认全部勾选");
    }

    private void BtnConfigWorkflows_Click(object sender, RoutedEventArgs e)
    {
        string baseUrl = ComfyClient.Normalize(TxtBaseUrl.Text);
        var existingIds = new HashSet<string>(_settings.Workflows.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);

        var win = new WorkflowConfigWindow(_settings.Workflows, _settings.LoraProfiles, _settings.LoraCategories, baseUrl)
        {
            Owner = Window.GetWindow(this)
        };

        if (win.ShowDialog() != true) return;

        var updated = win.ResultWorkflows;
        _settings.Workflows = updated;
        _settings.LoraProfiles = win.ResultLoraProfiles;
        _settings.LoraCategories = win.ResultLoraCategories;
        _settings.LoraTriggerWords = _settings.BuildTriggerLookup();

        if (_settings.HasSavedSelection)
        {
            foreach (var cfg in updated)
            {
                if (!existingIds.Contains(cfg.Id) && !_settings.IsEnabled(cfg.Id))
                    _settings.EnabledWorkflows.Add(cfg.Id);
            }
        }

        _store.Set(ModuleIds.Batch, _settings);
        ReloadWorkflows();
        SetStatus($"已保存 {updated.Count} 个工作流 API · {_settings.LoraProfiles.Count} 条全局 LoRA 配置");
    }

    private void Workflow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkflowItem.IsEnabled)) return;
        SaveSelection();
    }

    private void SaveSelection()
    {
        if (_suppressSelectionSave) return;
        if (_workflows.Count == 0)
        {
            UpdateBatchPreview();
            return;
        }

        _settings.EnabledWorkflows = _workflows.Where(w => w.IsEnabled).Select(w => w.Id).ToList();
        _settings.HasSavedSelection = true;
        _store.Set(ModuleIds.Batch, _settings);
        UpdateBatchPreview();
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => SetAllWorkflowsEnabled(true);

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e) => SetAllWorkflowsEnabled(false);

    private void SetAllWorkflowsEnabled(bool enabled)
    {
        _suppressSelectionSave = true;
        try
        {
            foreach (var wf in _workflows) wf.IsEnabled = enabled;
        }
        finally
        {
            _suppressSelectionSave = false;
        }

        SaveSelection();
    }

    // ==================== seed 输入 ====================

    private void ChkGlobalLockSeed_Changed(object sender, RoutedEventArgs e) => UpdateSeedControls();

    private void TxtSeed_TextChanged(object sender, TextChangedEventArgs e) => UpdateSeedControls();

    private void TxtCount_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateBatchPreview();
    }

    private void TxtPromptQueue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtPromptCount != null)
        {
            int count = ParsePromptLines(TxtPromptQueue.Text).Count();
            TxtPromptCount.Text = count > 0 ? $"{count} 组" : string.Empty;
        }
        if (!_uiReady) return;
        UpdateBatchPreview();
    }

    private void BtnClearPositive_Click(object sender, RoutedEventArgs e) => TxtPromptQueue.Clear();

    private void BtnClearNegative_Click(object sender, RoutedEventArgs e) => TxtNegativePrompt.Clear();

    private void UpdateBatchPreview()
    {
        if (_isRunning || TxtPromptQueue is null || TxtCount is null || TxtStatus is null) return;

        int prompts = ParsePromptLines(TxtPromptQueue.Text).Count();
        int workflows = _workflows.Count(w => w.IsEnabled);
        int perCount = int.TryParse(TxtCount.Text.Trim(), out int n) && n > 0 ? n : 1;

        if (prompts == 0)
        {
            SetStatus(_workflows.Count == 0
                ? "就绪 · 请点击「配置工作流」添加工作流 API"
                : $"就绪 · 已勾选 {workflows}/{_workflows.Count} 个工作流");
            return;
        }

        if (workflows == 0)
        {
            SetStatus($"队列共 {prompts} 组提示词 · 请至少勾选 1 个工作流");
            return;
        }

        long total = (long)prompts * workflows * perCount;
        SetStatus($"预计生成：{prompts} 组提示词 × {workflows} 个工作流 × {perCount} 张 = 共 {total} 张");
    }

    private void UpdateSeedControls()
    {
        if (ChkLockSeed is null || ChkGlobalLockSeed is null) return;

        bool fixedSeed = IsSeedFixed(TxtSeed?.Text);
        ChkGlobalLockSeed.IsEnabled = !fixedSeed;
        ChkLockSeed.IsEnabled = !fixedSeed && ChkGlobalLockSeed.IsChecked != true;
    }

    private static bool IsSeedFixed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return long.TryParse(text.Trim(), out long seed) && seed >= 0;
    }

    private static bool TryParseSeed(string? text, out long? fixedSeed)
    {
        fixedSeed = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!long.TryParse(text.Trim(), out long seed) || seed < -1) return false;

        if (seed >= 0) fixedSeed = seed;
        return true;
    }

    // ==================== 提交与实时进度 ====================

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning) return;

        BtnStop.IsEnabled = false;
        SetStatus("正在中止并清理本批未开始的任务…");

        var client = _activeClient;
        var monitor = _activeMonitor;
        var pendingIds = monitor?.GetPendingPromptIds() ?? new List<string>();

        try { _runCts?.Cancel(); } catch { }

        if (client is not null)
        {
            await client.DeletePendingAsync(pendingIds);
            await client.InterruptAsync();
        }

        AppendLog($"⏹ 已请求中止，并尝试从 ComfyUI 队列移除 {pendingIds.Count} 个未开始的任务。");
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_workflows.Count == 0)
        {
            SetError("暂无工作流 API，请先点击左侧「配置工作流」添加。");
            return;
        }

        var selected = _workflows.Where(w => w.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            SetError("请至少勾选一个工作流。");
            return;
        }

        var invalidMessages = selected
            .Select(w => WorkflowApiBuilder.Validate(w.Config))
            .Where(msg => msg.Length > 0)
            .ToList();
        if (invalidMessages.Count > 0)
        {
            SetError(invalidMessages[0]);
            ConfirmDialog.Warn(Window.GetWindow(this), string.Join("\n", invalidMessages), "工作流配置不完整");
            return;
        }

        if (!int.TryParse(TxtCount.Text.Trim(), out int perPromptCount) || perPromptCount < 1)
        {
            SetError("每组生成数量必须为大于 0 的整数，请检查。");
            return;
        }

        var promptLines = ParsePromptLines(TxtPromptQueue.Text).ToList();
        if (promptLines.Count == 0)
        {
            SetError("正向提示词队列为空，请填写内容或拖入 txt 文件。");
            return;
        }

        string dateSuffix = TxtDate.Text.Trim();
        if (dateSuffix.Length == 0)
        {
            SetError("日期后缀不能为空，请填写后再开始。");
            return;
        }
        if (dateSuffix.IndexOfAny(InvalidDateSuffixChars) >= 0 || dateSuffix.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            SetError("日期后缀包含非法文件名字符（不能含 : * ? \" < > |）。");
            return;
        }

        if (!TryParseSeed(TxtSeed.Text, out long? fixedSeed))
        {
            SetError("种子必须是非负整数，或填 -1 表示随机。");
            return;
        }

        Persist();

        string negativePrompt = TxtNegativePrompt.Text.Trim();
        bool lockSeed = ChkLockSeed.IsChecked == true;
        bool globalLockSeed = ChkGlobalLockSeed.IsChecked == true;
        var triggerLookup = _settings.BuildTriggerLookup();

        var batchSeeds = new List<long>();
        if (globalLockSeed && fixedSeed is null)
        {
            for (int round = 0; round < perPromptCount; round++)
                batchSeeds.Add(NextSeed());
        }

        var client = new ComfyClient(TxtBaseUrl.Text);
        var runCts = new CancellationTokenSource();
        var monitor = new ComfyProgressMonitor(client);

        _activeClient = client;
        _activeMonitor = monitor;
        _runCts = runCts;

        _isRunning = true;
        BtnRun.IsEnabled = false;
        BtnStop.Visibility = Visibility.Visible;
        BtnStop.IsEnabled = true;
        PbBatch.Visibility = Visibility.Visible;
        PbBatch.Value = 0;
        TxtLog.Text = string.Empty;

        int totalTasks = (int)Math.Min(int.MaxValue, (long)promptLines.Count * selected.Count * perPromptCount);
        SetStatus($"正在连接 ComfyUI…");
        AppendLog($"本次提交使用 {selected.Count} 个工作流：{string.Join("、", selected.Select(w => w.DisplayName))}");

        bool submitting = true;
        monitor.ProgressChanged += snapshot =>
        {
            RunOnUi(() =>
            {
                if (submitting) return;
                PbBatch.Value = snapshot.OverallPercent;
                SetStatus(FormatProgressStatus(snapshot));
            });
        };
        monitor.ExecutionError += (wfName, reason) =>
        {
            RunOnUi(() => AppendLog($"✗ [{(string.IsNullOrEmpty(wfName) ? "ComfyUI" : wfName)}] 生成报错：{reason}"));
        };

        try
        {
            var queue = await client.GetQueueAsync(runCts.Token);
            if (!queue.Reachable)
            {
                SetError($"连不上 ComfyUI：{client.BaseUrl}");
                ConfirmDialog.Warn(Window.GetWindow(this), $"连不上 ComfyUI（{client.BaseUrl}）。\n请确认它已经启动，或在顶部改地址。", "ComfyUI 未就绪");
                return;
            }

            // 先连 WebSocket，避免首张图极快执行时漏掉进度事件
            await monitor.StartAsync(totalTasks, runCts.Token);

            int queued = 0;
            int submitFailed = 0;
            bool disconnected = false;

            async Task<bool> SubmitOne(WorkflowItem wf, string prompt, long? seed)
            {
                if (runCts.IsCancellationRequested) return false;

                long actualSeed = seed ?? NextSeed();
                JsonObject payload = WorkflowApiBuilder.Build(
                    wf.Config,
                    triggerLookup,
                    prompt,
                    negativePrompt,
                    dateSuffix,
                    actualSeed);

                var result = await client.SubmitAsync(payload, runCts.Token);
                if (result.Ok)
                {
                    queued++;
                    if (!string.IsNullOrEmpty(result.PromptId))
                        monitor.TrackPrompt(result.PromptId, wf.DisplayName);
                }
                else if (result.Status == ComfySubmitStatus.Cancelled)
                {
                    return false;
                }
                else
                {
                    submitFailed++;
                    AppendLog($"✗ [{wf.DisplayName}] {result.Message}");
                    if (result.Status == ComfySubmitStatus.Unreachable)
                    {
                        disconnected = true;
                        AppendLog("✗ ComfyUI 连接已中断，已中止剩余任务。");
                        return false;
                    }
                }

                SetStatus($"正在入队 {queued + submitFailed}/{totalTasks}…");
                return true;
            }

            if (batchSeeds.Count > 0)
            {
                foreach (long roundSeed in batchSeeds)
                {
                    if (disconnected || runCts.IsCancellationRequested) break;
                    foreach (var prompt in promptLines)
                    {
                        if (disconnected || runCts.IsCancellationRequested) break;
                        foreach (var wf in selected)
                        {
                            if (!await SubmitOne(wf, prompt, roundSeed)) break;
                        }
                    }
                }
            }
            else
            {
                foreach (var prompt in promptLines)
                {
                    if (disconnected || runCts.IsCancellationRequested) break;
                    for (int i = 0; i < perPromptCount; i++)
                    {
                        if (disconnected || runCts.IsCancellationRequested) break;
                        long? sharedSeed = fixedSeed ?? (lockSeed ? NextSeed() : null);

                        foreach (var wf in selected)
                        {
                            if (!await SubmitOne(wf, prompt, sharedSeed)) break;
                        }
                    }
                }
            }

            submitting = false;
            monitor.CompleteSubmission();
            ShowSubmitted(queued, submitFailed, batchSeeds, fixedSeed);

            if (disconnected || runCts.IsCancellationRequested || queued == 0)
            {
                SetStatus(runCts.IsCancellationRequested
                    ? "已中止"
                    : (disconnected ? "连接中断，已中止" : $"提交失败（{submitFailed} 个）"));
                return;
            }

            // 等待 ComfyUI 执行完本批所有任务并实时刷新进度条
            AppendLog(monitor.IsWebSocketConnected
                ? "已连接实时进度监听，等待 ComfyUI 生成完成…"
                : "实时进度通道未连上，已切换为队列轮询跟踪完成进度…");

            var finalSnapshot = await monitor.WaitForCompletionAsync(runCts.Token);
            PbBatch.Value = 100;

            int totalErrors = submitFailed + finalSnapshot.Failed;
            if (totalErrors == 0)
            {
                SetStatus($"全部生成完成（{finalSnapshot.Completed}/{finalSnapshot.Total} 张）");
                AppendLog($"✓ 全部生成完成，共 {finalSnapshot.Completed} 张。");
            }
            else
            {
                SetStatus($"生成完成：{finalSnapshot.Completed} 成功，{totalErrors} 失败");
                AppendLog($"生成结束：成功 {finalSnapshot.Completed} 张，失败 {totalErrors} 张。");
            }

            AppLog.Info($"批量生成完成：入队 {queued}，成功 {finalSnapshot.Completed}，失败 {totalErrors}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已中止");
        }
        catch (Exception ex)
        {
            SetError($"出错: {ex.Message}");
            AppLog.Error("批量提交出错", ex);
            ConfirmDialog.Warn(Window.GetWindow(this), ex.Message, "提交任务出错");
        }
        finally
        {
            CleanupMonitor();
            _isRunning = false;
            BtnRun.IsEnabled = true;
            BtnStop.Visibility = Visibility.Collapsed;
            PbBatch.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatProgressStatus(BatchProgressSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append($"正在生成：{s.Finished}/{s.Total} 张（{s.OverallPercent:0}%）");
        if (!string.IsNullOrEmpty(s.CurrentWorkflow))
            sb.Append($" · [{s.CurrentWorkflow}]");
        if (s.MaxStep > 0)
            sb.Append($" 采样 {s.CurrentStep}/{s.MaxStep} 步");
        if (s.Failed > 0)
            sb.Append($" · 失败 {s.Failed}");
        return sb.ToString();
    }

    private void AppendLog(string line)
    {
        if (TxtLog.Text.Length > 0) TxtLog.AppendText(Environment.NewLine);
        TxtLog.AppendText(line);
        TxtLog.ScrollToEnd();
    }

    private static long NextSeed() => Random.Shared.NextInt64(0, long.MaxValue);

    // ==================== 拖入 txt ====================

    private void Prompt_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool hasTxt = e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            && paths.Any(p => File.Exists(p) && string.Equals(Path.GetExtension(p), ".txt", StringComparison.OrdinalIgnoreCase));
        e.Effects = hasTxt ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Prompt_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (sender is not TextBox target) return;

        var txtFiles = paths
            .Where(p => File.Exists(p) &&
                        string.Equals(Path.GetExtension(p), ".txt", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (txtFiles.Count == 0)
        {
            SetError("请拖入 .txt 文本文件。");
            return;
        }

        bool isPositive = ReferenceEquals(target, TxtPromptQueue);
        var pieces = new List<string>();
        var failed = new List<string>();

        foreach (var file in txtFiles)
        {
            try
            {
                string content = ReadTextAuto(file);
                if (isPositive) pieces.AddRange(ParsePromptLines(content));
                else pieces.Add(content.Trim());
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var nonEmpty = pieces.Where(p => p.Length > 0).ToList();

        if (nonEmpty.Count > 0)
        {
            var existing = target.Text.TrimEnd();
            var addition = string.Join(Environment.NewLine, nonEmpty);
            target.Text = existing.Length == 0 ? addition : existing + Environment.NewLine + addition;
            target.CaretIndex = target.Text.Length;
            target.ScrollToEnd();
        }

        string unit = isPositive ? "组提示词" : "条负面提示词";

        if (failed.Count == 0)
        {
            SetStatus($"已读取 {txtFiles.Count} 个 txt，共 {nonEmpty.Count} {unit}");
        }
        else
        {
            SetError($"读取失败 {failed.Count} 个文件");
            ConfirmDialog.Warn(Window.GetWindow(this), string.Join("\n", failed), "部分文件读取失败");
        }
    }

    /// <summary>从统一设置重新加载工作流列表（供参数读取等模块导入工作流后实时刷新）。</summary>
    public void ReloadFromStore()
    {
        RunOnUi(() =>
        {
            _settings = _store.Get<BatchSettings>(ModuleIds.Batch) ?? new BatchSettings();
            _settings.Normalize();
            ReloadWorkflows();
        });
    }

    public string PromptQueueText => TxtPromptQueue.Text;

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.Invoke(action);
    }

    public void SetPositive(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        RunOnUi(() =>
        {
            var lines = ParsePromptLines(text).ToList();
            TxtPromptQueue.Text = string.Join(Environment.NewLine, lines);
            TxtPromptQueue.CaretIndex = TxtPromptQueue.Text.Length;
            TxtPromptQueue.ScrollToEnd();
            SetStatus("已清空原队列并填入外部提示词。");
        });
    }

    public void AppendPositive(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        RunOnUi(() =>
        {
            var lines = ParsePromptLines(text).ToList();
            if (lines.Count == 0) return;
            var existing = TxtPromptQueue.Text.TrimEnd();
            var addition = string.Join(Environment.NewLine, lines);
            TxtPromptQueue.Text = existing.Length == 0 ? addition : existing + Environment.NewLine + addition;
            TxtPromptQueue.CaretIndex = TxtPromptQueue.Text.Length;
            TxtPromptQueue.ScrollToEnd();
            SetStatus("已追加外部提示词到队列。");
        });
    }

    public void SetNegative(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        RunOnUi(() =>
        {
            TxtNegativePrompt.Text = text.Trim();
            TxtNegativePrompt.CaretIndex = TxtNegativePrompt.Text.Length;
            TxtNegativePrompt.ScrollToEnd();
            SetStatus("已填入负向提示词。");
        });
    }

    public void AppendNegative(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        RunOnUi(() =>
        {
            var existing = TxtNegativePrompt.Text.TrimEnd();
            var addition = text.Trim();
            TxtNegativePrompt.Text = existing.Length == 0 ? addition : existing + Environment.NewLine + addition;
            TxtNegativePrompt.CaretIndex = TxtNegativePrompt.Text.Length;
            TxtNegativePrompt.ScrollToEnd();
            SetStatus("已追加负向提示词。");
        });
    }

    private static IEnumerable<string> ParsePromptLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n')
               .Split('\n')
               .Select(l => l.Trim())
               .Where(l => l.Length > 0);

    private static string ReadTextAuto(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Length == 0) return string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gb18030.GetString(bytes);
        }
    }

    private static Encoding Gb18030 => Gb18030Lazy.Value;

    // ==================== 状态与日志 ====================

    private void SetStatus(string msg)
    {
        TxtStatus.Text = msg;
        TxtStatus.Foreground = (Brush)FindResource("Muted");
    }

    private void SetError(string msg)
    {
        TxtStatus.Text = msg;
        TxtStatus.Foreground = (Brush)FindResource("Danger");
    }

    private void ShowSubmitted(int done, int failed, IReadOnlyList<long> batchSeeds, long? fixedSeed)
    {
        string fixedText = fixedSeed is not null ? $"，seed 固定为 {fixedSeed}" : "";
        string failText = failed > 0 ? $"，入队失败 {failed} 个" : "";

        if (batchSeeds.Count == 0)
        {
            AppendLog($"任务已入队，共 {done} 个{failText}{fixedText}。");
            return;
        }

        string seedText = batchSeeds.Count <= 10
            ? string.Join(", ", batchSeeds)
            : string.Join(", ", batchSeeds.Take(10)) + " …";

        AppendLog($"任务已入队，共 {done} 个{failText}。共 {batchSeeds.Count} 轮，每轮全局 seed: {seedText}");
    }
}
