using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DrawBench.Core;
using DrawBench.Core.Logging;
using DrawBench.Core.Python;
using DrawBench.Core.Ui;

namespace DrawBench.Modules.Gacha.Views;

/// <summary>
/// 抽卡模块：调 engine\anima_cli.py 抽角色特征与装扮。
/// 与旧版的区别：Python 探测走统一 PythonLocator、引擎目录走 AppPaths.EngineDir、
/// 结果多两个「发送到队列」入口。
/// </summary>
public partial class GachaView : UserControl
{
    private CancellationTokenSource? _cts;
    private string _engineDir = "";

    public GachaView()
    {
        InitializeComponent();

        _engineDir = AppPaths.Current.EngineDir;
        if (!File.Exists(Path.Combine(_engineDir, "anima_cli.py")))
        {
            AppendLog("找不到引擎：未在程序目录的 engine\\ 下发现 anima_cli.py（" + _engineDir + "）");
        }
    }

    private PythonInterpreter CurrentPython()
    {
        var app = SettingsStore.Shared.Get<AppSection>("app");
        return PythonLocator.Resolve(app?.PythonPath);
    }

    // ==================== 抽卡 ====================

    private AnimaOptions CollectOptions() => new()
    {
        Level = CmbText(CmbLevel),
        Repeat = IntOf(TxtRepeat, 1, 1, 99),
        Pool = CmbText(CmbPool),
        Count = IntOf(CmbText(CmbCount), 1, 1, 3),
        Relation = CmbText(CmbRelation),
        HugeBreasts = ChkHuge.IsChecked == true,
        NoAvoidRepeat = ChkNoAvoid.IsChecked == true,
        NoOutfit = ChkNoOutfit.IsChecked == true,
        NoPose = ChkNoPose.IsChecked == true,
        NoExpression = ChkNoExpr.IsChecked == true,
        NoScene = ChkNoScene.IsChecked == true,
        AllowPublic = ChkAllowPublic.IsChecked == true,
    };

    private async void OnGirl(object sender, RoutedEventArgs e)
    {
        var o = CollectOptions();
        await DrawAsync(ct => ExecAsync("girl", o, null, ct));
    }

    private async void OnRoll(object sender, RoutedEventArgs e)
    {
        var o = CollectOptions();
        await DrawAsync(ct => ExecAsync("roll", o, null, ct));
    }

