using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DrawBench.Modules.Batch.Controls;

/// <summary>
/// 带实时搜索过滤的 LoRA 下拉选择控件，支持按文件路径和全局别名双向搜索。
/// </summary>
public partial class LoraPicker : UserControl
{
    private sealed record PickerOption(string Value, string Display, string SearchText);

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable<string>),
            typeof(LoraPicker),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedLoraProperty =
        DependencyProperty.Register(
            nameof(SelectedLora),
            typeof(string),
            typeof(LoraPicker),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedLoraChanged));

    public static readonly DependencyProperty AliasMapProperty =
        DependencyProperty.Register(
            nameof(AliasMap),
            typeof(IReadOnlyDictionary<string, string>),
            typeof(LoraPicker),
            new PropertyMetadata(null, OnAliasMapChanged));

    private INotifyCollectionChanged? _subscribedCollection;

    public LoraPicker()
    {
        InitializeComponent();
        UpdateDisplayText();
    }

    public IEnumerable<string>? ItemsSource
    {
        get => (IEnumerable<string>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string SelectedLora
    {
        get => (string)GetValue(SelectedLoraProperty);
        set => SetValue(SelectedLoraProperty, value ?? string.Empty);
    }

    public IReadOnlyDictionary<string, string>? AliasMap
    {
        get => (IReadOnlyDictionary<string, string>?)GetValue(AliasMapProperty);
        set => SetValue(AliasMapProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (LoraPicker)d;
        if (picker._subscribedCollection is not null)
        {
            picker._subscribedCollection.CollectionChanged -= picker.OnSourceCollectionChanged;
            picker._subscribedCollection = null;
        }

        if (e.NewValue is INotifyCollectionChanged ncc)
        {
            picker._subscribedCollection = ncc;
            ncc.CollectionChanged += picker.OnSourceCollectionChanged;
        }

        if (picker.PopupDropDown.IsOpen)
            picker.RefreshFilterList();
    }

    private static void OnAliasMapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (LoraPicker)d;
        picker.UpdateDisplayText();
        if (picker.PopupDropDown.IsOpen)
            picker.RefreshFilterList();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (PopupDropDown.IsOpen)
            RefreshFilterList();
    }

    private static void OnSelectedLoraChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LoraPicker)d).UpdateDisplayText();
    }

    private string FormatDisplay(string loraName)
    {
        if (AliasMap is not null && AliasMap.TryGetValue(loraName, out var alias) && !string.IsNullOrWhiteSpace(alias))
            return $"[{alias}] {loraName}";
        return loraName;
    }

    private void UpdateDisplayText()
    {
        string value = (SelectedLora ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            TxtDisplay.Text = "请选择 LoRA…";
            TxtDisplay.Foreground = TryFindResource("Muted") as Brush ?? Brushes.Gray;
        }
        else
        {
            TxtDisplay.Text = FormatDisplay(value);
            TxtDisplay.Foreground = TryFindResource("Ink") as Brush ?? Brushes.Black;
        }
    }

    private void BtnToggle_Checked(object sender, RoutedEventArgs e)
    {
        PopupDropDown.IsOpen = true;
    }

    private void BtnToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        PopupDropDown.IsOpen = false;
    }

    private void PopupDropDown_Opened(object? sender, EventArgs e)
    {
        TxtFilter.Text = string.Empty;
        RefreshFilterList();
        TxtFilter.Focus();
    }

    private void PopupDropDown_Closed(object? sender, EventArgs e)
    {
        BtnToggle.IsChecked = false;
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtFilterPlaceholder.Visibility = string.IsNullOrEmpty(TxtFilter.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshFilterList();
    }

    private void RefreshFilterList()
    {
        var all = (ItemsSource ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s =>
            {
                string trimmed = s.Trim();
                string display = FormatDisplay(trimmed);
                return new PickerOption(trimmed, display, display);
            })
            .ToList();

        string query = (TxtFilter.Text ?? string.Empty).Trim();

        List<PickerOption> filtered;
        if (query.Length == 0)
        {
            filtered = all;
        }
        else
        {
            var tokens = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            filtered = all
                .Where(item => tokens.All(t => item.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        LstFiltered.ItemsSource = filtered;

        if (filtered.Count == 0)
            TxtCountHint.Text = all.Count == 0 ? "暂无可选 LoRA（请先刷新 ComfyUI 列表）" : "没有匹配的 LoRA";
        else if (query.Length > 0)
            TxtCountHint.Text = $"匹配 {filtered.Count} / {all.Count} 个 LoRA";
        else
            TxtCountHint.Text = $"共 {all.Count} 个可选 LoRA";

        if (!string.IsNullOrWhiteSpace(SelectedLora))
        {
            var match = filtered.FirstOrDefault(o => string.Equals(o.Value, SelectedLora.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                LstFiltered.SelectedItem = match;
                LstFiltered.ScrollIntoView(match);
            }
        }
    }

    private void TxtFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PopupDropDown.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && LstFiltered.Items.Count > 0)
        {
            LstFiltered.SelectedIndex = Math.Max(0, LstFiltered.SelectedIndex);
            LstFiltered.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && LstFiltered.Items.Count > 0)
        {
            var chosen = LstFiltered.SelectedItem as PickerOption ?? LstFiltered.Items[0] as PickerOption;
            if (chosen is not null)
                CommitSelection(chosen.Value);
            e.Handled = true;
        }
    }

    private void LstFiltered_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (LstFiltered.SelectedItem is PickerOption chosen)
            CommitSelection(chosen.Value);
    }

    private void LstFiltered_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LstFiltered.SelectedItem is PickerOption chosen)
        {
            CommitSelection(chosen.Value);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PopupDropDown.IsOpen = false;
            e.Handled = true;
        }
    }

    private void CommitSelection(string loraName)
    {
        SelectedLora = loraName;
        PopupDropDown.IsOpen = false;
    }
}
