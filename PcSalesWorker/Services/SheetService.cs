using System.Globalization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcSalesWorker.Models;

namespace PcSalesWorker.Services;

public sealed class SheetService
{
    private readonly ILogger<SheetService> _logger;
    private readonly AppOptions _options;
    private readonly SheetsService _sheets;

    public SheetService(ILogger<SheetService> logger, IOptions<AppOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        var credentialPath = Path.Combine(AppContext.BaseDirectory, "credentials.json");
        var credential = GoogleCredential.FromFile(credentialPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        _sheets = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "PcSalesWorker"
        });
    }

    public async Task<SheetContext> LoadContextAsync(CancellationToken cancellationToken)
    {
        var sheetName = _options.SheetName;
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            var sheet = await _sheets.Spreadsheets.Get(_options.SpreadsheetId)
                .ExecuteAsync(cancellationToken);
            sheetName = sheet.Sheets?.FirstOrDefault()?.Properties?.Title ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(sheetName))
        {
            throw new InvalidOperationException("找不到工作表名稱，請在 appsettings.json 設定 SheetName。");
        }

        Dictionary<string, int>? headersFromGrid = null;
        try
        {
            var headerRange = $"{sheetName}!A{_options.HeaderRow}:Z{_options.HeaderRow}";
            var gridRequest = _sheets.Spreadsheets.Get(_options.SpreadsheetId);
            gridRequest.Ranges = new Google.Apis.Util.Repeatable<string>(new[] { headerRange });
            gridRequest.IncludeGridData = true;
            var gridResponse = await gridRequest.ExecuteAsync(cancellationToken);
            var rowData = gridResponse.Sheets?
                .FirstOrDefault()?
                .Data?
                .FirstOrDefault()?
                .RowData?
                .FirstOrDefault();

            if (rowData?.Values != null)
            {
                headersFromGrid = rowData.Values
                    .Select((cell, index) => new { Name = cell.FormattedValue?.Trim() ?? string.Empty, Index = index })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                    .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取表頭 grid 資料失敗，改用 Values API。");
        }

        var range = $"{sheetName}!A1:Z";
        var response = await _sheets.Spreadsheets.Values.Get(_options.SpreadsheetId, range)
            .ExecuteAsync(cancellationToken);

        var values = response.Values ?? new List<IList<object>>();
        if (values.Count < _options.HeaderRow)
        {
            throw new InvalidOperationException("工作表沒有標題列。");
        }

        var headerIndex = _options.HeaderRow - 1;
        if (headersFromGrid == null)
        {
            throw new InvalidOperationException("讀取表頭失敗（GridData 為空），為避免欄位錯位已停止寫入。");
        }

        var headers = headersFromGrid ?? values[headerIndex]
            .Select((value, index) => new { Name = value?.ToString()?.Trim() ?? string.Empty, Index = index })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        LogHeaderIndex(headers);

        return new SheetContext(sheetName, headers, values);
    }

    private void LogHeaderIndex(Dictionary<string, int> headers)
    {
        var labels = new[]
        {
            _options.Columns.Ranking,
            _options.Columns.Keyword,
            _options.Columns.Url,
            _options.Columns.Title,
            _options.Columns.ProductId,
            _options.Columns.Status,
            _options.Columns.Sales30
        };

        var parts = new List<string>();
        foreach (var label in labels)
        {
            if (!headers.TryGetValue(label, out var index))
            {
                parts.Add($"{label}=未找到");
                continue;
            }

            parts.Add($"{label}={ToColumnLetter(index + 1)}({index + 1})");
        }

        _logger.LogInformation("表頭欄位索引：{Mapping}", string.Join(", ", parts));
    }

    public IReadOnlyList<SheetRow> ExtractRows(SheetContext context)
    {
        if (!context.Headers.TryGetValue(_options.Columns.ProductId, out var productIdColumn))
        {
            throw new InvalidOperationException($"找不到欄位：{_options.Columns.ProductId}");
        }

        context.Headers.TryGetValue(_options.Columns.Keyword, out var keywordColumn);

        var rows = new List<SheetRow>();
        for (var i = _options.HeaderRow; i < context.Values.Count; i++)
        {
            var row = context.Values[i];
            var productId = GetCell(row, productIdColumn).Trim();
            if (string.IsNullOrWhiteSpace(productId))
            {
                continue;
            }

            var keyword = keywordColumn >= 0 ? GetCell(row, keywordColumn).Trim() : string.Empty;
            rows.Add(new SheetRow
            {
                RowIndex = i + 1,
                ProductId = productId,
                Keyword = keyword
            });
        }

        return rows;
    }

    public async Task ApplyUpdatesAsync(SheetContext context, IReadOnlyList<SheetUpdate> updates, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var data = new List<ValueRange>();
        foreach (var update in updates)
        {
            var range = $"{context.SheetName}!{update.Column}{update.RowIndex}";
            data.Add(new ValueRange
            {
                Range = range,
                Values = new List<IList<object>> { new List<object> { update.Value } }
            });
        }

        var request = new BatchUpdateValuesRequest
        {
            ValueInputOption = "RAW",
            Data = data
        };

        var batchRequest = _sheets.Spreadsheets.Values.BatchUpdate(request, _options.SpreadsheetId);
        await batchRequest.ExecuteAsync(cancellationToken);
    }

    public int ResolveRowIndex(SheetContext context, string productId, int expectedRowIndex)
    {
        if (!context.Headers.TryGetValue(_options.Columns.ProductId, out var productIdColumn))
        {
            return expectedRowIndex;
        }

        var expectedIndex = expectedRowIndex - 1;
        if (expectedIndex >= 0 && expectedIndex < context.Values.Count)
        {
            var current = GetCell(context.Values[expectedIndex], productIdColumn).Trim();
            if (string.Equals(current, productId, StringComparison.OrdinalIgnoreCase))
            {
                return expectedRowIndex;
            }
        }

        var downIndex = expectedIndex + 1;
        if (downIndex >= 0 && downIndex < context.Values.Count)
        {
            var down = GetCell(context.Values[downIndex], productIdColumn).Trim();
            if (string.Equals(down, productId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("偵測到列號偏移，商品 {ProductId} 由 {From} 修正為 {To}", productId, expectedRowIndex, downIndex + 1);
                return downIndex + 1;
            }
        }

        var upIndex = expectedIndex - 1;
        if (upIndex >= 0 && upIndex < context.Values.Count)
        {
            var up = GetCell(context.Values[upIndex], productIdColumn).Trim();
            if (string.Equals(up, productId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("偵測到列號偏移，商品 {ProductId} 由 {From} 修正為 {To}", productId, expectedRowIndex, upIndex + 1);
                return upIndex + 1;
            }
        }

        for (var i = 0; i < context.Values.Count; i++)
        {
            var value = GetCell(context.Values[i], productIdColumn).Trim();
            if (string.Equals(value, productId, StringComparison.OrdinalIgnoreCase))
            {
                var resolved = i + 1;
                if (resolved != expectedRowIndex)
                {
                    _logger.LogWarning("偵測到列號偏移，商品 {ProductId} 由 {From} 修正為 {To}", productId, expectedRowIndex, resolved);
                }
                return resolved;
            }
        }

        return expectedRowIndex;
    }

    public string? ColumnLetter(SheetContext context, string header)
    {
        if (!context.Headers.TryGetValue(header, out var index))
        {
            return null;
        }

        return ToColumnLetter(index + 1);
    }

    public int? ColumnIndex(SheetContext context, string header)
    {
        if (!context.Headers.TryGetValue(header, out var index))
        {
            return null;
        }

        return index;
    }

    private static string GetCell(IList<object> row, int index)
    {
        if (index < 0 || index >= row.Count)
        {
            return string.Empty;
        }

        return row[index]?.ToString() ?? string.Empty;
    }

    private static string ToColumnLetter(int columnNumber)
    {
        var dividend = columnNumber;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }
        return columnName;
    }
}

public sealed record SheetContext(string SheetName, Dictionary<string, int> Headers, IList<IList<object>> Values);

public sealed record SheetUpdate(int RowIndex, string Column, object Value);
