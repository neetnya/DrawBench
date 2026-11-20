using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DrawBench.Modules.Batch.Views;

/// <summary>
/// 与软件全局 Theme.xaml 风格统一的自绘确认/提示弹窗，替代原生 MessageBox。
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(Window? owner, string title, string message, string confirmText, bool isDanger, bool showCancel)
    {
        InitializeComponent();

        if (owner is not null && owner.IsVisible)
            Owner = owner;
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

        TxtTitle.Text = title;
        TxtMessage.Text = message;
        BtnConfirm.Content = confirmText;
        BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        if (isDanger)
        {
            if (TryFindResource("DangerButton") is Style dangerStyle)
                BtnConfirm.Style = dangerStyle;
            if (TryFindResource("Danger") is Brush dangerBrush)
                AccentBar.Background = dangerBrush;
        }
    }

    public static bool ConfirmDelete(Window? owner, string message, string title = "删除确认", string confirmText = "删除")
    {
        var dlg = new ConfirmDialog(owner, title, message, confirmText, isDanger: true, showCancel: true);
        return dlg.ShowDialog() == true;
    }

    public static void Warn(Window? owner, string message, string title = "提示")
    {
        var dlg = new ConfirmDialog(owner, title, message, "知道了", isDanger: false, showCancel: false);
        dlg.ShowDialog();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
