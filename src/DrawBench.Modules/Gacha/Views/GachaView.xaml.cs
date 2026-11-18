using System;
using System.Collections.Generic;
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
    private sealed record ComboOption(string Value, string Label);

    private static readonly string[] FallbackHairLengths =
        { "short_hair", "medium_hair", "long_hair", "very_long_hair", "absurdly_long_hair" };

    private static readonly string[] FallbackBreastsSizes =
        { "small_breasts", "breasts", "medium_breasts", "large_breasts", "huge_breasts", "gigantic_breasts" };

    private static readonly Dictionary<string, string> TagChineseLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["short_hair"] = "短发",
        ["medium_hair"] = "中长发",
        ["long_hair"] = "长发",
        ["very_long_hair"] = "超长发",
        ["absurdly_long_hair"] = "极长发",
        ["small_breasts"] = "贫乳",
        ["breasts"] = "常规",
        ["medium_breasts"] = "中等胸部",
        ["large_breasts"] = "巨乳",
        ["huge_breasts"] = "爆乳",
        ["gigantic_breasts"] = "超巨乳",
    };

    private readonly string _engineDir;

    public GachaView()
    {
        InitializeComponent();

        _engineDir = AppPaths.Current.EngineDir;
        PopulateDropdowns();

        var saved = SettingsStore.Shared.Get<GachaOptions>(ModuleIds.Gacha);
        if (saved != null)
            ApplyOptions(saved);
        else
            ApplyOptions(new GachaOptions());

        string bankPath = Path.Combine(_engineDir, GachaEngine.BankFileName);
        if (!File.Exists(bankPath))
        {
            TxtLog.Text = $"找不到词库：未在 engine\\ 下发现 {GachaEngine.BankFileName}（{bankPath}）";
        }
    }

    private void PopulateDropdowns()
    {
        FillCombo(CmbHairLength, "hair", "hair_length", FallbackHairLengths);
        FillCombo(CmbBreastsSize, "body_parts", "breasts_size", FallbackBreastsSizes);

        CmbAttireMode.ItemsSource = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.RandomChoice, "随机"),
            new(GachaOptions.OutfitsChoice, "套装"),
            new(GachaOptions.TopsBottomsChoice, "上下装"),
        };
        CmbAttireMode.SelectedValue = GachaOptions.RandomChoice;

        CmbActionMode.ItemsSource = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.RandomChoice, "随机"),
            new(GachaOptions.PostureChoice, "全身姿势"),
            new(GachaOptions.GesturesChoice, "手势"),
        };
        CmbActionMode.SelectedValue = GachaOptions.PostureChoice;

        CmbSceneMode.ItemsSource = new List<ComboOption>
        {
            new(GachaOptions.NoneChoice, "不指定"),
            new(GachaOptions.RandomChoice, "随机"),
            new(GachaOptions.LocationsChoice, "地点环境"),
            new(GachaOptions.BackgroundsChoice, "纯白背景"),
        };
        CmbSceneMode.SelectedValue = GachaOptions.LocationsChoice;
    }

    private void FillCombo(ComboBox combo, string module, string subCategory, string[] fallback)
    {
        var tags = GachaEngine.GetSlotSfwTags(_engineDir, module, subCategory);
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

        combo.ItemsSource = items;
        combo.SelectedValue = GachaOptions.RandomChoice;
    }

    private static void SetComboValue(ComboBox combo, string? value)
    {
        string target = value ?? GachaOptions.NoneChoice;
        combo.SelectedValue = target;
        if (combo.SelectedIndex < 0)
            combo.SelectedValue = GachaOptions.NoneChoice;
    }

    private static string GetComboValue(ComboBox combo) =>
        combo.SelectedValue as string ?? GachaOptions.NoneChoice;

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
        ChkNsfw.IsChecked = o.IncludeNsfw;
        ChkMultiPerson.IsChecked = o.AllowMultiPerson;
        TxtPrefix.Text = o.PrefixTags ?? "";
        TxtSuffix.Text = o.SuffixTags ?? "";

        ChkHairColor.IsChecked = o.HairColor;
        SetComboValue(CmbHairLength, o.SelectedHairLength);
        ChkHairStyles.IsChecked = o.HairStyles;
        ChkHairAccessories.IsChecked = o.HairAccessories;

        ChkEyeColor.IsChecked = o.EyeColor;
        ChkEyes.IsChecked = o.Eyes;
        ChkFace.IsChecked = o.FaceAndExpression;
        ChkEarsRare.IsChecked = o.EarsRare && !o.Ears;
        ChkEars.IsChecked = o.Ears;

        SetComboValue(CmbBreastsSize, o.SelectedBreastsSize);
        ChkBodyDetails.IsChecked = o.BodyDetails;

        SetComboValue(CmbAttireMode, o.AttireMode);
        ChkLegwear.IsChecked = o.Legwear;
        ChkFootwear.IsChecked = o.Footwear;
        ChkAccessories.IsChecked = o.Accessories;

        SetComboValue(CmbActionMode, o.ActionMode);

        SetComboValue(CmbSceneMode, o.SceneMode);
    }

    private GachaOptions CollectOptions() => new()
    {
        Repeat = IntOf(TxtRepeat, 1, 1, 99),
        ReplaceUnderscoreWithSpace = ChkSpace.IsChecked == true,
        IncludeNsfw = ChkNsfw.IsChecked == true,
        AllowMultiPerson = ChkMultiPerson.IsChecked == true,
        PrefixTags = TxtPrefix.Text ?? "",
        SuffixTags = TxtSuffix.Text ?? "",

        HairColor = ChkHairColor.IsChecked == true,
        SelectedHairLength = GetComboValue(CmbHairLength),
        HairStyles = ChkHairStyles.IsChecked == true,
        HairAccessories = ChkHairAccessories.IsChecked == true,

        EyeColor = ChkEyeColor.IsChecked == true,
        Eyes = ChkEyes.IsChecked == true,
        FaceAndExpression = ChkFace.IsChecked == true,
        EarsRare = ChkEarsRare.IsChecked == true,
        Ears = ChkEars.IsChecked == true,

        SelectedBreastsSize = GetComboValue(CmbBreastsSize),
        BodyDetails = ChkBodyDetails.IsChecked == true,

        AttireMode = GetComboValue(CmbAttireMode),
        Legwear = ChkLegwear.IsChecked == true,
        Footwear = ChkFootwear.IsChecked == true,
        Accessories = ChkAccessories.IsChecked == true,

        ActionMode = GetComboValue(CmbActionMode),

        SceneMode = GetComboValue(CmbSceneMode),
    };

    // ==================== 快捷方案 ====================

    private void ApplyPreset(Action<GachaOptions> configure)
    {
        var d = new GachaOptions
        {
            Repeat = IntOf(TxtRepeat, 1, 1, 99),
            ReplaceUnderscoreWithSpace = ChkSpace.IsChecked == true,
            IncludeNsfw = ChkNsfw.IsChecked == true,
            AllowMultiPerson = ChkMultiPerson.IsChecked == true,
            PrefixTags = TxtPrefix.Text ?? "",
            SuffixTags = TxtSuffix.Text ?? "",
            HairColor = false,
            SelectedHairLength = GachaOptions.NoneChoice,
            HairStyles = false,
            HairAccessories = false,
            EyeColor = false,
            Eyes = false,
            FaceAndExpression = false,
            EarsRare = false,
            Ears = false,
            SelectedBreastsSize = GachaOptions.NoneChoice,
            BodyDetails = false,
            AttireMode = GachaOptions.NoneChoice,
            Accessories = false,
            Legwear = false,
            Footwear = false,
            ActionMode = GachaOptions.NoneChoice,
            SceneMode = GachaOptions.NoneChoice,
        };
        configure(d);
        ApplyOptions(d);
    }

    // 常规全身
    private void OnPresetStandard(object sender, RoutedEventArgs e)
    {
        ApplyPreset(o =>
        {
            o.SelectedHairLength = GachaOptions.RandomChoice;
            o.SelectedBreastsSize = GachaOptions.NoneChoice;
            o.AttireMode = GachaOptions.RandomChoice;
            o.ActionMode = GachaOptions.RandomChoice;
            o.SceneMode = GachaOptions.LocationsChoice;

            o.HairColor = true;
            o.HairStyles = true;
            o.EyeColor = true;
            o.Eyes = true;
            o.FaceAndExpression = true;
            o.EarsRare = true;

            o.Legwear = true;
            o.Footwear = true;
        });
    }

    // 人设外貌
    private void OnPresetCharacterOnly(object sender, RoutedEventArgs e)
    {
        ApplyPreset(o =>
        {
            o.SelectedHairLength = GachaOptions.RandomChoice;
            o.SelectedBreastsSize = GachaOptions.RandomChoice;
            o.AttireMode = GachaOptions.TopsBottomsChoice;
            o.ActionMode = GachaOptions.GesturesChoice;
            o.SceneMode = GachaOptions.BackgroundsChoice;

            o.HairColor = true;
            o.HairStyles = true;
            o.HairAccessories = true;
            o.EyeColor = true;
            o.Eyes = true;
            o.FaceAndExpression = true;
            o.EarsRare = true;

            o.Legwear = true;
            o.Footwear = true;
        });
    }

    // 服装展示
    private void OnPresetOutfit(object sender, RoutedEventArgs e)
    {
        ApplyPreset(o =>
        {
            o.SelectedHairLength = GachaOptions.NoneChoice;
            o.SelectedBreastsSize = GachaOptions.NoneChoice;
            o.AttireMode = GachaOptions.RandomChoice;
            o.ActionMode = GachaOptions.PostureChoice;
            o.SceneMode = GachaOptions.RandomChoice;

            o.HairColor = true;
            o.HairStyles = true;
            o.EarsRare = true;

            o.Accessories = true;
            o.Legwear = true;
            o.Footwear = true;
        });
    }

    private void OnPresetClear(object sender, RoutedEventArgs e)
    {
        SetAllSubChecks(false);
    }

    private void SetAllSubChecks(bool value)
    {
        string comboChoice = value ? GachaOptions.RandomChoice : GachaOptions.NoneChoice;

        ChkHairColor.IsChecked = value;
        SetComboValue(CmbHairLength, comboChoice);
        ChkHairStyles.IsChecked = value;
        ChkHairAccessories.IsChecked = value;

        ChkEyeColor.IsChecked = value;
        ChkEyes.IsChecked = value;
        ChkFace.IsChecked = value;
        ChkEarsRare.IsChecked = false;
        ChkEars.IsChecked = value;

        SetComboValue(CmbBreastsSize, comboChoice);
        ChkBodyDetails.IsChecked = value;

        SetComboValue(CmbAttireMode, comboChoice);
        ChkLegwear.IsChecked = value;
        ChkFootwear.IsChecked = value;
        ChkAccessories.IsChecked = value;

        SetComboValue(CmbActionMode, comboChoice);

        SetComboValue(CmbSceneMode, comboChoice);
    }

    private void OnEarsToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ChkEars == null || ChkEarsRare == null) return;
        if (ReferenceEquals(sender, ChkEars) && ChkEars.IsChecked == true)
            ChkEarsRare.IsChecked = false;
        else if (ReferenceEquals(sender, ChkEarsRare) && ChkEarsRare.IsChecked == true)
            ChkEars.IsChecked = false;
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
