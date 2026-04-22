using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
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
    private readonly Queue<StartupTask> _startupQueue = new();
    private string? _progressFilePath;
    private bool _suppressNextWorkerExitError;
    private bool _windowInitialized;
    private bool _autoStartupRunning;
    private int _autoStartupProgressEvery = 10;
    private bool _autoCloseAfterAutoStartup;

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

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_windowInitialized)
        {
            return;
        }

        _windowInitialized = true;
        LoadUiSettings();
        if (AutoStartupCheckBox.IsChecked == true)
        {
            _autoCloseAfterAutoStartup = true;
            BeginAutoStartup();
        }
        else
        {
            _autoCloseAfterAutoStartup = false;
        }
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

    private void OnUpdateSalesClick(object sender, RoutedEventArgs e)
    {
        var selected = MaxPagesCombo.SelectedItem as int? ?? 20;
        var progressEvery = ProgressEveryCombo.SelectedItem as int? ?? 10;
        StatusText.Text = "啟動中...";
        var updated = TryUpdateMaxPages(selected);
        if (!updated)
        {
            StatusText.Text = "設定檔寫入失敗，改用暫時參數啟動";
        }

        if (!TryStartWorker(updateMode: "sales", progressEvery: progressEvery, maxPages: selected, showCompletionMessage: true))
        {
            StatusText.Text = "啟動失敗";
            return;
        }

        StatusText.Text = $"更新銷量中（最大頁數 {selected}）";
        ProgressText.Text = "進度：處理中";
        StartProgressTimer();
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _autoStartupRunning = false;
        _startupQueue.Clear();
        _autoCloseAfterAutoStartup = false;

        var stopped = TryStopRunningWorker();
        StatusText.Text = stopped ? "已停止目前工作，回復資料中..." : "沒有執行中的工作，檢查回復資料中...";
        ProgressText.Text = "進度：回復中";

        var progressEvery = ProgressEveryCombo.SelectedItem as int? ?? 10;
        var rollbackPath = ResolveSalesRollbackPath();
        var hasRollbackSnapshot = !string.IsNullOrWhiteSpace(rollbackPath) && File.Exists(rollbackPath);
        var result = await Task.Run(() => RunWorkerOnce("restore-sales", progressEvery, maxPages: null, showCompletionMessage: false));
        if (!result.Success)
        {
            StatusText.Text = "取消後回復失敗";
            var detail = string.IsNullOrWhiteSpace(result.Detail) ? "未知錯誤" : result.Detail;
            MessageBox.Show($"取消後回復失敗：{Environment.NewLine}{detail}", "回復失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (hasRollbackSnapshot)
        {
            StatusText.Text = "已取消並回復到更新前資料";
            ProgressText.Text = "進度：已回復";
            return;
        }

        StatusText.Text = "已取消，目前沒有可回復資料";
        ProgressText.Text = "進度：已停止";
    }

    private void OnAutoStartupCheckedChanged(object sender, RoutedEventArgs e)
    {
        if (!_windowInitialized)
        {
            return;
        }

        TrySaveUiSettings(AutoStartupCheckBox.IsChecked == true);
    }

    private void BeginAutoStartup()
    {
        if (_autoStartupRunning)
        {
            return;
        }

        var selected = MaxPagesCombo.SelectedItem as int? ?? 20;
        var progressEvery = ProgressEveryCombo.SelectedItem as int? ?? 10;
        _autoStartupProgressEvery = progressEvery;
        var updated = TryUpdateMaxPages(selected);
        if (!updated)
        {
            StatusText.Text = "設定檔寫入失敗，改用暫時參數啟動";
        }

        _startupQueue.Clear();
        _startupQueue.Enqueue(new StartupTask("ranking", $"啟動時自動更新：排名（最大頁數 {selected}）", selected));
        _startupQueue.Enqueue(new StartupTask("pc-link", "啟動時自動更新：PC連結", null));
        _autoStartupRunning = true;
        StartNextAutoStartupTask();
    }

    private void StartNextAutoStartupTask()
    {
        if (!_autoStartupRunning)
        {
            return;
        }

        if (_startupQueue.Count == 0)
        {
            _autoStartupRunning = false;
            StatusText.Text = "啟動時自動更新完成";
            ProgressText.Text = "進度：已完成";
            if (_autoCloseAfterAutoStartup)
            {
                _ = ShowAutoStartupCompletedAndCloseAsync();
            }
            return;
        }

        var task = _startupQueue.Dequeue();
        if (!TryStartWorker(task.Mode, _autoStartupProgressEvery, task.MaxPages, showCompletionMessage: false))
        {
            _autoStartupRunning = false;
            _startupQueue.Clear();
            StatusText.Text = "啟動時自動更新失敗";
            return;
        }

        StatusText.Text = task.StatusText;
        ProgressText.Text = "進度：處理中";
        StartProgressTimer();
    }

    private async Task ShowAutoStartupCompletedAndCloseAsync()
    {
        const string message = "已完成PC排名與PC連結更新，程式30秒後自動關閉 ʕ•ᴥ•ʔ";
        var noticeWindow = CreateAutoCloseNoticeWindow(message);
        noticeWindow.Owner = this;
        noticeWindow.Show();

        var noticeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        noticeTimer.Tick += (_, _) =>
        {
            noticeTimer.Stop();
            if (noticeWindow.IsVisible)
            {
                noticeWindow.Close();
            }
        };
        noticeTimer.Start();

        await Task.Delay(TimeSpan.FromSeconds(30));
        if (noticeWindow.IsVisible)
        {
            noticeWindow.Close();
        }

        if (IsVisible)
        {
            Close();
        }
    }

    private static Window CreateAutoCloseNoticeWindow(string message)
    {
        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromRgb(242, 242, 242)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 209, 214)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16)
        };

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(16),
            BorderBrush = new SolidColorBrush(Color.FromRgb(236, 236, 236)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18)
        };

        var text = new TextBlock
        {
            Text = message,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        card.Child = text;
        outerBorder.Child = card;

        var window = new Window
        {
            Title = "完成",
            Width = 430,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Content = outerBorder,
            FontFamily = new FontFamily("Segoe UI"),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };

        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(window, TextHintingMode.Fixed);

        return window;
    }

    private bool TryStopRunningWorker()
    {
        try
        {
            if (_workerProcess == null || _workerProcess.HasExited)
            {
                return false;
            }

            _suppressNextWorkerExitError = true;
            _workerProcess.Kill(true);
            return true;
        }
        catch (Exception ex)
        {
            _suppressNextWorkerExitError = false;
            MessageBox.Show($"停止工作失敗：{ex.Message}", "取消失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
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

    private void LoadUiSettings()
    {
        try
        {
            var path = ResolveUiSettingsPath();
            if (path == null || !File.Exists(path))
            {
                AutoStartupCheckBox.IsChecked = false;
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                AutoStartupCheckBox.IsChecked = false;
                return;
            }

            var settings = JsonSerializer.Deserialize<UiSettings>(json);
            AutoStartupCheckBox.IsChecked = settings?.AutoStartupEnabled ?? false;
        }
        catch
        {
            AutoStartupCheckBox.IsChecked = false;
        }
    }

    private void TrySaveUiSettings(bool autoStartupEnabled)
    {
        try
        {
            var path = ResolveUiSettingsPath();
            if (path == null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var settings = new UiSettings
            {
                AutoStartupEnabled = autoStartupEnabled
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"儲存自動更新設定失敗：{ex.Message}", "設定失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private bool TryStartWorker(string updateMode, int progressEvery, int? maxPages, bool showCompletionMessage)
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
                MessageBox.Show("目前已有執行中的工作，請先按「取消」或等待完成。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var errorBuffer = new StringBuilder();
            var process = new Process
            {
                StartInfo = BuildWorkerProcessStartInfo(root, updateMode, progressEvery, maxPages, showCompletionMessage),
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                AppendLineWithLimit(errorBuffer, args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                AppendLineWithLimit(errorBuffer, args.Data);
            };
            process.Exited += (_, _) => Dispatcher.Invoke(() => OnWorkerExited(process, errorBuffer));

            if (!process.Start())
            {
                MessageBox.Show("無法啟動更新程序。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            _workerProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                process.WaitForExit(300);
            }
            catch
            {
                // ignore
            }

            if (process.HasExited && process.ExitCode != 0)
            {
                var detail = ReadErrorPreview(errorBuffer);
                if (string.IsNullOrWhiteSpace(detail))
                {
                    MessageBox.Show($"更新程序啟動後立即結束（ExitCode={process.ExitCode}）。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                MessageBox.Show($"更新程序啟動後立即結束（ExitCode={process.ExitCode}）：{Environment.NewLine}{detail}", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private WorkerRunResult RunWorkerOnce(string updateMode, int progressEvery, int? maxPages, bool showCompletionMessage)
    {
        try
        {
            var root = ResolveProjectRoot();
            if (root == null)
            {
                return new WorkerRunResult(false, "找不到專案路徑。");
            }

            var errorBuffer = new StringBuilder();
            using var process = new Process
            {
                StartInfo = BuildWorkerProcessStartInfo(root, updateMode, progressEvery, maxPages, showCompletionMessage)
            };
            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                AppendLineWithLimit(errorBuffer, args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                AppendLineWithLimit(errorBuffer, args.Data);
            };

            if (!process.Start())
            {
                return new WorkerRunResult(false, "無法啟動更新程序。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return new WorkerRunResult(true, ReadErrorPreview(errorBuffer));
            }

            var detail = ReadErrorPreview(errorBuffer);
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"更新程序結束（ExitCode={process.ExitCode}）。";
            }
            return new WorkerRunResult(false, detail);
        }
        catch (Exception ex)
        {
            return new WorkerRunResult(false, ex.Message);
        }
    }

    private static ProcessStartInfo BuildWorkerProcessStartInfo(
        string root,
        string updateMode,
        int progressEvery,
        int? maxPages,
        bool showCompletionMessage)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --project PcSalesWorker",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.Environment["RUN_ONCE"] = "1";
        psi.Environment["UPDATE_MODE"] = updateMode;
        psi.Environment["PROGRESS_EVERY"] = progressEvery.ToString();
        psi.Environment["BRING_BROWSER_FRONT"] = "1";
        psi.Environment["DOTNET_CLI_FORCE_UTF8_ENCODING"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "zh-Hant";
        psi.Environment["SHOW_COMPLETION_MESSAGE"] = showCompletionMessage ? "1" : "0";
        if (maxPages.HasValue)
        {
            psi.Environment["APP__SEARCH__MAXPAGES"] = maxPages.Value.ToString();
        }

        return psi;
    }

    private void OnWorkerExited(Process process, StringBuilder errorBuffer)
    {
        if (!ReferenceEquals(_workerProcess, process))
        {
            return;
        }

        if (_suppressNextWorkerExitError)
        {
            _suppressNextWorkerExitError = false;
            return;
        }

        if (process.ExitCode != 0)
        {
            _autoStartupRunning = false;
            _startupQueue.Clear();
            _autoCloseAfterAutoStartup = false;
            StatusText.Text = $"更新程序異常結束（{process.ExitCode}）";
            var detail = ReadErrorPreview(errorBuffer);
            if (string.IsNullOrWhiteSpace(detail))
            {
                MessageBox.Show($"更新程序異常結束（ExitCode={process.ExitCode}）。", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            MessageBox.Show($"更新程序異常結束（ExitCode={process.ExitCode}）：{Environment.NewLine}{detail}", "啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_autoStartupRunning)
        {
            StartNextAutoStartupTask();
            return;
        }

        StatusText.Text = "更新完成";
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

    private static string? ResolveUiSettingsPath()
    {
        var root = ResolveProjectRoot();
        if (root == null)
        {
            return null;
        }

        return Path.Combine(root, "logs", "main-ui-settings.json");
    }

    private static string? ResolveSalesRollbackPath()
    {
        var root = ResolveProjectRoot();
        if (root == null)
        {
            return null;
        }

        return Path.Combine(root, "logs", "sales-rollback.json");
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

    private static void AppendLineWithLimit(StringBuilder buffer, string line)
    {
        lock (buffer)
        {
            if (buffer.Length >= 8000)
            {
                return;
            }

            buffer.AppendLine(line);
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

    private sealed class StartupTask
    {
        public StartupTask(string mode, string statusText, int? maxPages)
        {
            Mode = mode;
            StatusText = statusText;
            MaxPages = maxPages;
        }

        public string Mode { get; }
        public string StatusText { get; }
        public int? MaxPages { get; }
    }

    private sealed class UiSettings
    {
        public bool AutoStartupEnabled { get; set; }
    }

    private sealed class WorkerRunResult
    {
        public WorkerRunResult(bool success, string detail)
        {
            Success = success;
            Detail = detail;
        }

        public bool Success { get; }
        public string Detail { get; }
    }
}
