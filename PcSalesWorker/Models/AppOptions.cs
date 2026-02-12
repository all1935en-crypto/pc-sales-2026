using System.Collections.Generic;

namespace PcSalesWorker.Models;

public sealed class AppOptions
{
    public string SpreadsheetId { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public int HeaderRow { get; set; } = 1;
    public ColumnOptions Columns { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public ScheduleOptions Schedule { get; set; } = new();
    public ChromeOptions Chrome { get; set; } = new();
    public PchomeOptions Pchome { get; set; } = new();
}

public sealed class ColumnOptions
{
    public string Ranking { get; set; } = "排名";
    public string Keyword { get; set; } = "關鍵字";
    public string Url { get; set; } = "網址";
    public string Title { get; set; } = "標題";
    public string ProductId { get; set; } = "商品ID";
    public string Status { get; set; } = "狀態";
    public string Sales30 { get; set; } = "30日銷量";
}

public sealed class SearchOptions
{
    public int MaxPages { get; set; } = 20;
    public int PageSize { get; set; } = 20;
}

public sealed class ScheduleOptions
{
    public string Timezone { get; set; } = "Asia/Taipei";
    public int DailyRunHour { get; set; } = 11;
    public int DailyRunMinute { get; set; } = 0;
}

public sealed class ChromeOptions
{
    public string UserDataDir { get; set; } = string.Empty;
    public string ProfileDirectory { get; set; } = "Default";
    public string UserAgent { get; set; } = string.Empty;
    public int WindowWidth { get; set; } = 1895;
    public int WindowHeight { get; set; } = 950;
}

public sealed class PchomeOptions
{
    public string LoginUrl { get; set; } = string.Empty;
    public string ReportUrl { get; set; } = string.Empty;
    public string SearchApiBase { get; set; } = string.Empty;
}
