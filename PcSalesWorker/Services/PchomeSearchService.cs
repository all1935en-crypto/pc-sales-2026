using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            _logger.LogWarning("商品 {ProductId} 標題頁面回應失敗：{StatusCode}", productId, (int)response.StatusCode);
            return await GetProductTitleFromSearchApiAsync(productId, cancellationToken);
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var title = HtmlExtractMetaContent(html, "og:title")
            ?? HtmlExtractMetaContent(html, "twitter:title")
            ?? HtmlExtractMetaContent(html, "title")
            ?? HtmlExtractTitle(html);

        var normalized = NormalizeTitle(title);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        _logger.LogWarning("商品 {ProductId} 無法從商品頁擷取標題，改用搜尋 API 嘗試。", productId);
        return await GetProductTitleFromSearchApiAsync(productId, cancellationToken);
    }

    private async Task<string?> GetProductTitleFromSearchApiAsync(string productId, CancellationToken cancellationToken)
    {
        try
        {
            var encoded = WebUtility.UrlEncode(productId);
            var url = $"{_options.Pchome.SearchApiBase}?q={encoded}&page=1";
            var json = await _httpClient.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Prods", out var prods))
            {
                return null;
            }

            foreach (var prod in prods.EnumerateArray())
            {
                if (!prod.TryGetProperty("Id", out var idElement))
                {
                    continue;
                }

                var id = idElement.GetString() ?? string.Empty;
                if (!string.Equals(id, productId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!prod.TryGetProperty("Name", out var nameElement))
                {
                    return null;
                }

                return NormalizeTitle(nameElement.GetString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "商品 {ProductId} 搜尋 API 取標題失敗。", productId);
        }

        return null;
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
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(property))
        {
            return null;
        }

        var metaTags = Regex.Matches(html, "<meta\\b[^>]*>", RegexOptions.IgnoreCase);
        foreach (Match metaTag in metaTags)
        {
            var tag = metaTag.Value;
            var propMatch = Regex.Match(tag, "\\b(property|name)\\s*=\\s*([\"'])(?<value>.*?)\\2", RegexOptions.IgnoreCase);
            if (!propMatch.Success)
            {
                continue;
            }

            var propValue = propMatch.Groups["value"].Value.Trim();
            if (!string.Equals(propValue, property, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contentMatch = Regex.Match(tag, "\\bcontent\\s*=\\s*([\"'])(?<value>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!contentMatch.Success)
            {
                return null;
            }

            return WebUtility.HtmlDecode(contentMatch.Groups["value"].Value);
        }

        return null;
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
