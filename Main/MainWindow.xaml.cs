using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
    private bool _suppressNextWorkerExitError;

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
        StatusText.Text = "啟動中...";
        var updated = TryUpdateMaxPages(selected);
        if (!updated)
        {
            StatusText.Text = "設定檔寫入失敗，改用暫時參數啟動";
        }

        if (!TryStartWorker(updateMode: "all", progressEvery: progressEvery, maxPages: selected))
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
        StatusText.Text = "啟動中...";
        var updated = TryUpdateMaxPages(selected);
        if (!updated)
        {
            StatusText.Text = "設定檔寫入失敗，改用暫時參數啟動";
        }

        if (!TryStartWorker(updateMode: "ranking", progressEvery: progressEvery, maxPages: selected))
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

            _suppressNextWorkerExitError = true;
            _workerProcess.Kill(true);
            StatusText.Text = "已終止執行中的工作";
            ProgressText.Text = "進度：已終止";
        }
        catch
        {
            _suppressNextWorkerExitError = false;
            StatusText.Text = "終止失敗";
        }
    }

    private void OnUpdatePcLinkClick(object sender, RoutedEventArgs e)
    {
        var progressEvery = ProgressEveryCombo.SelectedItem as int? ?? 10;
        StatusText.Text = "啟動中...";
        if (!TryStartWorker(updateMode: "pc-link", progressEvery: progressEvery, maxPages: null))
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

    private bool TryStartWorker(string updateMode, int progressEvery, int? maxPages)
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

            if (_workerProcess != null && !_workerProcess.HasExited)
            {
                StatusText.Text = "已有執行中的工作";
                MessageBox.Show("目前已有執行中的工作，請先按「清除」或等待完成。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var errorBuffer = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run --project PcSalesWorker",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["RUN_ONCE"] = "1";
            psi.Environment["UPDATE_MODE"] = updateMode;
            psi.Environment["PROGRESS_EVERY"] = progressEvery.ToString();
            psi.Environment["BRING_BROWSER_FRONT"] = "1";
            if (maxPages.HasValue)
            {
                psi.Environment["APP__SEARCH__MAXPAGES"] = maxPages.Value.ToString();
            }

            _workerProcess = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };
            _workerProcess.OutputDataReceived += (_, _) => { };
            _workerProcess.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                lock (errorBuffer)
                {
                    if (errorBuffer.Length < 4000)
                    {
                        errorBuffer.AppendLine(args.Data);
                    }
                }
            };
            _workerProcess.Exited += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_workerProcess == null)
                    {
                        return;
                    }

                    if (_suppressNextWorkerExitError)
                    {
                        _suppressNextWorkerExitError = false;
                        return;
                    }

                    if (_workerProcess.ExitCode == 0)
                    {
                        return;
                    }

                    StatusText.Text = $"更新程序異常結束（{_workerProcess.ExitCode}）";
                    var detail = ReadErrorPreview(errorBuffer);
                    if (string.IsNullOrWhiteSpace(detail))
                    {
                        MessageBox.Show($"更新程序異常結束（ExitCode={_workerProcess.ExitCode}）。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    MessageBox.Show($"更新程序異常結束（ExitCode={_workerProcess.ExitCode}）：{Environment.NewLine}{detail}", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            };

            if (!_workerProcess.Start())
            {
                MessageBox.Show("無法啟動更新程序。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            _workerProcess.BeginOutputReadLine();
            _workerProcess.BeginErrorReadLine();

            try
            {
                _workerProcess.WaitForExit(300);
            }
            catch
            {
                // ignore
            }

            if (_workerProcess.HasExited)
            {
                var detail = ReadErrorPreview(errorBuffer);
                if (string.IsNullOrWhiteSpace(detail))
                {
                    MessageBox.Show($"更新程序啟動後立即結束（ExitCode={_workerProcess.ExitCode}）。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                MessageBox.Show($"更新程序啟動後立即結束（ExitCode={_workerProcess.ExitCode}）：{Environment.NewLine}{detail}", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

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

    private static string ReadErrorPreview(StringBuilder errorBuffer)
    {
        lock (errorBuffer)
        {
            if (errorBuffer.Length == 0)
            {
                return string.Empty;
            }

            var lines = errorBuffer.ToString()
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(8);
            return string.Join(Environment.NewLine, lines);
        }
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
