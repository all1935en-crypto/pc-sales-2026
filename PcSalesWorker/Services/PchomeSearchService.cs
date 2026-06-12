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

    public async Task<string?> GetProductTitleRequiredAsync(string productId, string keyword, CancellationToken cancellationToken)
    {
        var maxAttempts = GetTitleRetryAttempts();
        var delayMs = GetTitleRetryDelayMs();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var title = await GetProductTitleSinglePassAsync(productId, keyword, cancellationToken);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }

                _logger.LogWarning("商品 {ProductId} 第 {Attempt}/{MaxAttempts} 次尚未取得標題。", productId, attempt, maxAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "商品 {ProductId} 第 {Attempt}/{MaxAttempts} 次抓標題失敗。", productId, attempt, maxAttempts);
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(delayMs * attempt, cancellationToken);
            }
        }

        _logger.LogWarning("商品 {ProductId} 無法取得標題（已重試 {MaxAttempts} 次）。", productId, maxAttempts);
        return null;
    }

    public async Task<string?> GetProductTitleAsync(string productId, CancellationToken cancellationToken)
    {
        return await GetProductTitleSinglePassAsync(productId, keyword: string.Empty, cancellationToken);
    }

    private async Task<string?> GetProductTitleSinglePassAsync(string productId, string keyword, CancellationToken cancellationToken)
    {
        var url = $"https://24h.pchome.com.tw/prod/{productId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_options.Chrome.UserAgent);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("商品 {ProductId} 標題頁面回應失敗：{StatusCode}", productId, (int)response.StatusCode);
            return await ResolveTitleFromFallbacksAsync(productId, keyword, cancellationToken);
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var pageHeading = NormalizeTitle(HtmlExtractProductHeading(html));
        if (!string.IsNullOrWhiteSpace(pageHeading))
        {
            return pageHeading;
        }

        var titleFromApi = await ResolveTitleFromFallbacksAsync(productId, keyword, cancellationToken);
        if (!string.IsNullOrWhiteSpace(titleFromApi))
        {
            return titleFromApi;
        }

        var title = HtmlExtractMetaContent(html, "og:title")
            ?? HtmlExtractMetaContent(html, "twitter:title")
            ?? HtmlExtractMetaContent(html, "title")
            ?? HtmlExtractJsonLdName(html)
            ?? HtmlExtractTitle(html);

        var normalized = NormalizeTitle(title);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        _logger.LogWarning("商品 {ProductId} 無法從商品頁與 API 擷取標題。", productId);
        return null;
    }

    private async Task<string?> ResolveTitleFromFallbacksAsync(string productId, string keyword, CancellationToken cancellationToken)
    {
        var fromSearchById = await GetProductTitleFromSearchApiAsync(productId, productId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromSearchById))
        {
            return fromSearchById;
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var fromSearchByKeyword = await GetProductTitleFromSearchApiAsync(keyword, productId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fromSearchByKeyword))
            {
                return fromSearchByKeyword;
            }
        }

        return await GetProductTitleFromProdApiAsync(productId, cancellationToken);
    }

    private async Task<string?> GetProductTitleFromSearchApiAsync(string query, string productId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        try
        {
            var encoded = WebUtility.UrlEncode(query.Trim());
            var url = $"{_options.Pchome.SearchApiBase}?q={encoded}&page=1";
            var json = await _httpClient.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Prods", out var prods))
            {
                return null;
            }

            string? partialMatch = null;
            foreach (var prod in prods.EnumerateArray())
            {
                if (!prod.TryGetProperty("Id", out var idElement))
                {
                    continue;
                }

                var id = idElement.GetString() ?? string.Empty;
                if (!string.Equals(id, productId, StringComparison.OrdinalIgnoreCase))
                {
                    if (partialMatch == null && id.StartsWith(productId, StringComparison.OrdinalIgnoreCase))
                    {
                        partialMatch = NormalizeTitle(prod.TryGetProperty("Name", out var altNameElement) ? altNameElement.GetString() : null);
                    }
                    continue;
                }

                if (!prod.TryGetProperty("Name", out var nameElement))
                {
                    return null;
                }

                return NormalizeTitle(nameElement.GetString());
            }

            if (!string.IsNullOrWhiteSpace(partialMatch))
            {
                return partialMatch;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "商品 {ProductId} 搜尋 API 取標題失敗（query={Query}）。", productId, query);
        }

        return null;
    }

    private async Task<string?> GetProductTitleFromProdApiAsync(string productId, CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            $"https://ecapi.pchome.com.tw/ecshop/prodapi/v2/prod/{productId}&fields=Id,Name,Nick",
            $"https://ecapi.pchome.com.tw/ecshop/prodapi/v2/prod/{productId}&fields=Id,Name,Nick&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        };

        foreach (var url in urls)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var raw = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var start = raw.IndexOf('{');
                var end = raw.LastIndexOf('}');
                if (start < 0 || end <= start)
                {
                    continue;
                }

                var json = raw.Substring(start, end - start + 1);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty(productId, out var productNode) && productNode.ValueKind == JsonValueKind.Object)
                    {
                        var title = ExtractProductName(productNode);
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            return title;
                        }
                    }

                    var topLevelTitle = ExtractProductName(root);
                    if (!string.IsNullOrWhiteSpace(topLevelTitle))
                    {
                        return topLevelTitle;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "商品 {ProductId} Prod API 取標題失敗。", productId);
            }
        }

        return null;
    }

    private static string? ExtractProductName(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (node.TryGetProperty("Name", out var nameElement))
        {
            var name = NormalizeTitle(nameElement.GetString());
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        if (node.TryGetProperty("Nick", out var nickElement))
        {
            var nick = NormalizeTitle(nickElement.GetString());
            if (!string.IsNullOrWhiteSpace(nick))
            {
                return nick;
            }
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

    private static string? HtmlExtractProductHeading(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var headingMatch = Regex.Match(
            html,
            "<h1\\b[^>]*>(?<value>.*?)</h1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!headingMatch.Success)
        {
            return null;
        }

        var text = Regex.Replace(headingMatch.Groups["value"].Value, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? HtmlExtractJsonLdName(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var scripts = Regex.Matches(
            html,
            "<script\\b[^>]*type\\s*=\\s*([\"'])application/ld\\+json\\1[^>]*>(?<json>.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match script in scripts)
        {
            try
            {
                var json = script.Groups["json"].Value.Trim();
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(json);
                var name = FindNameInJson(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return WebUtility.HtmlDecode(name);
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string? FindNameInJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    if (element.TryGetProperty("@type", out var typeElement))
                    {
                        var type = typeElement.GetString() ?? string.Empty;
                        if (type.Contains("Product", StringComparison.OrdinalIgnoreCase)
                            && element.TryGetProperty("name", out var nameElement))
                        {
                            var candidate = nameElement.GetString();
                            if (!string.IsNullOrWhiteSpace(candidate))
                            {
                                return candidate;
                            }
                        }
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        var nested = FindNameInJson(property.Value);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            return nested;
                        }
                    }

                    return null;
                }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindNameInJson(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }

                return null;
            default:
                return null;
        }
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

    private static int GetTitleRetryAttempts()
    {
        var raw = Environment.GetEnvironmentVariable("TITLE_RETRY_ATTEMPTS");
        if (int.TryParse(raw, out var value) && value >= 1)
        {
            return value;
        }

        return 8;
    }

    private static int GetTitleRetryDelayMs()
    {
        var raw = Environment.GetEnvironmentVariable("TITLE_RETRY_DELAY_MS");
        if (int.TryParse(raw, out var value) && value >= 100)
        {
            return value;
        }

        return 500;
    }
}
