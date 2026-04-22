using Microsoft.Extensions.Options;
using PcSalesWorker.Models;
using PcSalesWorker.Services;
using System.Globalization;
using System.Text.Json;

namespace PcSalesWorker;

public sealed class Worker : BackgroundService
{
    private const string MissingTitleText = "異常！抓不到標題";

    private readonly ILogger<Worker> _logger;
    private readonly AppOptions _options;
    private readonly SheetService _sheetService;
    private readonly PchomeSearchService _searchService;
    private readonly PchomeBackendService _backendService;
    private readonly IHostApplicationLifetime _lifetime;

    public Worker(
        ILogger<Worker> logger,
        IOptions<AppOptions> options,
        SheetService sheetService,
        PchomeSearchService searchService,
        PchomeBackendService backendService,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _options = options.Value;
        _sheetService = sheetService;
        _searchService = searchService;
        _backendService = backendService;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runOnce = string.Equals(Environment.GetEnvironmentVariable("RUN_ONCE"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("RUN_ONCE"), "true", StringComparison.OrdinalIgnoreCase);
        if (!runOnce)
        {
            var updateMode = Environment.GetEnvironmentVariable("UPDATE_MODE");
            if (!string.IsNullOrWhiteSpace(updateMode))
            {
                runOnce = true;
            }
        }
        var pauseAfterFirst = IsPauseAfterFirstEnabled();
        var pauseBeforeSearch = IsPauseBeforeSearchEnabled();

        if (runOnce)
        {
            if (pauseBeforeSearch || pauseAfterFirst)
            {
                _logger.LogWarning("暫停模式啟用中：{PauseBeforeSearch}, {PauseAfterFirst}。", pauseBeforeSearch, pauseAfterFirst);
            }

            await RunOnceAsync(stoppingToken);
            if (!IsPauseAfterFirstEnabled() && !IsKeepBrowserOpenEnabled())
            {
                _lifetime.StopApplication();
            }
            else
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = GetNextRunTime();
            var delay = nextRun - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero)
            {
                _logger.LogInformation("下一次更新時間：{Time}", nextRun);
                await Task.Delay(delay, stoppingToken);
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    private DateTimeOffset GetNextRunTime()
    {
        var timeZone = TimeZoneHelper.Resolve(_options.Schedule.Timezone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, timeZone);
        var target = new DateTimeOffset(now.Year, now.Month, now.Day, _options.Schedule.DailyRunHour, _options.Schedule.DailyRunMinute, 0, now.Offset);
        if (target <= now)
        {
            target = target.AddDays(1);
        }

        return TimeZoneInfo.ConvertTime(target, TimeZoneInfo.Local);
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var startAt = DateTimeOffset.Now;
        _logger.LogInformation("開始更新 Google 試算表。時間：{Time}", startAt);
        var errors = new List<string>();
        var pauseAfterFirst = IsPauseAfterFirstEnabled();
        var pauseAfterN = GetPauseAfterN();
        var progressEvery = GetProgressEvery();

        try
        {
            var updateMode = GetUpdateMode();
            var updatePcLinkOnly = string.Equals(updateMode, "pc-link", StringComparison.OrdinalIgnoreCase);
            var updateRankingOnly = string.Equals(updateMode, "ranking", StringComparison.OrdinalIgnoreCase);
            var updateSalesOnly = string.Equals(updateMode, "sales", StringComparison.OrdinalIgnoreCase);
            var restoreSales = string.Equals(updateMode, "restore-sales", StringComparison.OrdinalIgnoreCase);
            var updateAll = string.Equals(updateMode, "all", StringComparison.OrdinalIgnoreCase)
                || (!updatePcLinkOnly && !updateRankingOnly && !updateSalesOnly && !restoreSales);

            if (restoreSales)
            {
                WriteProgress(new ProgressState
                {
                    Total = 0,
                    Processed = 0,
                    Status = "running"
                });
                var restoredRows = await RestoreSalesRollbackSnapshotAsync(cancellationToken);
                _logger.LogInformation("銷量回復流程完成，共處理 {Count} 筆。", restoredRows);
            }
            else if (updatePcLinkOnly)
            {
                WriteProgress(new ProgressState
                {
                    Total = 0,
                    Processed = 0,
                    Status = "running"
                });
                var updatedCount = await _sheetService.UpdatePcLinkAsync(cancellationToken);
                _logger.LogInformation("更新 PC 連結流程完成，填入 {Count} 筆。", updatedCount);
            }
            else
            {
                var shouldUpdateRanking = updateAll || updateRankingOnly;
                var shouldUpdateSales = updateAll || updateSalesOnly;
                var context = await _sheetService.LoadContextAsync(cancellationToken);
                var rows = _sheetService.ExtractRows(context);
                WriteProgress(new ProgressState
                {
                    Total = rows.Count,
                    Processed = 0,
                    RowIndex = rows.Count > 0 ? rows[0].RowIndex : _options.HeaderRow,
                    Status = "running"
                });

                var rankingColumn = _sheetService.ColumnLetter(context, _options.Columns.Ranking);
                var urlColumn = _sheetService.ColumnLetter(context, _options.Columns.Url);
                var titleColumn = _sheetService.ColumnLetter(context, _options.Columns.Title);
                var salesColumn = _sheetService.ColumnLetter(context, _options.Columns.Sales30);
                var rankingIndex = _sheetService.ColumnIndex(context, _options.Columns.Ranking);
                var urlIndex = _sheetService.ColumnIndex(context, _options.Columns.Url);
                var titleIndex = _sheetService.ColumnIndex(context, _options.Columns.Title);
                var salesIndex = _sheetService.ColumnIndex(context, _options.Columns.Sales30);

                if (shouldUpdateSales && (urlColumn == null || titleColumn == null || salesColumn == null))
                {
                    throw new InvalidOperationException("工作表缺少必要欄位，請確認欄位名稱。");
                }
                if (shouldUpdateRanking && rankingColumn == null)
                {
                    throw new InvalidOperationException("找不到排名欄位，無法更新排名。");
                }

                if (updateSalesOnly)
                {
                    await CaptureSalesRollbackSnapshotAsync(context, rows, urlIndex, titleIndex, salesIndex, cancellationToken);
                }

                var processed = 0;
                var pendingUpdates = new List<SheetUpdate>();
                var pendingTitleTextColorUpdates = new List<SheetTextColorUpdate>();
                foreach (var row in rows)
                {
                    var resolvedRowIndex = _sheetService.ResolveRowIndex(context, row.ProductId, row.RowIndex);
                    int? ranking = null;
                    int? sales30 = null;
                    string? title = null;
                    var titleIsError = false;

                    if (shouldUpdateRanking)
                    {
                        try
                        {
                            ranking = await _searchService.GetRankingAsync(row.Keyword, row.ProductId, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{row.ProductId}（排名）: {ex.Message}");
                            ExceptionHelper.Report(_logger, $"商品 {row.ProductId} 排名解析失敗", ex);
                        }
                    }

                    if (shouldUpdateSales)
                    {
                        title = await _searchService.GetProductTitleRequiredAsync(row.ProductId, row.Keyword, cancellationToken);
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            errors.Add($"{row.ProductId}（標題）: 無法取得標題，已填入異常訊息");
                            _logger.LogWarning("商品 {ProductId} 標題抓取失敗，已回填異常訊息。", row.ProductId);
                            title = MissingTitleText;
                            titleIsError = true;
                        }

                        try
                        {
                            sales30 = await _backendService.GetSales30Async(row.ProductId, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{row.ProductId}（30日銷量）: {ex.Message}");
                            ExceptionHelper.Report(_logger, $"商品 {row.ProductId} 銷量解析失敗", ex);
                        }
                    }

                    var productUrl = $"https://24h.pchome.com.tw/prod/{row.ProductId}";

                    if (shouldUpdateSales)
                    {
                        AddIfChanged(context, pendingUpdates, resolvedRowIndex, urlColumn, urlIndex, productUrl);
                        AddIfChanged(context, pendingUpdates, resolvedRowIndex, titleColumn, titleIndex, title ?? MissingTitleText);
                        AddIfChanged(context, pendingUpdates, resolvedRowIndex, salesColumn, salesIndex, sales30.HasValue ? sales30.Value : string.Empty);
                        AddTitleTextColorUpdate(pendingTitleTextColorUpdates, resolvedRowIndex, titleIndex, titleIsError);
                    }
                    if (shouldUpdateRanking && rankingColumn != null)
                    {
                        AddIfChanged(context, pendingUpdates, resolvedRowIndex, rankingColumn, rankingIndex, ranking.HasValue ? ranking.Value : -100);
                    }

                    processed++;
                    if (progressEvery > 0 && (processed % progressEvery == 0 || processed == 1))
                    {
                        WriteProgress(new ProgressState
                        {
                            Total = rows.Count,
                            Processed = processed,
                            RowIndex = resolvedRowIndex,
                            ProductId = row.ProductId,
                            Status = "running"
                        });
                    }
                    if (processed % 10 == 0)
                    {
                        await _sheetService.ApplyUpdatesAsync(context, pendingUpdates, pendingTitleTextColorUpdates, cancellationToken);
                        pendingUpdates.Clear();
                        pendingTitleTextColorUpdates.Clear();
                    }
                    if (pauseAfterFirst && processed >= 1)
                    {
                        _logger.LogWarning("已暫停在第一筆商品後，請手動測試。");
                        break;
                    }
                    if (pauseAfterN > 0 && processed >= pauseAfterN)
                    {
                        _logger.LogWarning("已暫停在第 {Count} 筆商品後，請手動測試。", pauseAfterN);
                        break;
                    }
                }

                if (pendingUpdates.Count > 0 || pendingTitleTextColorUpdates.Count > 0)
                {
                    await _sheetService.ApplyUpdatesAsync(context, pendingUpdates, pendingTitleTextColorUpdates, cancellationToken);
                    pendingUpdates.Clear();
                    pendingTitleTextColorUpdates.Clear();
                }

                if (shouldUpdateRanking)
                {
                    await _sheetService.RotateRankingSnapshotColumnsAsync(context, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            ExceptionHelper.Report(_logger, "更新流程失敗", ex);
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "本次更新共有 {Count} 筆錯誤，已依設定略過 Email 通知。錯誤摘要：{Summary}",
                errors.Count,
                string.Join(" | ", errors.Take(5)));
        }

        var endAt = DateTimeOffset.Now;
        _logger.LogInformation("更新流程結束。時間：{Time}", endAt);
        WriteProgress(new ProgressState
        {
            Total = 0,
            Processed = 0,
            Status = "completed"
        });

        if (pauseAfterFirst)
        {
            _logger.LogWarning("暫停模式啟用中，程式將保持開啟以便手動測試。");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return;
        }

        if (ShouldShowCompletionMessage())
        {
            var elapsed = endAt - startAt;
            var message = $"已完成更新 ʕ•ᴥ•ʔ{Environment.NewLine}共花費{elapsed.Minutes}分鐘{elapsed.Seconds}秒";
            MessageBox.Show(message, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        }
        return;
    }

    private async Task CaptureSalesRollbackSnapshotAsync(
        SheetContext context,
        IReadOnlyList<SheetRow> rows,
        int? urlIndex,
        int? titleIndex,
        int? salesIndex,
        CancellationToken cancellationToken)
    {
        if (urlIndex is null or < 0 || titleIndex is null or < 0 || salesIndex is null or < 0)
        {
            throw new InvalidOperationException("建立回復快照失敗：缺少網址/標題/30日銷量欄位。");
        }

        var snapshot = new SalesRollbackSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Rows = new List<SalesRollbackRow>(rows.Count)
        };

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedRowIndex = _sheetService.ResolveRowIndex(context, row.ProductId, row.RowIndex);
            snapshot.Rows.Add(new SalesRollbackRow
            {
                RowIndex = resolvedRowIndex,
                ProductId = row.ProductId,
                Url = GetCellValue(context, resolvedRowIndex, urlIndex.Value),
                Title = GetCellValue(context, resolvedRowIndex, titleIndex.Value),
                Sales30 = GetCellValue(context, resolvedRowIndex, salesIndex.Value)
            });
        }

        SaveSalesRollbackSnapshot(snapshot);
        _logger.LogInformation("已建立銷量回復快照，共 {Count} 筆。", snapshot.Rows.Count);
    }

    private async Task<int> RestoreSalesRollbackSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshotPath = ResolveSalesRollbackPath();
        if (!File.Exists(snapshotPath))
        {
            _logger.LogInformation("找不到可回復的銷量快照，略過回復。");
            return 0;
        }

        SalesRollbackSnapshot? snapshot;
        try
        {
            var json = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
            snapshot = JsonSerializer.Deserialize<SalesRollbackSnapshot>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("讀取銷量回復快照失敗。", ex);
        }

        if (snapshot?.Rows == null || snapshot.Rows.Count == 0)
        {
            TryDeleteSalesRollbackSnapshot(snapshotPath);
            _logger.LogInformation("銷量快照為空，已清除快照檔。");
            return 0;
        }

        var context = await _sheetService.LoadContextAsync(cancellationToken);
        var urlColumn = _sheetService.ColumnLetter(context, _options.Columns.Url);
        var titleColumn = _sheetService.ColumnLetter(context, _options.Columns.Title);
        var salesColumn = _sheetService.ColumnLetter(context, _options.Columns.Sales30);
        var urlIndex = _sheetService.ColumnIndex(context, _options.Columns.Url);
        var titleIndex = _sheetService.ColumnIndex(context, _options.Columns.Title);
        var salesIndex = _sheetService.ColumnIndex(context, _options.Columns.Sales30);

        if (urlColumn == null || titleColumn == null || salesColumn == null
            || urlIndex is null or < 0 || titleIndex is null or < 0 || salesIndex is null or < 0)
        {
            throw new InvalidOperationException("回復銷量快照失敗：工作表缺少網址/標題/30日銷量欄位。");
        }

        var pendingUpdates = new List<SheetUpdate>();
        foreach (var row in snapshot.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedRowIndex = string.IsNullOrWhiteSpace(row.ProductId)
                ? row.RowIndex
                : _sheetService.ResolveRowIndex(context, row.ProductId, row.RowIndex);

            AddIfChanged(context, pendingUpdates, resolvedRowIndex, urlColumn, urlIndex, row.Url ?? string.Empty);
            AddIfChanged(context, pendingUpdates, resolvedRowIndex, titleColumn, titleIndex, row.Title ?? string.Empty);
            AddIfChanged(context, pendingUpdates, resolvedRowIndex, salesColumn, salesIndex, row.Sales30 ?? string.Empty);

            if (pendingUpdates.Count >= 300)
            {
                await _sheetService.ApplyUpdatesAsync(context, pendingUpdates, null, cancellationToken);
                pendingUpdates.Clear();
            }
        }

        if (pendingUpdates.Count > 0)
        {
            await _sheetService.ApplyUpdatesAsync(context, pendingUpdates, null, cancellationToken);
            pendingUpdates.Clear();
        }

        TryDeleteSalesRollbackSnapshot(snapshotPath);
        return snapshot.Rows.Count;
    }

    private static void SaveSalesRollbackSnapshot(SalesRollbackSnapshot snapshot)
    {
        var path = ResolveSalesRollbackPath();
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("找不到銷量快照儲存路徑。");
        }

        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(snapshot, options);
        var tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, true);
    }

    private void TryDeleteSalesRollbackSnapshot(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刪除銷量快照檔失敗：{Path}", path);
        }
    }

    private static void WriteProgress(ProgressState state)
    {
        try
        {
            var root = ResolveProjectRoot();
            var logsDir = Path.Combine(root, "logs");
            Directory.CreateDirectory(logsDir);
            var path = Path.Combine(logsDir, "progress.json");
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(path, json);
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolveProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (string.Equals(current.Name, "PcSalesWorker", StringComparison.OrdinalIgnoreCase))
            {
                return current.Parent?.FullName ?? current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string ResolveSalesRollbackPath()
    {
        var root = ResolveProjectRoot();
        return Path.Combine(root, "logs", "sales-rollback.json");
    }

    private sealed class ProgressState
    {
        public int Total { get; set; }
        public int Processed { get; set; }
        public int RowIndex { get; set; }
        public string? ProductId { get; set; }
        public string Status { get; set; } = "idle";
    }

    private static void AddIfChanged(SheetContext context, List<SheetUpdate> updates, int rowIndex, string? columnLetter, int? columnIndex, object value)
    {
        if (columnLetter == null || columnIndex == null || columnIndex < 0)
        {
            return;
        }

        var existing = GetCellValue(context, rowIndex, columnIndex.Value);
        if (IsSameValue(existing, value))
        {
            return;
        }

        updates.Add(new SheetUpdate(rowIndex, columnLetter, value));
        SetCellValue(context, rowIndex, columnIndex.Value, value);
    }

    private static void AddTitleTextColorUpdate(List<SheetTextColorUpdate> updates, int rowIndex, int? columnIndex, bool isError)
    {
        if (columnIndex == null || columnIndex < 0)
        {
            return;
        }

        if (isError)
        {
            updates.Add(new SheetTextColorUpdate(rowIndex, columnIndex.Value, 1f, 0f, 0f));
            return;
        }

        updates.Add(new SheetTextColorUpdate(rowIndex, columnIndex.Value, 0f, 0f, 0f));
    }

    private static string GetCellValue(SheetContext context, int rowIndex, int columnIndex)
    {
        var rowIdx = rowIndex - 1;
        if (rowIdx < 0 || rowIdx >= context.Values.Count)
        {
            return string.Empty;
        }

        var row = context.Values[rowIdx];
        if (columnIndex < 0 || columnIndex >= row.Count)
        {
            return string.Empty;
        }

        return row[columnIndex]?.ToString()?.Trim() ?? string.Empty;
    }

    private static void SetCellValue(SheetContext context, int rowIndex, int columnIndex, object value)
    {
        var rowIdx = rowIndex - 1;
        if (rowIdx < 0 || rowIdx >= context.Values.Count)
        {
            return;
        }

        var row = context.Values[rowIdx];
        while (row.Count <= columnIndex)
        {
            row.Add(string.Empty);
        }

        row[columnIndex] = value?.ToString() ?? string.Empty;
    }

    private static bool IsSameValue(string existing, object value)
    {
        var incoming = value?.ToString()?.Trim() ?? string.Empty;
        if (string.Equals(existing, incoming, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (decimal.TryParse(existing, NumberStyles.Any, CultureInfo.InvariantCulture, out var existingNumber)
            && decimal.TryParse(incoming, NumberStyles.Any, CultureInfo.InvariantCulture, out var incomingNumber))
        {
            return existingNumber == incomingNumber;
        }

        return false;
    }

    private static bool IsPauseAfterFirstEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("PAUSE_AFTER_FIRST_PRODUCT"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("PAUSE_AFTER_FIRST_PRODUCT"), "true", StringComparison.OrdinalIgnoreCase);

    private static int GetPauseAfterN()
    {
        var raw = Environment.GetEnvironmentVariable("PAUSE_AFTER_N");
        if (int.TryParse(raw, out var value) && value > 0)
        {
            return value;
        }

        return 0;
    }

    private static int GetProgressEvery()
    {
        var raw = Environment.GetEnvironmentVariable("PROGRESS_EVERY");
        if (int.TryParse(raw, out var value) && value > 0)
        {
            return value;
        }

        return 10;
    }

    private static bool IsKeepBrowserOpenEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("KEEP_BROWSER_OPEN"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("KEEP_BROWSER_OPEN"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsPauseBeforeSearchEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("PAUSE_BEFORE_SEARCH"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("PAUSE_BEFORE_SEARCH"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldShowCompletionMessage()
    {
        var raw = Environment.GetEnvironmentVariable("SHOW_COMPLETION_MESSAGE");
        if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string GetUpdateMode()
    {
        var mode = Environment.GetEnvironmentVariable("UPDATE_MODE");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            return mode.Trim();
        }

        var legacy = Environment.GetEnvironmentVariable("UPDATE_RANKING");
        if (string.Equals(legacy, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(legacy, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "ranking";
        }

        return "all";
    }

    private sealed class SalesRollbackSnapshot
    {
        public DateTimeOffset CapturedAt { get; set; }
        public List<SalesRollbackRow> Rows { get; set; } = new();
    }

    private sealed class SalesRollbackRow
    {
        public int RowIndex { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Sales30 { get; set; } = string.Empty;
    }
}
