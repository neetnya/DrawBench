using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DrawBench.Core;
using DrawBench.Core.Logging;
using DrawBench.Core.Ui;
using DrawBench.Modules.Batch.Views;

namespace DrawBench.Modules.Gacha.Views;

/// <summary>
/// 模块化抽卡界面：读取 engine\tagGroup_modular.json，按模块开关抽取提示词，
/// 支持用户自定义抽卡预设（新建、改名保存、删除），并通过 AiBridge 与「批量提交」互通。
/// </summary>
public partial class GachaView : UserControl
{
    private readonly string _engineDir;
    private readonly GachaViewModel _vm;

    public GachaView()
    {
        InitializeComponent();

        _engineDir = AppPaths.Current.EngineDir;
        _vm = new GachaViewModel(_engineDir, ExecuteRoll);
        DataContext = _vm;

        var saved = SettingsStore.Shared.Get<GachaOptions>(ModuleIds.Gacha);
        _vm.LoadOptions(saved ?? new GachaOptions());

        string bankPath = Path.Combine(_engineDir, GachaEngine.BankFileName);
        if (!File.Exists(bankPath))
        {
            _vm.LogText = $"找不到词库：未在 engine\\ 下发现 {GachaEngine.BankFileName}（{bankPath}）";
        }
    }

    public void SaveSettings() => SaveSettings(_vm.ToOptions());

    private static void SaveSettings(GachaOptions options)
    {
        try
        {
            SettingsStore.Shared.Set(ModuleIds.Gacha, options);
        }
        catch (Exception ex)
        {
            AppLog.Error("保存抽卡设置失败", ex);
        }
    }

    // ==================== 连抽输入校验 ====================

    private void OnRepeatPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void OnRepeatPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        string pasted = (e.DataObject.GetData(DataFormats.Text) as string ?? "").Trim();
        if (pasted.Length == 0)
        {
            e.CancelCommand();
            return;
        }

        foreach (char c in pasted)
        {
            if (!char.IsDigit(c))
            {
                e.CancelCommand();
                return;
            }
        }
    }

    private void OnRepeatLostFocus(object sender, RoutedEventArgs e) => _vm.NormalizeRepeat();

    // ==================== 自定义预设 ====================

    private void OnPresetNew(object sender, RoutedEventArgs e)
    {
        _vm.CreateNewPreset();
        SaveSettings();
        TxtPresetName.Focus();
        TxtPresetName.SelectAll();
    }

    private void OnPresetSave(object sender, RoutedEventArgs e)
    {
        if (!_vm.SaveCurrentPreset(out string msg))
        {
            ConfirmDialog.Warn(Window.GetWindow(this), msg, "无法保存预设");
            TxtPresetName.Focus();
            return;
        }
        SaveSettings();
    }

    private void OnPresetDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedPreset is null) return;
        bool ok = ConfirmDialog.ConfirmDelete(
            Window.GetWindow(this),
            $"确定删除抽卡预设「{_vm.SelectedPreset.Name}」吗？",
            "删除预设");
        if (!ok) return;

        _vm.DeleteCurrentPreset();
        SaveSettings();
    }

    private void OnPresetClear(object sender, RoutedEventArgs e) => _vm.ClearSelections();

    // ==================== 抽卡 ====================

    private void ExecuteRoll()
    {
        var opts = _vm.ToOptions();
        SaveSettings(opts);

        var sw = Stopwatch.StartNew();
        GachaRunOutcome outcome;
        try
        {
            outcome = GachaEngine.Roll(opts, _engineDir);
        }
        catch (Exception ex)
        {
            outcome = new GachaRunOutcome { Failure = ex.Message };
            AppLog.Error("抽卡出错", ex);
        }
        finally
        {
            sw.Stop();
        }

        if (!outcome.Ok)
        {
            _vm.LogText = outcome.Failure ?? "抽卡未产生结果";
            _vm.ResultText = "";
            _vm.MetaText = "";
            _vm.StatusText = "失败：" + FirstLine(outcome.Failure);
            return;
        }

        _vm.ResultText = outcome.AllText;
        _vm.LogText = outcome.AllReport;
        _vm.MetaText = outcome.Items.Count + " 条";
        _vm.StatusText = $"完成 · {sw.ElapsedMilliseconds} ms";
    }

    private void OnClear(object sender, RoutedEventArgs e) => _vm.ClearOutput();

    // ==================== 输出区与模块间通讯 ====================

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        string text = _vm.ResultText;
        if (text.Length == 0) return;

        try
        {
            CopyToClipboardWithRetry(text);
            _vm.StatusText = "已复制 " + text.Length + " 字符";
        }
        catch (Exception ex)
        {
            AppLog.Error("复制提示词失败", ex);
            _vm.StatusText = "复制失败：" + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string text = _vm.ResultText;
        if (text.Length == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存提示词",
            Filter = "文本文件|*.txt|所有文件|*.*",
            FileName = "gacha_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
                _vm.StatusText = "已保存：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                AppLog.Error("保存提示词失败", ex);
                UiHost.Error("保存失败：" + ex.Message, "抽卡");
            }
        }
    }

    /// <summary>清空批量提交队列，把当前结果送进去并跳转到批量提交页。</summary>
    private void OnSendToBatch(object sender, RoutedEventArgs e)
    {
        string text = _vm.ResultText;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (AiBridge.SendToBatchQueue(text, "抽卡"))
        {
            _vm.StatusText = "已发送到批量提交队列";
            SharedContext.Current.RequestModule(ModuleIds.Batch);
        }
        else
        {
            UiHost.Warn("批量提交模块没有接收这段文本。");
        }
    }

    /// <summary>把当前结果追加到批量提交队列末尾，不清空也不跳转。</summary>
    private void OnAppendToBatch(object sender, RoutedEventArgs e)
    {
        string text = _vm.ResultText;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (AiBridge.AppendToBatchQueue(text, "抽卡"))
            _vm.StatusText = "已追加到批量提交队列";
        else
            UiHost.Warn("批量提交模块没有接收这段文本。");
    }

    private static void CopyToClipboardWithRetry(string text, int maxAttempts = 3)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(20);
            }
        }
    }

    private static string FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOf('\n');
        return i < 0 ? s : s.Substring(0, i).TrimEnd();
    }
}
