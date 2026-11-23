using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DrawBench.Modules.Batch.Views;

/// <summary>
/// 自定义 LoRA 分类管理弹窗：支持添加、重命名、排序与删除，并记录改名/删除映射以同步到所有 LoRA。
/// </summary>
public partial class CategoryManagerDialog : Window
{
    private sealed class CategoryEntry : INotifyPropertyChanged
    {
        private string _name = string.Empty;

        public string? OriginalName { get; init; }

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<CategoryEntry> _entries = new();
    private readonly HashSet<string> _deletedOriginals = new(StringComparer.OrdinalIgnoreCase);

    public CategoryManagerDialog(Window? owner, IEnumerable<string> categories)
    {
        InitializeComponent();

        if (owner is not null && owner.IsVisible)
            Owner = owner;
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

        foreach (var cat in categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            _entries.Add(new CategoryEntry { OriginalName = cat, Name = cat });

        LstCategories.ItemsSource = _entries;
        UpdateEmptyState();
    }

    public List<string> ResultCategories { get; private set; } = new();
    public Dictionary<string, string> RenamedMap { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeletedSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void TxtNewCategory_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtNewCategoryHint.Visibility = string.IsNullOrEmpty(TxtNewCategory.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TxtNewCategory_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddNewCategory();
            e.Handled = true;
        }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e) => AddNewCategory();

    private void AddNewCategory()
    {
        string name = (TxtNewCategory.Text ?? string.Empty).Trim();
        if (name.Length == 0) return;

        if (string.Equals(name, "全部", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "未分类", StringComparison.OrdinalIgnoreCase))
        {
            ConfirmDialog.Warn(this, $"「{name}」为系统保留名称，请换一个分类名。", "名称不可用");
            return;
        }

        if (_entries.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ConfirmDialog.Warn(this, $"分类「{name}」已存在。", "重复分类");
            return;
        }

        var entry = new CategoryEntry { OriginalName = null, Name = name };
        _entries.Add(entry);
        TxtNewCategory.Clear();
        LstCategories.SelectedItem = entry;
        UpdateEmptyState();
    }

    private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = LstCategories.SelectedItem as CategoryEntry;
        bool has = selected is not null;
        TxtRename.IsEnabled = has;
        BtnRename.IsEnabled = has;
        BtnUp.IsEnabled = has;
        BtnDown.IsEnabled = has;
        BtnDelete.IsEnabled = has;
        TxtRename.Text = selected?.Name ?? string.Empty;
    }

    private void TxtRename_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RenameSelected();
            e.Handled = true;
        }
    }

    private void BtnRename_Click(object sender, RoutedEventArgs e) => RenameSelected();

    private void RenameSelected()
    {
        if (LstCategories.SelectedItem is not CategoryEntry selected) return;
        string next = (TxtRename.Text ?? string.Empty).Trim();
        if (next.Length == 0)
        {
            ConfirmDialog.Warn(this, "分类名称不能为空。", "名称无效");
            return;
        }

        if (string.Equals(next, "全部", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(next, "未分类", StringComparison.OrdinalIgnoreCase))
        {
            ConfirmDialog.Warn(this, $"「{next}」为系统保留名称，请换一个分类名。", "名称不可用");
            return;
        }

        if (_entries.Any(x => !ReferenceEquals(x, selected) && string.Equals(x.Name, next, StringComparison.OrdinalIgnoreCase)))
        {
            ConfirmDialog.Warn(this, $"分类「{next}」已存在。", "重复分类");
            return;
        }

        selected.Name = next;
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e)
    {
        if (LstCategories.SelectedItem is not CategoryEntry selected) return;
        int idx = _entries.IndexOf(selected);
        if (idx <= 0) return;
        _entries.Move(idx, idx - 1);
        LstCategories.SelectedItem = selected;
    }

    private void BtnDown_Click(object sender, RoutedEventArgs e)
    {
        if (LstCategories.SelectedItem is not CategoryEntry selected) return;
        int idx = _entries.IndexOf(selected);
        if (idx < 0 || idx >= _entries.Count - 1) return;
        _entries.Move(idx, idx + 1);
        LstCategories.SelectedItem = selected;
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (LstCategories.SelectedItem is not CategoryEntry selected) return;
        int idx = _entries.IndexOf(selected);
        if (!string.IsNullOrEmpty(selected.OriginalName))
            _deletedOriginals.Add(selected.OriginalName);

        _entries.RemoveAt(idx);
        if (_entries.Count > 0)
            LstCategories.SelectedIndex = Math.Min(idx, _entries.Count - 1);
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        TxtEmpty.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        ResultCategories = _entries.Select(x => x.Name).ToList();
        RenamedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (!string.IsNullOrEmpty(entry.OriginalName) &&
                !string.Equals(entry.OriginalName, entry.Name, StringComparison.Ordinal))
            {
                RenamedMap[entry.OriginalName] = entry.Name;
            }
        }
        DeletedSet = new HashSet<string>(_deletedOriginals, StringComparer.OrdinalIgnoreCase);

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
