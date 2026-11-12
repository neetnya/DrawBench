using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawBench.Core;
using DrawBench.Core.Comfy;
using DrawBench.Core.Images;
using DrawBench.Core.Logging;
using DrawBench.Core.Ui;
using DrawBench.Modules.Batch.Models;

namespace DrawBench.Modules.Batch.Views;

/// <summary>
/// 批量提交模块：把正向/负向提示词写进 API 格式工作流，逐条提交给 ComfyUI。
/// 与旧版的区别只有三处：路径走统一的 api\ 目录、设置进统一 settings.json、
/// 提交改用 Core 的 <see cref="ComfyClient"/>（多了连不上/被拒绝的明确提示）。
/// </summary>
public partial class BatchView : UserControl
{
    /// <summary>
    /// 必须用可通知的集合：刷新/拖入时列表会重建条目，
    /// 用普通 List 的话 ItemsSource 赋值给的是同一个实例，WPF 不会重新生成条目，
    /// 界面上的勾选框会绑在早已丢弃的旧对象上（表现为全选/全不选点了没反应）。
    /// </summary>
    private readonly ObservableCollection<WorkflowItem> _workflows = new();

    private readonly SettingsStore _store;
    private BatchSettings _settings;
    private static Encoding? _gbEncoding;

    /// <summary>重建列表期间不要回写勾选状态，否则会拿半截列表覆盖已保存的勾选。</summary>
    private bool _suppressSelectionSave;

    /// <summary>
    /// 界面初始化完成前不要回写设置：XAML 里 TxtSeed 的 Text="-1" 会提前触发 TextChanged，
    /// 那时其它控件还没创建，直接访问会空引用。
    /// </summary>
    private bool _uiReady;

    public BatchView(SettingsStore store)
    {
        _store = store;
        _settings = store.Get<BatchSettings>(ModuleIds.Batch) ?? new BatchSettings();

        InitializeComponent();

        LstWorkflows.ItemsSource = _workflows;

        Loaded += OnViewLoaded;
    }

    // ==================== 启动 ====================

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnViewLoaded;

        TxtBaseUrl.Text = ComfyClient.Normalize(_settings.ComfyBaseUrl);
        TxtCount.Text = "1";
        TxtDate.Text = DateTime.Now.ToString("yyMMdd");
        TxtSeed.Text = "-1";
        ChkLockSeed.IsChecked = false;
        ChkGlobalLockSeed.IsChecked = false;

        UpdateSeedControls();
        ReloadWorkflows();

