using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DrawBench.Core;
using DrawBench.Core.Logging;
using DrawBench.Core.Ui;

namespace DrawBench.Modules.Gacha.Views;

/// <summary>
/// 模块化抽卡界面：读取 engine\tagGroup_modular.json，按模块开关抽取提示词，
/// 并通过 AiBridge / SharedContext 与「批量提交」模块互通。
/// </summary>
public partial class GachaView : UserControl
{
    private readonly string _engineDir;

    public GachaView()
    {
        InitializeComponent();

        var saved = SettingsStore.Shared.Get<GachaOptions>(ModuleIds.Gacha);
        if (saved != null) ApplyOptions(saved);
        SyncNsfwPanelState();

        _engineDir = AppPaths.Current.EngineDir;
        string bankPath = Path.Combine(_engineDir, GachaEngine.BankFileName);
        if (!File.Exists(bankPath))
        {
            TxtLog.Text = $"找不到词库：未在 engine\\ 下发现 {GachaEngine.BankFileName}（{bankPath}）";
        }
    }

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Shared.Set(ModuleIds.Gacha, CollectOptions());
        }
        catch
        {
            // 保存失败不阻塞
        }
    }

    private void ApplyOptions(GachaOptions o)
    {
        TxtRepeat.Text = Math.Clamp(o.Repeat, 1, 99).ToString();
        ChkSpace.IsChecked = o.ReplaceUnderscoreWithSpace;
        TxtPrefix.Text = o.PrefixTags ?? "";
        TxtSuffix.Text = o.SuffixTags ?? "";

        ChkHairColor.IsChecked = o.HairColor;
        ChkHairStyles.IsChecked = o.HairStyles;
        ChkHairAccessories.IsChecked = o.HairAccessories;
        ChkHairTraits.IsChecked = o.HairTraitsAndActions;

        ChkEyes.IsChecked = o.Eyes;
        ChkFace.IsChecked = o.FaceAndExpression;
        ChkEars.IsChecked = o.Ears;

        ChkBreasts.IsChecked = o.Breasts;
        ChkAss.IsChecked = o.Ass;
        ChkHands.IsChecked = o.Hands;
        ChkFeet.IsChecked = o.Feet;
        ChkShoulders.IsChecked = o.Shoulders;
        ChkBodyOther.IsChecked = o.OtherBodyParts;

        ChkFashionStyle.IsChecked = o.FashionStyle;
        ChkClothing.IsChecked = o.Clothing;
        ChkHeadwear.IsChecked = o.Headwear;
        ChkNeckwear.IsChecked = o.Neckwear;
        ChkSleeves.IsChecked = o.Sleeves;
        ChkHandwear.IsChecked = o.Handwear;
        ChkLegwear.IsChecked = o.Legwear;
        ChkAccessories.IsChecked = o.Accessories;

        ChkPosture.IsChecked = o.Posture;
        ChkGestures.IsChecked = o.Gestures;
        ChkDances.IsChecked = o.Dances;
        ChkHolding.IsChecked = o.Holding;
        ChkVerbs.IsChecked = o.VerbsAndActions;

        ChkLocations.IsChecked = o.Locations;
        ChkBackgrounds.IsChecked = o.Backgrounds;
        ChkLighting.IsChecked = o.Lighting;
        ChkComposition.IsChecked = o.ImageComposition;

        ChkEnableNsfw.IsChecked = o.EnableNsfw;
        ChkNsfwNudity.IsChecked = o.NsfwNudityAndCovering;
        ChkNsfwAttire.IsChecked = o.NsfwSexualAttire;
        ChkNsfwActs.IsChecked = o.NsfwSexActs;
        ChkNsfwPositions.IsChecked = o.NsfwSexualPositions;
        ChkNsfwObjects.IsChecked = o.NsfwSexObjects;
    }

    private GachaOptions CollectOptions() => new()
    {
        Repeat = IntOf(TxtRepeat, 1, 1, 99),
        ReplaceUnderscoreWithSpace = ChkSpace.IsChecked == true,
        PrefixTags = TxtPrefix.Text ?? "",
        SuffixTags = TxtSuffix.Text ?? "",

        HairColor = ChkHairColor.IsChecked == true,
        HairStyles = ChkHairStyles.IsChecked == true,
        HairAccessories = ChkHairAccessories.IsChecked == true,
        HairTraitsAndActions = ChkHairTraits.IsChecked == true,

        Eyes = ChkEyes.IsChecked == true,
        FaceAndExpression = ChkFace.IsChecked == true,
        Ears = ChkEars.IsChecked == true,

        Breasts = ChkBreasts.IsChecked == true,
        Ass = ChkAss.IsChecked == true,
        Hands = ChkHands.IsChecked == true,
        Feet = ChkFeet.IsChecked == true,
        Shoulders = ChkShoulders.IsChecked == true,
        OtherBodyParts = ChkBodyOther.IsChecked == true,

        FashionStyle = ChkFashionStyle.IsChecked == true,
        Clothing = ChkClothing.IsChecked == true,
        Headwear = ChkHeadwear.IsChecked == true,
        Neckwear = ChkNeckwear.IsChecked == true,
        Sleeves = ChkSleeves.IsChecked == true,
        Handwear = ChkHandwear.IsChecked == true,
        Legwear = ChkLegwear.IsChecked == true,
        Accessories = ChkAccessories.IsChecked == true,

        Posture = ChkPosture.IsChecked == true,
        Gestures = ChkGestures.IsChecked == true,
        Dances = ChkDances.IsChecked == true,
        Holding = ChkHolding.IsChecked == true,
        VerbsAndActions = ChkVerbs.IsChecked == true,

        Locations = ChkLocations.IsChecked == true,
        Backgrounds = ChkBackgrounds.IsChecked == true,
        Lighting = ChkLighting.IsChecked == true,
        ImageComposition = ChkComposition.IsChecked == true,

        EnableNsfw = ChkEnableNsfw.IsChecked == true,
        NsfwNudityAndCovering = ChkNsfwNudity.IsChecked == true,
        NsfwSexualAttire = ChkNsfwAttire.IsChecked == true,
        NsfwSexActs = ChkNsfwActs.IsChecked == true,
        NsfwSexualPositions = ChkNsfwPositions.IsChecked == true,
        NsfwSexObjects = ChkNsfwObjects.IsChecked == true,
    };

    // ==================== 快捷方案 ====================

    private void OnPresetStandard(object sender, RoutedEventArgs e)
    {
        var d = new GachaOptions
        {
            Repeat = IntOf(TxtRepeat, 1, 1, 99),
            ReplaceUnderscoreWithSpace = ChkSpace.IsChecked == true,
            PrefixTags = TxtPrefix.Text ?? "",
            SuffixTags = TxtSuffix.Text ?? "",
        };
        ApplyOptions(d);
        SyncNsfwPanelState();
    }

    private void OnPresetCharacterOnly(object sender, RoutedEventArgs e)
    {
        SetAllSubChecks(false);
        ChkHairColor.IsChecked = true;
        ChkHairStyles.IsChecked = true;
        ChkHairAccessories.IsChecked = true;
        ChkEyes.IsChecked = true;
        ChkFace.IsChecked = true;
        ChkFashionStyle.IsChecked = true;
        ChkClothing.IsChecked = true;
        ChkLegwear.IsChecked = true;
        SyncNsfwPanelState();
    }

    private void OnPresetAllSfw(object sender, RoutedEventArgs e)
    {
        SetAllSubChecks(true);
        ChkEnableNsfw.IsChecked = false;
        ChkNsfwNudity.IsChecked = false;
        ChkNsfwAttire.IsChecked = false;
        ChkNsfwActs.IsChecked = false;
        ChkNsfwPositions.IsChecked = false;
        ChkNsfwObjects.IsChecked = false;
        SyncNsfwPanelState();
    }

    private void OnPresetClear(object sender, RoutedEventArgs e)
    {
        SetAllSubChecks(false);
        ChkEnableNsfw.IsChecked = false;
        SyncNsfwPanelState();
    }

    private void SetAllSubChecks(bool value)
    {
        ChkHairColor.IsChecked = value;
        ChkHairStyles.IsChecked = value;
        ChkHairAccessories.IsChecked = value;
        ChkHairTraits.IsChecked = value;

        ChkEyes.IsChecked = value;
        ChkFace.IsChecked = value;
        ChkEars.IsChecked = value;

        ChkBreasts.IsChecked = value;
        ChkAss.IsChecked = value;
        ChkHands.IsChecked = value;
        ChkFeet.IsChecked = value;
        ChkShoulders.IsChecked = value;
        ChkBodyOther.IsChecked = value;

        ChkFashionStyle.IsChecked = value;
        ChkClothing.IsChecked = value;
        ChkHeadwear.IsChecked = value;
        ChkNeckwear.IsChecked = value;
        ChkSleeves.IsChecked = value;
        ChkHandwear.IsChecked = value;
        ChkLegwear.IsChecked = value;
        ChkAccessories.IsChecked = value;

        ChkPosture.IsChecked = value;
        ChkGestures.IsChecked = value;
        ChkDances.IsChecked = value;
        ChkHolding.IsChecked = value;
        ChkVerbs.IsChecked = value;

        ChkLocations.IsChecked = value;
        ChkBackgrounds.IsChecked = false;
        ChkLighting.IsChecked = value;
        ChkComposition.IsChecked = value;
    }

    private void OnSceneBgToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ChkLocations == null || ChkBackgrounds == null) return;
        if (ReferenceEquals(sender, ChkLocations) && ChkLocations.IsChecked == true)
            ChkBackgrounds.IsChecked = false;
        else if (ReferenceEquals(sender, ChkBackgrounds) && ChkBackgrounds.IsChecked == true)
            ChkLocations.IsChecked = false;
    }

    private void OnNsfwToggleChanged(object sender, RoutedEventArgs e) => SyncNsfwPanelState();

    private void SyncNsfwPanelState()
    {
        if (PanelNsfwSubs != null && ChkEnableNsfw != null)
            PanelNsfwSubs.IsEnabled = ChkEnableNsfw.IsChecked == true;
    }

    // ==================== 抽卡 ====================

    private void OnRoll(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var opts = CollectOptions();
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
            TxtLog.Text = outcome.Failure ?? "抽卡未产生结果";
            TxtResult.Text = "";
            TxtMeta.Text = "";
            TxtStatus.Text = "失败：" + FirstLine(outcome.Failure);
            return;
        }

        TxtResult.Text = outcome.AllText;
        TxtLog.Text = outcome.AllReport;
        TxtMeta.Text = outcome.Items.Count + " 条";
        TxtStatus.Text = $"完成 · {sw.ElapsedMilliseconds} ms";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        TxtResult.Text = "";
        TxtLog.Text = "";
        TxtMeta.Text = "";
        TxtStatus.Text = "已清空";
    }

    // ==================== 输出区与模块间通讯 ====================

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
            FileName = "gacha_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt",
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

    /// <summary>清空批量提交队列，把当前结果送进去并跳转到批量提交页。</summary>
    private void OnSendToBatch(object sender, RoutedEventArgs e)
    {
        string text = TxtResult.Text ?? "";
        if (text.Trim().Length == 0)
        {
            UiHost.Info("还没有抽卡结果可以发送。");
            return;
        }

        if (AiBridge.SendToBatchQueue(text, "抽卡"))
        {
            TxtStatus.Text = "已发送到批量提交队列";
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
        string text = TxtResult.Text ?? "";
        if (text.Trim().Length == 0)
        {
            UiHost.Info("还没有抽卡结果可以追加。");
            return;
        }

        if (AiBridge.AppendToBatchQueue(text, "抽卡"))
            TxtStatus.Text = "已追加到批量提交队列";
        else
            UiHost.Warn("批量提交模块没有接收这段文本。");
    }

    private static int IntOf(TextBox box, int fallback, int min, int max)
    {
        if (!int.TryParse((box.Text ?? "").Trim(), out int v)) return fallback;
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