    /// <summary>
    /// girl + roll：抽到几组角色就配几套装扮，一组一块日志、一条提示词。
    /// 逐条往下走 —— 先出「抽到谁」，紧接着出这组的服装/姿势/表情/场所。
    /// </summary>
    private async void OnGirlRoll(object sender, RoutedEventArgs e)
    {
        var o = CollectOptions();
        var rollOnce = o.Clone();      // 每组角色只配一条 roll
        rollOnce.Repeat = 1;

        await DrawAsync(async ct =>
        {
            var g = await ExecAsync("girl", o, null, ct, false);
            if (g.Failure != null || g.Items.Count == 0) return g;   // 失败原样返回

            var merged = new RunOutcome();
            int total = g.Items.Count;

            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) break;

                var gi = g.Items[i];
                WriteLog(StepTitle(i, total), LogText.Tidy(LogText.Clean(gi.Report)));

                if (!gi.Ok || string.IsNullOrEmpty(gi.ClaimJson))
                {
                    AppendLines("girl 没有产出可接力的状态，已停止。");
                    break;
                }

                var r = await ExecAsync("roll", rollOnce, gi.ClaimJson, ct, false);
                if (r.Failure != null)
                {
                    AppendLines(r.Failure);
                    if (merged.Items.Count == 0) return r;
                    break;
                }

                // 接在同一块里 —— 上面是抽到谁，下面是这组穿什么
                AppendLines(r.AllReport);
                merged.Items.AddRange(r.Items);
            }

            if (merged.Items.Count == 0) return merged;   // 交给 DrawAsync 统一报「没有拿到结果」
            return merged;
        });
    }

    private static string? StepTitle(int index, int total)
        => total > 1 ? "—— 第 " + (index + 1) + " 条 ——" : null;

    /// <summary>清空日志 → 跑一次抽卡流程 → 把结果摊到界面上。</summary>
    private async Task DrawAsync(Func<CancellationToken, Task<RunOutcome>> body)
    {
        if (!File.Exists(Path.Combine(_engineDir, "anima_cli.py")))
        {
            UiHost.Warn("软件目录下没有 anima_cli.py，引擎不完整。\n期望位置：" +
                        Path.Combine(_engineDir, "anima_cli.py"), "抽卡");
            return;
        }

        var python = CurrentPython();
        if (!python.Found)
        {
            UiHost.Warn("找不到 Python。\n抽卡需要本机 Python 3.8 以上版本，请安装并加入 PATH，" +
                        "或在「设置」里手动指定解释器。", "找不到 Python");
            return;
        }

        TxtLog.Text = "";

        var cts = new CancellationTokenSource();
        _cts = cts;
        SetBusy(true);
        var sw = Stopwatch.StartNew();

        RunOutcome outcome;
        try
        {
            outcome = await body(cts.Token);
        }
        catch (Exception ex)
        {
            outcome = new RunOutcome { Failure = ex.Message };
            AppLog.Error("抽卡出错", ex);
        }
        finally
        {
            sw.Stop();
            SetBusy(false);
            _cts = null;
            cts.Dispose();
        }

        if (outcome.Failure != null)
        {
            AppendLog(outcome.Failure);
            TxtResult.Text = "";
            TxtMeta.Text = "";
            TxtStatus.Text = "失败：" + FirstLine(outcome.Failure);
            return;
        }

        if (outcome.Items.Count == 0)
        {
            TxtResult.Text = "";
            TxtMeta.Text = "";
            TxtStatus.Text = "没有拿到结果";
            return;
        }

        var last = outcome.Items[^1];
        if (!last.Ok)
        {
            TxtResult.Text = "";
            TxtMeta.Text = "";
            TxtStatus.Text = "抽取失败：" + FirstLine(last.Report);
            return;
        }

        TxtResult.Text = outcome.AllText;
        TxtMeta.Text = outcome.Items.Count + " 条";
        TxtStatus.Text = "完成 · " + sw.ElapsedMilliseconds + " ms";
    }

    /// <summary>调一次引擎。writeLog=false 时由调用方自己组织日志（girl+roll 逐条配对用）。</summary>
    private async Task<RunOutcome> ExecAsync(string cmd, AnimaOptions o,
                                             string? claimInJson, CancellationToken ct,
                                             bool writeLog = true)
    {
        string runDir = Path.Combine(Path.GetTempPath(), "DrawBench");
        Directory.CreateDirectory(runDir);
        string claimOut = Path.Combine(runDir, "claim_" + cmd + ".json");
        string claimIn = Path.Combine(runDir, "claim_in_" + cmd + ".json");

        if (File.Exists(claimOut)) File.Delete(claimOut);
        if (File.Exists(claimIn)) File.Delete(claimIn);

        string? claimInPath = null;
        if (!string.IsNullOrEmpty(claimInJson))
        {
            File.WriteAllText(claimIn, claimInJson, new UTF8Encoding(false));
            claimInPath = claimIn;
        }

        var args = o.ToArgs(cmd, claimInPath, claimOut);

        var outcome = await GachaEngine.RunAsync(CurrentPython(), _engineDir, args, ct);
        if (writeLog) WriteLog(null, outcome.Failure ?? outcome.AllReport);
        return outcome;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch { }
        AppendLog("（已请求取消）");
    }

    // ==================== 输出区 ====================

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        string text = TxtResult.Text ?? "";
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            TxtStatus.Text = "已复制 " + text.Length + " 字符";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string text = TxtResult.Text ?? "";
        if (text.Length == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存提示词",
            Filter = "文本文件|*.txt|所有文件|*.*",
            FileName = "anima_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
        };

        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
                TxtStatus.Text = "已保存：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                UiHost.Error("保存失败：" + ex.Message, "抽卡");
            }
        }
    }

    /// <summary>把当前结果直接送进批量提交模块的队列。</summary>
    private void OnSendToBatch(object sender, RoutedEventArgs e)
    {
        string text = TxtResult.Text ?? "";
        if (text.Trim().Length == 0)
        {
            UiHost.Info("还没有抽卡结果可以发送。");
            return;
        }

        if (AiBridge.SendToBatchQueue(text, "抽卡"))
            TxtStatus.Text = "已发送到批量提交队列";
        else
            UiHost.Warn("批量提交模块没有接收这段文本。");
    }

    /// <summary>起一个新日志块：可选标题（—— 第 N 条 ——）+ 若干行正文，块之间空一行。</summary>
    private void WriteLog(string? title, params string?[] lines)
    {
        var sb = new StringBuilder();
        if (TxtLog.Text.Length > 0) sb.AppendLine();
        if (!string.IsNullOrEmpty(title)) sb.AppendLine(title);
        foreach (string? l in lines)
            if (!string.IsNullOrEmpty(l)) sb.AppendLine(l);
        if (sb.Length == 0) return;
        TxtLog.AppendText(sb.ToString());
        TxtLog.ScrollToEnd();
    }

    /// <summary>往当前这块日志下面接着写，不加空行、不加标题。</summary>
    private void AppendLines(params string?[] lines)
    {
        var sb = new StringBuilder();
        foreach (string? l in lines)
            if (!string.IsNullOrEmpty(l)) sb.AppendLine(l);
        if (sb.Length == 0) return;
        if (TxtLog.Text.Length > 0 && !TxtLog.Text.EndsWith("\n", StringComparison.Ordinal))
            sb.Insert(0, Environment.NewLine);
        TxtLog.AppendText(sb.ToString());
        TxtLog.ScrollToEnd();
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        AppendLines(text);
    }

    private void SetBusy(bool busy)
    {
        BtnGirl.IsEnabled = !busy;
        BtnGirlRoll.IsEnabled = !busy;
        BtnRoll.IsEnabled = !busy;
        BtnCancel.IsEnabled = busy;
        TxtStatus.Text = busy ? "抽卡中…" : TxtStatus.Text;
    }

    // ==================== 小工具 ====================

    private static string CmbText(ComboBox cmb)
    {
        if (cmb.SelectedItem is ComboBoxItem item)
        {
            string? tag = item.Tag as string;      // 显示名和取值不同时用 Tag
            if (!string.IsNullOrEmpty(tag)) return tag;
            return Convert.ToString(item.Content) ?? "";
        }
        return Convert.ToString(cmb.SelectedItem) ?? "";
    }

    private static int IntOf(TextBox box, int fallback, int min, int max)
    {
        if (!int.TryParse((box.Text ?? "").Trim(), out int v)) return fallback;
        if (v < min) v = min;
        if (v > max) v = max;
        return v;
    }

    private static int IntOf(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse((text ?? "").Trim(), out int v)) return fallback;
        if (v < min) v = min;
        if (v > max) v = max;
        return v;
    }

    private static string FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOf('\n');
        return i < 0 ? s : s.Substring(0, i).TrimEnd();
    }
}
