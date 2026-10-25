using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PadMapper.App.Services;

/// <summary>
/// ページの中のリストや複数行の入力欄がマウスホイールを握りつぶして、ページ全体がスクロールしなくなるのを防ぐ。
/// 中身がそれ以上スクロールできないときは、ホイールを外側（ページ）に渡す。
/// </summary>
internal static class WheelForwarding
{
    public static void Register()
    {
        var handler = new MouseWheelEventHandler(OnPreviewMouseWheel);
        EventManager.RegisterClassHandler(typeof(ListBox), UIElement.PreviewMouseWheelEvent, handler);
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseWheelEvent, handler);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement element) return;
        var inner = FindScrollViewer(element);
        if (inner != null && inner.ScrollableHeight > 0)
        {
            bool atTop = inner.VerticalOffset <= 0;
            bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom)) return; // 中身をスクロールする
        }
        if (VisualTreeHelper.GetParent(element) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = element,
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
