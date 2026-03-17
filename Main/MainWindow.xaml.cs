using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Main;

public partial class MainWindow : Window
{
    private Process? _workerProcess;
    private readonly DispatcherTimer _progressTimer;
    private string? _progressFilePath;

    public MainWindow()
    {
        InitializeComponent();
        CleanupLogsIfNeeded();
        InitializePageOptions();
        _progressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _progressTimer.Tick += OnProgressTick;
        _progressFilePath = ResolveProgressPath();
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

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        var selected = MaxPagesCombo.SelectedItem as int? ?? 20;
        var progressEvery = ProgressEveryCombo.SelectedItem as int? ?? 10;
        if (!TryUpdateMaxPages(selected))
        {
            StatusText.Text = "更新設定失敗";
            return;
        }

        if (!TryStartWorker(updateMode: "all", progressEvery: progressEvery))
        {
            StatusText.Text = "啟動失敗";
            return;
        }

        StatusText.Text = $"已啟動（最大頁數 {selected}）";
        StartProgressTimer();
    }

    private void OnUpdateRankingClick(object sender, RoutedEventArgs e)
    {
        var selected = MaxPagesCombo.SelectedItem as int? ?? 20;
        var progressEvery = ProgressEveryCombo.SelectedItem as int? ?? 10;
        if (!TryUpdateMaxPages(selected))
        {
            StatusText.Text = "更新設定失敗";
            return;
        }

        if (!TryStartWorker(updateMode: "ranking", progressEvery: progressEvery))
        {
            StatusText.Text = "啟動失敗";
            return;
        }

        StatusText.Text = $"更新排名（最大頁數 {selected}）";
        StartProgressTimer();
    }

    private void OnTerminateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_workerProcess == null || _workerProcess.HasExited)
            {
                StatusText.Text = "目前沒有執行中的工作";
                return;
            }

            _workerProcess.Kill(true);
            StatusText.Text = "已終止執行中的工作";
            ProgressText.Text = "進度：已終止";
        }
        catch
        {
            StatusText.Text = "終止失敗";
        }
    }

    private void OnUpdatePcLinkClick(object sender, RoutedEventArgs e)
    {
        var progressEvery = ProgressEveryCombo.SelectedItem as int? ?? 10;
        if (!TryStartWorker(updateMode: "pc-link", progressEvery: progressEvery))
        {
            StatusText.Text = "啟動失敗";
            return;
        }

        StatusText.Text = "更新PC連結中";
        ProgressText.Text = "進度：處理中";
        StartProgressTimer();
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

    private void InitializePageOptions()
    {
        MaxPagesCombo.ItemsSource = Enumerable.Range(1, 100).ToList();
        var current = TryReadMaxPages();
        if (current is >= 1 and <= 100)
        {
            MaxPagesCombo.SelectedItem = current;
        }
        else
        {
            MaxPagesCombo.SelectedItem = 20;
        }

        ProgressEveryCombo.ItemsSource = Enumerable.Range(1, 100).ToList();
        ProgressEveryCombo.SelectedItem = 10;
    }

    private int? TryReadMaxPages()
    {
        try
        {
            var path = ResolveAppSettingsPath();
            if (path == null || !File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var node = JsonNode.Parse(json);
            return node?["App"]?["Search"]?["MaxPages"]?.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }

    private bool TryUpdateMaxPages(int maxPages)
    {
        try
        {
            var path = ResolveAppSettingsPath();
            if (path == null || !File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path);
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null)
            {
                return false;
            }

            var app = node["App"] as JsonObject ?? new JsonObject();
            var search = app["Search"] as JsonObject ?? new JsonObject();
            search["MaxPages"] = maxPages;
            app["Search"] = search;
            node["App"] = app;

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, node.ToJsonString(options));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryStartWorker(string updateMode, int progressEvery)
    {
        try
        {
            var root = ResolveProjectRoot();
            if (root == null)
            {
                StatusText.Text = "找不到專案路徑";
                MessageBox.Show("找不到專案路徑，無法啟動。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c cd /d \"{root}\" && set RUN_ONCE=1 && set UPDATE_MODE={updateMode} && set PROGRESS_EVERY={progressEvery} && set BRING_BROWSER_FRONT=1 && dotnet run --project PcSalesWorker",
                WorkingDirectory = root,
                UseShellExecute = true,
                CreateNoWindow = false
            };

            _workerProcess = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "啟動失敗";
            MessageBox.Show($"啟動失敗：{ex.Message}", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void StartProgressTimer()
    {
        if (_progressTimer.IsEnabled)
        {
            return;
        }

        _progressTimer.Start();
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        try
        {
            if (_workerProcess != null && _workerProcess.HasExited)
            {
                _progressTimer.Stop();
            }

            if (string.IsNullOrWhiteSpace(_progressFilePath) || !File.Exists(_progressFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_progressFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<ProgressState>(json);
            if (state == null)
            {
                return;
            }

            if (string.Equals(state.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                ProgressText.Text = "進度：已完成";
                return;
            }

            if (state.Total <= 0)
            {
                ProgressText.Text = "進度：準備中";
                return;
            }

            var displayId = string.IsNullOrWhiteSpace(state.ProductId) ? string.Empty : $"（{state.ProductId}）";
            ProgressText.Text = $"進度：第 {state.Processed} / {state.Total} 列{displayId}";
        }
        catch
        {
            // ignore
        }
    }

    private static string? ResolveProgressPath()
    {
        var root = ResolveProjectRoot();
        if (root == null)
        {
            return null;
        }

        return Path.Combine(root, "logs", "progress.json");
    }

    private static string? ResolveAppSettingsPath()
    {
        var root = ResolveProjectRoot();
        if (root == null)
        {
            return null;
        }

        return Path.Combine(root, "PcSalesWorker", "appsettings.json");
    }

    private static string? ResolveProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "PcSalesWorker");
            if (Directory.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private sealed class ProgressState
    {
        public int Total { get; set; }
        public int Processed { get; set; }
        public int RowIndex { get; set; }
        public string? ProductId { get; set; }
        public string? Status { get; set; }
    }
}
