using System.Globalization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcSalesWorker.Models;
using SheetsColor = Google.Apis.Sheets.v4.Data.Color;

namespace PcSalesWorker.Services;

public sealed class SheetService
{
    private const string PcLinkSpreadsheetId = "1IFy624vuMAFIUshcTBlcUb0xtpnq0o8lgwhG2ON9Q0Y";
    private const int PcLinkSheetId = 1044879942;
    private const string PcLinkSheetFallbackName = "PC連結";
    private const string SalesSpreadsheetId = "1JdEZdgzdq3YIkT2X6fPirlLa1AYJPdbzf9AmHFehH5E";
    private const int SalesSheetId = 0;
    private const string SalesSheetFallbackName = "2026年PC曝光";

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
        var sheetMetadataRequest = _sheets.Spreadsheets.Get(_options.SpreadsheetId);
        sheetMetadataRequest.Fields = "sheets(properties(sheetId,title))";
        var spreadsheet = await sheetMetadataRequest.ExecuteAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(sheetName))
        {
            sheetName = spreadsheet.Sheets?.FirstOrDefault()?.Properties?.Title ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            throw new InvalidOperationException("找不到工作表名稱，請在 appsettings.json 設定 SheetName。");
        }

        var sheetId = spreadsheet.Sheets?
            .FirstOrDefault(s => string.Equals(s.Properties?.Title, sheetName, StringComparison.OrdinalIgnoreCase))?
            .Properties?
            .SheetId;
        if (!sheetId.HasValue)
        {
            throw new InvalidOperationException($"找不到工作表 ID：{sheetName}");
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
                headersFromGrid = BuildHeaderMap(
                    rowData.Values.Select(cell => cell.FormattedValue),
                    "GridData");
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
        var headersFromValues = BuildHeaderMap(
            values[headerIndex].Select(value => value?.ToString()),
            "Values API");

        var headers = headersFromGrid != null && headersFromGrid.Count > 0
            ? headersFromGrid
            : headersFromValues;

        if (headersFromGrid == null || headersFromGrid.Count == 0)
        {
            _logger.LogWarning("GridData 未提供可用表頭，已改用 Values API。");
        }

        if (headers.Count == 0)
        {
            throw new InvalidOperationException("讀取表頭失敗（找不到任何可用欄位），已停止寫入。");
        }

        LogHeaderIndex(headers);

        return new SheetContext(sheetName, sheetId.Value, headers, values);
    }

    private Dictionary<string, int> BuildHeaderMap(IEnumerable<string?> headerValues, string sourceName)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicatedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var headerValue in headerValues)
        {
            var header = headerValue?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(header) && !headers.TryAdd(header, index))
            {
                duplicatedHeaders.Add(header);
            }

            index++;
        }

        if (duplicatedHeaders.Count > 0)
        {
            _logger.LogWarning(
                "{SourceName} 表頭有重複欄位：{DuplicatedHeaders}，已採用最左側欄位。",
                sourceName,
                string.Join(", ", duplicatedHeaders.OrderBy(x => x)));
        }

        return headers;
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

    public async Task ApplyUpdatesAsync(
        SheetContext context,
        IReadOnlyList<SheetUpdate> updates,
        IReadOnlyList<SheetTextColorUpdate>? textColorUpdates,
        CancellationToken cancellationToken)
    {
        var colorUpdates = textColorUpdates ?? Array.Empty<SheetTextColorUpdate>();
        if (updates.Count == 0 && colorUpdates.Count == 0)
        {
            return;
        }

        if (updates.Count > 0)
        {
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

        if (colorUpdates.Count == 0)
        {
            return;
        }

        var latestColorByCell = new Dictionary<(int RowIndex, int ColumnIndex), SheetTextColorUpdate>();
        foreach (var colorUpdate in colorUpdates)
        {
            if (colorUpdate.RowIndex <= 0 || colorUpdate.ColumnIndex < 0)
            {
                continue;
            }

            latestColorByCell[(colorUpdate.RowIndex, colorUpdate.ColumnIndex)] = colorUpdate;
        }

        if (latestColorByCell.Count == 0)
        {
            return;
        }

        var formatRequests = new List<Request>();
        foreach (var colorUpdate in latestColorByCell.Values)
        {
            formatRequests.Add(new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange
                    {
                        SheetId = context.SheetId,
                        StartRowIndex = colorUpdate.RowIndex - 1,
                        EndRowIndex = colorUpdate.RowIndex,
                        StartColumnIndex = colorUpdate.ColumnIndex,
                        EndColumnIndex = colorUpdate.ColumnIndex + 1
                    },
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat
                        {
                            TextFormat = new TextFormat
                            {
                                ForegroundColor = new SheetsColor
                                {
                                    Red = colorUpdate.Red,
                                    Green = colorUpdate.Green,
                                    Blue = colorUpdate.Blue
                                }
                            }
                        }
                    },
                    Fields = "userEnteredFormat.textFormat.foregroundColor"
                }
            });
        }

        var formatBatch = new BatchUpdateSpreadsheetRequest
        {
            Requests = formatRequests
        };

        var formatBatchRequest = _sheets.Spreadsheets.BatchUpdate(formatBatch, _options.SpreadsheetId);
        await formatBatchRequest.ExecuteAsync(cancellationToken);
    }

    public async Task RotateRankingSnapshotColumnsAsync(SheetContext context, CancellationToken cancellationToken)
    {
        // A=0, J=9, K=10
        const int sourceAIndex = 0;
        const int sourceJIndex = 9;
        const int insertedKIndex = 10;
        var today = DateTime.Today;
        var todayFullLabel = today.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
        var todaySerial = today.ToOADate();

        var rowCount = Math.Max(context.Values.Count, _options.HeaderRow);
        if (rowCount <= 0)
        {
            rowCount = 1;
        }

        var requests = new List<Request>
        {
            new Request
            {
                InsertDimension = new InsertDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = context.SheetId,
                        Dimension = "COLUMNS",
                        StartIndex = insertedKIndex,
                        EndIndex = insertedKIndex + 1
                    },
                    InheritFromBefore = false
                }
            },
            new Request
            {
                CopyPaste = new CopyPasteRequest
                {
                    Source = new GridRange
                    {
                        SheetId = context.SheetId,
                        StartRowIndex = 0,
                        EndRowIndex = rowCount,
                        StartColumnIndex = sourceJIndex,
                        EndColumnIndex = sourceJIndex + 1
                    },
                    Destination = new GridRange
                    {
                        SheetId = context.SheetId,
                        StartRowIndex = 0,
                        EndRowIndex = rowCount,
                        StartColumnIndex = insertedKIndex,
                        EndColumnIndex = insertedKIndex + 1
                    },
                    PasteType = "PASTE_VALUES"
                }
            },
            new Request
            {
                CopyPaste = new CopyPasteRequest
                {
                    Source = new GridRange
                    {
                        SheetId = context.SheetId,
                        StartRowIndex = 0,
                        EndRowIndex = rowCount,
                        StartColumnIndex = sourceAIndex,
                        EndColumnIndex = sourceAIndex + 1
                    },
                    Destination = new GridRange
                    {
                        SheetId = context.SheetId,
                        StartRowIndex = 0,
                        EndRowIndex = rowCount,
                        StartColumnIndex = sourceJIndex,
                        EndColumnIndex = sourceJIndex + 1
                    },
                    PasteType = "PASTE_VALUES"
                }
            },
            new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange
                    {
                        SheetId = context.SheetId,
                        StartRowIndex = 0,
                        EndRowIndex = 1,
                        StartColumnIndex = sourceJIndex,
                        EndColumnIndex = sourceJIndex + 1
                    },
                    Cell = new CellData
                    {
                        UserEnteredValue = new ExtendedValue
                        {
                            NumberValue = todaySerial
                        },
                        UserEnteredFormat = new CellFormat
                        {
                            NumberFormat = new NumberFormat
                            {
                                Type = "DATE",
                                Pattern = "M/d"
                            }
                        }
                    },
                    Fields = "userEnteredValue,userEnteredFormat.numberFormat"
                }
            }
        };

        var request = new BatchUpdateSpreadsheetRequest
        {
            Requests = requests
        };

        var batchRequest = _sheets.Spreadsheets.BatchUpdate(request, _options.SpreadsheetId);
        await batchRequest.ExecuteAsync(cancellationToken);
        _logger.LogInformation("排名欄位快照完成：已插入 K 欄，並將 J->K、A->J，且 J1 設為 {DateLabel}（顯示 M/d）。", todayFullLabel);
    }

    public async Task<int> UpdatePcLinkAsync(CancellationToken cancellationToken)
    {
        var pcLinkSheetName = await ResolveSheetNameAsync(
            PcLinkSpreadsheetId,
            PcLinkSheetId,
            PcLinkSheetFallbackName,
            cancellationToken);
        var salesSheetName = await ResolveSheetNameAsync(
            SalesSpreadsheetId,
            SalesSheetId,
            SalesSheetFallbackName,
            cancellationToken);

        var pcLinkReadRange = BuildRange(pcLinkSheetName, "C2:G");
        var pcLinkResponse = await _sheets.Spreadsheets.Values.Get(PcLinkSpreadsheetId, pcLinkReadRange)
            .ExecuteAsync(cancellationToken);
        var pcLinkRows = pcLinkResponse.Values ?? new List<IList<object>>();

        var pendingRows = new List<(int RowIndex, string ProductId)>();
        for (var i = 0; i < pcLinkRows.Count; i++)
        {
            var row = pcLinkRows[i];
            var productId = GetCell(row, 0).Trim();
            if (string.IsNullOrWhiteSpace(productId))
            {
                continue;
            }

            var linkCell = GetCell(row, 4).Trim();
            if (!string.IsNullOrWhiteSpace(linkCell))
            {
                continue;
            }

            pendingRows.Add((i + 2, productId));
        }

        if (pendingRows.Count == 0)
        {
            _logger.LogInformation("PC連結表沒有需要比對的空白 G 欄。");
            return 0;
        }

        var salesIdRange = BuildRange(salesSheetName, "F2:F");
        var salesResponse = await _sheets.Spreadsheets.Values.Get(SalesSpreadsheetId, salesIdRange)
            .ExecuteAsync(cancellationToken);
        var salesRows = salesResponse.Values ?? new List<IList<object>>();

        var salesIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in salesRows)
        {
            var productId = GetCell(row, 0).Trim();
            if (!string.IsNullOrWhiteSpace(productId))
            {
                salesIds.Add(productId);
            }
        }

        var updates = new List<ValueRange>();
        foreach (var pendingRow in pendingRows)
        {
            if (!salesIds.Contains(pendingRow.ProductId))
            {
                continue;
            }

            updates.Add(new ValueRange
            {
                Range = BuildRange(pcLinkSheetName, $"G{pendingRow.RowIndex}"),
                Values = new List<IList<object>>
                {
                    new List<object> { "---" }
                }
            });
        }

        if (updates.Count == 0)
        {
            _logger.LogInformation("PC連結表空白 G 欄共 {Count} 筆，沒有任何商品 ID 命中 2026年PC曝光 F 欄。", pendingRows.Count);
            return 0;
        }

        var updateRequest = new BatchUpdateValuesRequest
        {
            ValueInputOption = "RAW",
            Data = updates
        };
        var batchRequest = _sheets.Spreadsheets.Values.BatchUpdate(updateRequest, PcLinkSpreadsheetId);
        await batchRequest.ExecuteAsync(cancellationToken);

        _logger.LogInformation("PC連結同步完成，已填入 {Updated} 筆。", updates.Count);
        return updates.Count;
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

    private async Task<string> ResolveSheetNameAsync(
        string spreadsheetId,
        int sheetId,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = _sheets.Spreadsheets.Get(spreadsheetId);
            request.Fields = "sheets(properties(sheetId,title))";
            var spreadsheet = await request.ExecuteAsync(cancellationToken);
            var bySheetId = spreadsheet.Sheets?
                .FirstOrDefault(x => x.Properties?.SheetId == sheetId)?
                .Properties?
                .Title;

            if (!string.IsNullOrWhiteSpace(bySheetId))
            {
                return bySheetId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用 sheetId 解析工作表名稱失敗，改用預設名稱：{FallbackName}", fallbackName);
        }

        if (string.IsNullOrWhiteSpace(fallbackName))
        {
            throw new InvalidOperationException("找不到工作表名稱。");
        }

        return fallbackName;
    }

    private static string BuildRange(string sheetName, string range)
    {
        var escaped = sheetName.Replace("'", "''");
        return $"'{escaped}'!{range}";
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

public sealed record SheetContext(string SheetName, int SheetId, Dictionary<string, int> Headers, IList<IList<object>> Values);

public sealed record SheetUpdate(int RowIndex, string Column, object Value);

public sealed record SheetTextColorUpdate(int RowIndex, int ColumnIndex, float Red, float Green, float Blue);
