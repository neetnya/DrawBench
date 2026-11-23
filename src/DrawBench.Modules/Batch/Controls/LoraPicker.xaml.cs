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
/// 带实时搜索与自定义分类筛选的 LoRA 下拉选择控件，支持按路径、别名和分类搜索。
/// 点击窗口任意空白区域、失焦或再次点击下拉按钮均自动收起。
/// </summary>
public partial class LoraPicker : UserControl
{
    private const string AllCategoryToken = "__ALL__";
    private const string UncategorizedToken = "__NONE__";

    private sealed record PickerOption(string Value, string Display, string SearchText, string Category, string Tooltip);

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
            new PropertyMetadata(null, OnMetadataChanged));

    public static readonly DependencyProperty CategoryMapProperty =
        DependencyProperty.Register(
            nameof(CategoryMap),
            typeof(IReadOnlyDictionary<string, string>),
            typeof(LoraPicker),
            new PropertyMetadata(null, OnMetadataChanged));

    public static readonly DependencyProperty CategoriesProperty =
        DependencyProperty.Register(
            nameof(Categories),
            typeof(IEnumerable<string>),
            typeof(LoraPicker),
            new PropertyMetadata(null, OnMetadataChanged));

    private INotifyCollectionChanged? _subscribedCollection;
    private Window? _hookedWindow;
    private MouseButtonEventHandler? _windowMouseDownHandler;
    private string _activeCategoryFilter = AllCategoryToken;

    public LoraPicker()
    {
        InitializeComponent();
        UpdateDisplayText();
        Unloaded += (_, _) => UnhookOwnerWindow();
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

    public IReadOnlyDictionary<string, string>? CategoryMap
    {
        get => (IReadOnlyDictionary<string, string>?)GetValue(CategoryMapProperty);
        set => SetValue(CategoryMapProperty, value);
    }

    public IEnumerable<string>? Categories
    {
        get => (IEnumerable<string>?)GetValue(CategoriesProperty);
        set => SetValue(CategoriesProperty, value);
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

    private static void OnMetadataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (LoraPicker)d;
        picker.UpdateDisplayText();
        if (picker.PopupDropDown.IsOpen)
        {
            picker.RebuildCategoryPills();
            picker.RefreshFilterList();
        }
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
            return alias.Trim();
        return loraName;
    }

    private string GetCategory(string loraName)
    {
        if (CategoryMap is not null && CategoryMap.TryGetValue(loraName, out var cat) && !string.IsNullOrWhiteSpace(cat))
            return cat.Trim();
        return string.Empty;
    }

    private string BuildSearchText(string loraName, string category)
    {
        string alias = AliasMap is not null && AliasMap.TryGetValue(loraName, out var a) ? a.Trim() : string.Empty;
        return $"{category} {alias} {loraName}".Trim();
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

    private void BtnToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PopupDropDown.IsOpen)
        {
            PopupDropDown.IsOpen = false;
            BtnToggle.IsChecked = false;
            e.Handled = true;
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
        HookOwnerWindow();
        _activeCategoryFilter = AllCategoryToken;
        TxtFilter.Text = string.Empty;
        RebuildCategoryPills();
        RefreshFilterList();
        TxtFilter.Focus();
    }

    private void PopupDropDown_Closed(object? sender, EventArgs e)
    {
        UnhookOwnerWindow();
        BtnToggle.IsChecked = false;
    }

    private void RebuildCategoryPills()
    {
        PnlCategoryPills.Children.Clear();
        var cats = (Categories ?? Enumerable.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cats.Count == 0)
        {
            PnlCategoryPills.Visibility = Visibility.Collapsed;
            return;
        }

        PnlCategoryPills.Visibility = Visibility.Visible;
        AddPill("全部", AllCategoryToken);
        foreach (var cat in cats)
            AddPill(cat, cat);
        AddPill("未分类", UncategorizedToken);
    }

    private void AddPill(string label, string token)
    {
        bool isActive = string.Equals(_activeCategoryFilter, token, StringComparison.OrdinalIgnoreCase);
        var accentSoft = TryFindResource("AccentSoft") as Brush ?? Brushes.LightBlue;
        var accentDeep = TryFindResource("AccentDeep") as Brush ?? Brushes.Blue;
        var line = TryFindResource("Line") as Brush ?? Brushes.LightGray;
        var inkSoft = TryFindResource("InkSoft") as Brush ?? Brushes.DimGray;

        var btn = new Button
        {
            Content = label,
            Tag = token,
            FontSize = 11.5,
            Padding = new Thickness(8, 2, 8, 2),
            MinHeight = 22,
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            Background = isActive ? accentSoft : Brushes.Transparent,
            Foreground = isActive ? accentDeep : inkSoft,
            BorderBrush = isActive ? accentDeep : line,
            BorderThickness = new Thickness(1)
        };
        btn.Click += (_, _) =>
        {
            _activeCategoryFilter = token;
            RebuildCategoryPills();
            RefreshFilterList();
            TxtFilter.Focus();
        };
        PnlCategoryPills.Children.Add(btn);
    }

    private void HookOwnerWindow()
    {
        UnhookOwnerWindow();
        var win = Window.GetWindow(this);
        if (win is null) return;

        _hookedWindow = win;
        _windowMouseDownHandler = OnOwnerWindowPreviewMouseDown;
        win.AddHandler(UIElement.PreviewMouseDownEvent, _windowMouseDownHandler, handledEventsToo: true);
        win.Deactivated += OnOwnerWindowClosePopup;
        win.LocationChanged += OnOwnerWindowClosePopup;
        win.SizeChanged += OnOwnerWindowClosePopup;
    }

    private void UnhookOwnerWindow()
    {
        if (_hookedWindow is null) return;

        if (_windowMouseDownHandler is not null)
            _hookedWindow.RemoveHandler(UIElement.PreviewMouseDownEvent, _windowMouseDownHandler);
        _hookedWindow.Deactivated -= OnOwnerWindowClosePopup;
        _hookedWindow.LocationChanged -= OnOwnerWindowClosePopup;
        _hookedWindow.SizeChanged -= OnOwnerWindowClosePopup;

        _hookedWindow = null;
        _windowMouseDownHandler = null;
    }

    private void OnOwnerWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!PopupDropDown.IsOpen) return;

        if (e.OriginalSource is DependencyObject src)
        {
            if (IsDescendantOf(src, PopupBorder) || IsDescendantOf(src, BtnToggle))
                return;
        }

        PopupDropDown.IsOpen = false;
    }

    private void OnOwnerWindowClosePopup(object? sender, EventArgs e)
    {
        if (PopupDropDown.IsOpen)
            PopupDropDown.IsOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
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
                string category = GetCategory(trimmed);
                string display = FormatDisplay(trimmed);
                string searchText = BuildSearchText(trimmed, category);
                string tooltip = category.Length > 0 ? $"[{category}] {trimmed}" : trimmed;
                return new PickerOption(trimmed, display, searchText, category, tooltip);
            })
            .ToList();

        IEnumerable<PickerOption> queryable = all;

        // 分类筛选
        if (string.Equals(_activeCategoryFilter, UncategorizedToken, StringComparison.Ordinal))
        {
            queryable = queryable.Where(o => o.Category.Length == 0);
        }
        else if (!string.Equals(_activeCategoryFilter, AllCategoryToken, StringComparison.Ordinal))
        {
            queryable = queryable.Where(o => string.Equals(o.Category, _activeCategoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        // 关键字筛选
        string query = (TxtFilter.Text ?? string.Empty).Trim();
        if (query.Length > 0)
        {
            var tokens = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            queryable = queryable.Where(item => tokens.All(t => item.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }

        var filtered = queryable.ToList();
        LstFiltered.ItemsSource = filtered;

        bool isFiltered = query.Length > 0 || !string.Equals(_activeCategoryFilter, AllCategoryToken, StringComparison.Ordinal);
        if (filtered.Count == 0)
            TxtCountHint.Text = all.Count == 0 ? "暂无可选 LoRA（请先刷新 ComfyUI 列表）" : "没有匹配的 LoRA";
        else if (isFiltered)
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
