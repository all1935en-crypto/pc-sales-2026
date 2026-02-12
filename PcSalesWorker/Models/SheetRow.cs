namespace PcSalesWorker.Models;

public sealed class SheetRow
{
    public int RowIndex { get; init; }
    public string ProductId { get; init; } = string.Empty;
    public string Keyword { get; init; } = string.Empty;
}
