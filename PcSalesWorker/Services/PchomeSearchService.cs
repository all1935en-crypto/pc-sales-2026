using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcSalesWorker.Models;

namespace PcSalesWorker.Services;

public sealed class PchomeSearchService
{
    private readonly ILogger<PchomeSearchService> _logger;
    private readonly AppOptions _options;
    private readonly HttpClient _httpClient;

    public PchomeSearchService(ILogger<PchomeSearchService> logger, IOptions<AppOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_options.Chrome.UserAgent);
    }

    public async Task<int?> GetRankingAsync(string keyword, string productId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return null;
        }

        var encoded = WebUtility.UrlEncode(keyword);
        var maxPages = Math.Max(1, _options.Search.MaxPages);
        var pageSize = Math.Max(1, _options.Search.PageSize);

        for (var page = 1; page <= maxPages; page++)
        {
            var url = $"{_options.Pchome.SearchApiBase}?q={encoded}&page={page}";
            var json = await _httpClient.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Prods", out var prods))
            {
                continue;
            }

            var index = 0;
            foreach (var prod in prods.EnumerateArray())
            {
                index++;
                if (!prod.TryGetProperty("Id", out var idElement))
                {
                    continue;
                }

                var id = idElement.GetString() ?? string.Empty;
                if (string.Equals(id, productId, StringComparison.OrdinalIgnoreCase))
                {
                    return (page - 1) * pageSize + index;
                }
            }

            if (prods.GetArrayLength() == 0)
            {
                break;
            }
        }

        return null;
    }

    public async Task<string?> GetProductTitleAsync(string productId, CancellationToken cancellationToken)
    {
        var url = $"https://24h.pchome.com.tw/prod/{productId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_options.Chrome.UserAgent);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var title = HtmlExtractMetaContent(html, "og:title")
            ?? HtmlExtractTitle(html);

        return NormalizeTitle(title);
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var trimmed = title.Trim();
        const string suffix = " - PChome 24h購物";
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^suffix.Length].TrimEnd();
        }

        return trimmed;
    }

    private static string? HtmlExtractMetaContent(string html, string property)
    {
        var marker = $"property=\"{property}\"";
        var index = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var contentIndex = html.IndexOf("content=\"", index, StringComparison.OrdinalIgnoreCase);
        if (contentIndex < 0)
        {
            return null;
        }

        contentIndex += "content=\"".Length;
        var endIndex = html.IndexOf('"', contentIndex);
        if (endIndex < 0)
        {
            return null;
        }

        return WebUtility.HtmlDecode(html.Substring(contentIndex, endIndex - contentIndex));
    }

    private static string? HtmlExtractTitle(string html)
    {
        var start = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += "<title>".Length;
        var end = html.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return null;
        }

        return WebUtility.HtmlDecode(html.Substring(start, end - start));
    }
}
