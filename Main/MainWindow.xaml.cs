using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Main;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        CleanupLogsIfNeeded();
    }

    private static void CleanupLogsIfNeeded()
    {
        try
        {
            var logsDir = Path.Combine(Environment.CurrentDirectory, "logs");
            if (!Directory.Exists(logsDir))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var filePath in Directory.EnumerateFiles(logsDir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(filePath);
                if (string.Equals(fileName, ".keep", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(filePath);
                if (info.LastWriteTime < cutoff)
                {
                    info.Delete();
                }
            }
        }
        catch
        {
            // 避免清理失敗影響程式啟動
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnWindowMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (source == null)
        {
            DragMove();
            return;
        }

        if (IsInsideInteractiveControl(source) || IsInsideWhiteCard(source))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInsideInteractiveControl(DependencyObject source)
    {
        var current = source;
        while (current != null)
        {
            if (current is ButtonBase || current is TextBoxBase || current is ComboBox || current is ScrollBar)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideWhiteCard(DependencyObject source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Border border && border.Background is SolidColorBrush brush)
            {
                if (brush.Color == Colors.White)
                {
                    return true;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