        _uiReady = true;
    }

    /// <summary>把 ComfyUI 地址写回设置并落盘（勾选状态由 SaveSelection 维护）。</summary>
    private void Persist()
    {
        if (!_uiReady) return;
        _settings.ComfyBaseUrl = ComfyClient.Normalize(TxtBaseUrl.Text);

        // 勾选状态只由 ReloadWorkflows / 勾选事件维护，这里不要覆盖
        _store.Set(ModuleIds.Batch, _settings);
    }

    // ==================== 工作流列表 ====================

    private static string WorkflowDirectory => AppPaths.Current.ApiDir;

    private void ReloadWorkflows()
    {
        // 重建期间条目的 IsEnabled 变化不应该回写设置
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
        _workflows.Clear();
        string targetDir = WorkflowDirectory;

        if (!Directory.Exists(targetDir))
        {
            SetStatus($"未找到 api 目录，可把 .json 拖进左侧列表创建: {targetDir}");
            return;
        }

        var files = Directory.GetFiles(targetDir, "*.json")
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), NaturalComparer.Instance)
            .ToArray();

        if (files.Length == 0)
        {
            SetStatus($"api 目录中暂无 JSON，可拖入 .json 文件添加: {targetDir}");
            return;
        }

        foreach (var path in files)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                var item = new WorkflowItem(path, root);

                // 保存过勾选状态时按上次的来；否则全部默认勾选
                if (_settings.HasSavedSelection)
                    item.IsEnabled = _settings.IsEnabled(item.FileName);

                item.PropertyChanged += Workflow_PropertyChanged;
                _workflows.Add(item);
            }
            catch (Exception ex)
            {
                UiHost.Warn($"{Path.GetFileName(path)}\n{ex.Message}", "工作流加载失败");
            }
        }

        SetStatus(_settings.HasSavedSelection
            ? $"已加载 {_workflows.Count} 个 JSON，已恢复上次勾选"
            : $"已加载 {_workflows.Count} 个 JSON，默认全部勾选");
    }

    private void Workflow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkflowItem.IsEnabled)) return;
        SaveSelection();
    }

    private void SaveSelection()
    {
        // 列表重建过程中不要回写；列表为空时也不要把已保存的勾选状态清掉
        if (_suppressSelectionSave) return;
        if (_workflows.Count == 0) return;

        _settings.EnabledWorkflows = _workflows.Where(w => w.IsEnabled).Select(w => w.FileName).ToList();
        _settings.HasSavedSelection = true;
        _store.Set(ModuleIds.Batch, _settings);
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

    private void BtnReloadWorkflows_Click(object sender, RoutedEventArgs e) => ReloadWorkflows();

    private void BtnOpenWorkflowFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.OpenInExplorer(WorkflowDirectory);
            SetStatus($"已打开工作流目录: {WorkflowDirectory}");
        }
        catch (Exception ex)
        {
            SetError($"无法打开工作流目录: {ex.Message}");
        }
    }

    // ==================== seed 输入 ====================

    private void ChkGlobalLockSeed_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSeedControls();
        Persist();
    }

    private void TxtSeed_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSeedControls();
        Persist();
    }

    /// <summary>填了固定 seed（-1 以外的整数）后，随机相关的两个勾选项都不再起作用，置灰避免误解。</summary>
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
        return long.TryParse(text.Trim(), out long seed) && seed != -1;
    }

    private static bool TryParseSeed(string? text, out long? fixedSeed)
    {
        fixedSeed = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!long.TryParse(text.Trim(), out long seed)) return false;

        if (seed != -1) fixedSeed = seed;
        return true;
    }

    // ==================== 拖入 json ====================

    private void WorkflowList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedJsonFiles(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static List<string> GetDroppedJsonFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        if (data.GetData(DataFormats.FileDrop) is not string[] paths) return new List<string>();

        return paths
            .Where(p => File.Exists(p) &&
                        string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void WorkflowList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var jsonFiles = GetDroppedJsonFiles(e.Data);
        if (jsonFiles.Count == 0)
        {
            SetError("请拖入 .json 工作流文件。");
            return;
        }

        string dir = WorkflowDirectory;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            SetError($"无法创建 api 目录: {ex.Message}");
            return;
        }

        var added = new List<string>();
        var failed = new List<string>();
        int skipped = 0;

        foreach (var file in jsonFiles)
        {
            var target = Path.Combine(dir, Path.GetFileName(file));

            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            try
            {
                // 能解析成 JSON 对象才收，防止拖进非 ComfyUI 的 json 把列表搞乱
                JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: 不是有效的 JSON - {ex.Message}");
                continue;
            }

            try
            {
                if (File.Exists(target))
                {
                    if (!UiHost.Confirm($"{Path.GetFileName(target)} 已存在，是否覆盖？", "同名工作流"))
                    {
                        skipped++;
                        continue;
                    }
                }

                File.Copy(file, target, true);
                added.Add(Path.GetFileName(target));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (added.Count > 0)
        {
            ReloadWorkflows();
            // 刚加进来的默认勾选
            foreach (var wf in _workflows.Where(w => added.Contains(w.FileName, StringComparer.OrdinalIgnoreCase)))
                wf.IsEnabled = true;
            SaveSelection();
        }

        if (failed.Count > 0)
        {
            SetError($"已添加 {added.Count} 个，失败 {failed.Count} 个");
            UiHost.Warn(string.Join("\n", failed), "部分工作流添加失败");
            return;
        }

        var extra = skipped > 0 ? $"，跳过 {skipped} 个" : "";
        SetStatus(added.Count > 0 ? $"已添加 {added.Count} 个工作流{extra}" : $"没有新增工作流{extra}");
    }

    // ==================== 提交 ====================

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_workflows.Count == 0)
        {
            SetError("没有可用的工作流 JSON，请检查 api 目录后点「刷新」。");
            return;
        }

        var selected = _workflows.Where(w => w.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            SetError("请至少勾选一个工作流。");
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

        if (!TryParseSeed(TxtSeed.Text, out long? fixedSeed))
        {
            SetError("种子必须是整数（-1 表示保持原来的随机逻辑）。");
            return;
        }

        Persist();

        // 负面词：整个输入框算一条，本批所有图共用；留空则不修改工作流里的负面提示词节点
        string negativePrompt = TxtNegativePrompt.Text.Trim();
        bool lockSeed = ChkLockSeed.IsChecked == true;
        bool globalLockSeed = ChkGlobalLockSeed.IsChecked == true;

        var batchSeeds = new List<long>();
        if (globalLockSeed && fixedSeed is null)
        {
            for (int round = 0; round < perPromptCount; round++)
                batchSeeds.Add(NextSeed());
        }

        var client = new ComfyClient(TxtBaseUrl.Text);

        BtnRun.IsEnabled = false;
        TxtLog.Text = string.Empty;
        SetStatus("正在提交…");
        AppendLog($"本次提交使用 {selected.Count} 个工作流：{string.Join("、", selected.Select(w => w.DisplayName))}");

        try
        {
            // 先探一下服务在不在，避免几十条都白跑一遍
            var queue = await client.GetQueueAsync();
            if (!queue.Reachable)
            {
                SetError($"连不上 ComfyUI：{client.BaseUrl}");
                UiHost.Warn($"连不上 ComfyUI（{client.BaseUrl}）。\n请确认它已经启动，或在顶部改地址。", "ComfyUI 未就绪");
                return;
            }

            int done = 0;
            int failed = 0;

            async System.Threading.Tasks.Task SubmitOne(JsonObject clone, long? seed)
            {
                // seed 为 null 表示不锁定：每个工作流里的种子节点各自随机（与旧版行为一致）
                if (seed.HasValue) ApplySeed(clone, seed.Value);
                else RandomizeSeed(clone);

                var result = await client.SubmitAsync(clone);
                if (result.Ok)
                {
                    done++;
                }
                else
                {
                    failed++;
                    AppendLog($"✗ {result.Message}");
                }
            }

            if (batchSeeds.Count > 0)
            {
                // 全局锁定：外层是「轮」，轮内所有提示词、所有工作流共用一个 seed
                foreach (long roundSeed in batchSeeds)
                {
                    foreach (var prompt in promptLines)
                    {
                        foreach (var wf in selected)
                        {
                            var clone = Clone(wf, prompt, negativePrompt, dateSuffix);
                            await SubmitOne(clone, roundSeed);
                        }
                    }
                }
            }
            else
            {
                foreach (var prompt in promptLines)
                {
                    for (int i = 0; i < perPromptCount; i++)
                    {
                        // 填了固定 seed 时所有图都用它；勾选「锁定 seed」时同一组提示词的所有工作流
                        // 共用一个随机 seed；两者都不生效时传 null，让每个工作流各自随机。
                        long? sharedSeed = fixedSeed ?? (lockSeed ? NextSeed() : null);

                        foreach (var wf in selected)
                        {
                            var clone = Clone(wf, prompt, negativePrompt, dateSuffix);
                            await SubmitOne(clone, sharedSeed);
                        }
                    }
                }
            }

            ShowSubmitted(done, failed, batchSeeds, fixedSeed);
            SetStatus(failed == 0 ? "排队完成" : $"排队完成，{failed} 个失败");
            AppLog.Info($"批量提交：成功 {done} 个，失败 {failed} 个");

            if (failed > 0)
                UiHost.Warn($"{failed} 个任务提交失败，原因见右侧运行日志。", "部分任务失败");
        }
        catch (Exception ex)
        {
            SetError($"出错: {ex.Message}");
            AppLog.Error("批量提交出错", ex);
            UiHost.Error(ex.Message, "提交任务出错");
        }
        finally
        {
            BtnRun.IsEnabled = true;
        }
    }

    private static JsonObject Clone(WorkflowItem workflow, string prompt, string negativePrompt, string dateSuffix)
    {
        var clone = JsonNode.Parse(workflow.Root.ToJsonString())!.AsObject();
        SetPrompt(clone, prompt, PositiveNodeKeyword, PositivePlaceholder);
        if (negativePrompt.Length > 0) SetPrompt(clone, negativePrompt, NegativeNodeKeyword, NegativePlaceholder);
        AppendDateToFilenamePrefix(clone, dateSuffix);
        return clone;
    }

    private void AppendLog(string line)
    {
        TxtLog.Text = TxtLog.Text.Length == 0 ? line : TxtLog.Text + Environment.NewLine + line;
        TxtLog.ScrollToEnd();
    }

    // ==================== 提示词写入 ====================

    /// <summary>模板 text 里标记「队列提示词插在这里」的占位词。</summary>
    private const string PositivePlaceholder = "Positive Prompt";

    /// <summary>负面提示词节点的占位词，处理流程与正向词完全一致。</summary>
    private const string NegativePlaceholder = "Negative Prompt";

    private const string PositiveNodeKeyword = "Positive";
    private const string NegativeNodeKeyword = "Negative";

    /// <summary>
    /// 正向与负面提示词共用同一套写入流程，区别只在节点关键字和占位词：
    /// 1. 文本里带占位符时：只替换占位那一段，模板原有的质量词保留；
    /// 2. 没有占位符时：拼接到模板提示词后面。
    /// </summary>
    private static void SetPrompt(JsonObject root, string prompt, string nodeKeyword, string placeholder)
    {
        if (TrySetPromptNode(root, prompt, nodeKeyword, placeholder)) return;

        // 兜底：标题里没写关键字，但文本里写了占位符的节点（工作流被改过名时不至于漏掉）
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            if (node["inputs"] is not JsonObject inputs ||
                inputs["text"] is not JsonNode textNode) continue;

            if (TryReplacePlaceholder(textNode, placeholder, prompt)) return;
        }
    }

    private static bool TrySetPromptNode(JsonObject root, string prompt, string nodeKeyword, string placeholder)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";
            if (!title.Contains(nodeKeyword, StringComparison.OrdinalIgnoreCase)) continue;
            if (node["inputs"] is not JsonObject inputs ||
                inputs["text"] is not JsonNode textNode) continue;

            if (TryReplacePlaceholder(textNode, placeholder, prompt)) return true;

            string originalText;
            try
            {
                originalText = textNode.GetValue<string>();
            }
            catch (Exception)
            {
                return false; // text 不是字符串（比如被连成了节点）就不碰
            }

            originalText = originalText.Trim().TrimEnd(',', '，').TrimEnd();
            inputs["text"] = string.IsNullOrWhiteSpace(originalText)
                ? prompt
                : $"{originalText}, {prompt}";
            return true;
        }

        return false;
    }

    private static bool TryReplacePlaceholder(JsonNode textNode, string placeholder, string prompt)
    {
        string text;
        try
        {
            text = textNode.GetValue<string>();
        }
        catch (Exception)
        {
            return false;
        }

        int index = text.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;

        var builder = new StringBuilder(text.Length + prompt.Length);
        int start = 0;
        while (index >= 0)
        {
            builder.Append(text, start, index - start);
            builder.Append(prompt);
            start = index + placeholder.Length;
            index = text.IndexOf(placeholder, start, StringComparison.OrdinalIgnoreCase);
        }
        builder.Append(text, start, text.Length - start);

        string finalText = builder.ToString().Trim();
        finalText = finalText.TrimStart(',', '，').Trim();
        finalText = finalText.TrimEnd(',', '，').Trim();

        textNode.ReplaceWith(finalText);
        return true;
    }

    /// <summary>给保存节点的 filename_prefix 末尾追加日期，方便按天归档。</summary>
    private static void AppendDateToFilenamePrefix(JsonObject root, string dateSuffix)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            if (node["inputs"] is not JsonObject inputs) continue;
            if (inputs["filename_prefix"] is not JsonNode prefixNode) continue;

            string prefix;
            try
            {
                prefix = prefixNode.GetValue<string>();
            }
            catch (Exception)
            {
                continue;
            }

            if (prefix.EndsWith(dateSuffix, StringComparison.Ordinal)) continue;
            inputs["filename_prefix"] = prefix + dateSuffix;
        }
    }

    private static void RandomizeSeed(JsonObject root)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var field = SeedFieldName(node);
            if (field is null) continue;

            if (node["inputs"] is JsonObject inputs && inputs.ContainsKey(field))
                inputs[field] = NextSeed();
        }
    }

    private static void ApplySeed(JsonObject root, long seed)
    {
        foreach (var kv in root)
        {
            if (kv.Value is not JsonObject node) continue;
            var field = SeedFieldName(node);
            if (field is null) continue;

            if (node["inputs"] is JsonObject inputs && inputs.ContainsKey(field))
                inputs[field] = seed;
        }
    }

    private static string? SeedFieldName(JsonObject node)
    {
        var classType = node["class_type"]?.GetValue<string>() ?? "";
        return classType switch
        {
            "KSampler" => "seed",
            // KSamplerAdvanced 的输入字段是 noise_seed（不是 seed）
            "KSamplerAdvanced" or "RandomNoise" => "noise_seed",
            _ => null
        };
    }

    private static long NextSeed() => Random.Shared.NextInt64(0, long.MaxValue);

    // ==================== 拖入 txt ====================

    private void Prompt_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
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
            UiHost.Warn(string.Join("\n", failed), "部分文件读取失败");
        }
    }

    /// <summary>正向队列当前内容（供自检核对投递结果）。</summary>
    public string PromptQueueText => TxtPromptQueue.Text;

    /// <summary>外部（其他模块）送来的提示词追加到正向队列。</summary>
    public void AppendPositive(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (string line in ParsePromptLines(text))
        {
            var existing = TxtPromptQueue.Text.TrimEnd();
            TxtPromptQueue.Text = existing.Length == 0 ? line : existing + Environment.NewLine + line;
        }
        TxtPromptQueue.CaretIndex = TxtPromptQueue.Text.Length;
        TxtPromptQueue.ScrollToEnd();
        SetStatus("已追加外部提示词到队列。");
    }

    /// <summary>外部送来的负向提示词覆盖负向框。</summary>
    public void SetNegative(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        TxtNegativePrompt.Text = text.Trim();
        SetStatus("已填入负向提示词。");
    }

    private static IEnumerable<string> ParsePromptLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n')
               .Split('\n')
               .Select(l => l.Trim())
               .Where(l => l.Length > 0);

    /// <summary>读取文本文件，自动识别 UTF-8 / UTF-16(BOM) / GB18030(ANSI 中文)。</summary>
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

    private static Encoding Gb18030
    {
        get
        {
            if (_gbEncoding != null) return _gbEncoding;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gbEncoding = Encoding.GetEncoding(54936);
            return _gbEncoding;
        }
    }

    // ==================== 状态与日志 ====================

    private void SetStatus(string msg)
    {
        TxtStatus.Text = msg;
        TxtStatus.Foreground = Brushes.Gray;
    }

    private void SetError(string msg)
    {
        TxtStatus.Text = msg;
        TxtStatus.Foreground = Brushes.Firebrick;
    }

    private void ShowSubmitted(int done, int failed, IReadOnlyList<long> batchSeeds, long? fixedSeed)
    {
        string fixedText = fixedSeed is not null ? $"seed 固定为 {fixedSeed}" : "";
        string failText = failed > 0 ? $"，失败 {failed} 个" : "";

        if (batchSeeds.Count == 0)
        {
            AppendLog(fixedText.Length == 0
                ? $"全部任务已提交，共 {done} 个{failText}。"
                : $"全部任务已提交，共 {done} 个{failText}，{fixedText}。");
            return;
        }

        string seedText = batchSeeds.Count <= 10
            ? string.Join(", ", batchSeeds)
            : string.Join(", ", batchSeeds.Take(10)) + " …";

        AppendLog($"全部任务已提交，共 {done} 个{failText}。共 {batchSeeds.Count} 轮，每轮全局 seed: {seedText}");
    }
}
