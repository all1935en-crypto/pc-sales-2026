using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PcSalesWorker.Models;

namespace PcSalesWorker.Services;

public sealed class PchomeBackendService : IAsyncDisposable
{
    private readonly ILogger<PchomeBackendService> _logger;
    private readonly AppOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private PchomeCredential? _credential;
    private string? _lastAttentionText;

    public PchomeBackendService(ILogger<PchomeBackendService> logger, IOptions<AppOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int?> GetSales30Async(string productId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await EnsureContextAsync(cancellationToken);
            var page = await GetPageAsync(cancellationToken);
            await EnsureLoggedInAsync(page, cancellationToken);
            if (!await IsReportReadyAsync(page, cancellationToken))
            {
                MessageBox.Show("尚未完成登入或報表頁尚未就緒，請確認登入/OTP 後再繼續。", "需要人工確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            await NavigateToReportAsync(page, cancellationToken);
            var matched = await FillQueryAsync(page, productId, cancellationToken);
            if (!matched)
            {
                return null;
            }
            var frame = await FindReportFrameAsync(page, cancellationToken);
            var zeroCheck = await CheckZeroSalesBannerAsync(page, frame, cancellationToken);
            if (zeroCheck.IsZero)
            {
                LogZeroSalesDiagnostics(zeroCheck);
                _logger.LogInformation("零銷量判定，商品 {ProductId} 回寫 0。", productId);
                return 0;
            }
            if (!await TableContainsProductIdAsync(frame, productId, cancellationToken))
            {
                _logger.LogWarning("查詢結果未包含商品ID，略過本次商品：{ProductId}", productId);
                return null;
            }
            var sum = await SumTotalColumnAsync(frame, productId, cancellationToken);
            if (sum.HasValue)
            {
                _logger.LogInformation("前台 DOM 計算總計合計: {Sum}", sum.Value);
            }

            return sum;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureContextAsync(CancellationToken cancellationToken)
    {
        if (_context != null && _playwright != null)
        {
            return;
        }

        var userDataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.Chrome.UserDataDir));
        Directory.CreateDirectory(userDataDir);

        _playwright = await Playwright.CreateAsync();
        var launchOptions = new BrowserTypeLaunchPersistentContextOptions
        {
            Channel = "chrome",
            Headless = false,
            UserAgent = _options.Chrome.UserAgent,
            ViewportSize = new ViewportSize { Width = _options.Chrome.WindowWidth, Height = _options.Chrome.WindowHeight },
            AcceptDownloads = true,
            Args = new[]
            {
                $"--window-size={_options.Chrome.WindowWidth},{_options.Chrome.WindowHeight}",
                $"--profile-directory={_options.Chrome.ProfileDirectory}",
                "--disable-blink-features=AutomationControlled",
                "--disable-features=IsolateOrigins,site-per-process"
            }
        };

        _context = await _playwright.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions);
        await _context.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            window.chrome = { runtime: {} };
            const originalQuery = window.navigator.permissions.query;
            window.navigator.permissions.query = (parameters) => (
                parameters.name === 'notifications'
                    ? Promise.resolve({ state: Notification.permission })
                    : originalQuery(parameters)
            );
        ");
    }

    private async Task<IPage> GetPageAsync(CancellationToken cancellationToken)
    {
        if (_context == null)
        {
            throw new InvalidOperationException("瀏覽器尚未建立。");
        }

        if (_page == null || _page.IsClosed)
        {
            _page = _context.Pages.FirstOrDefault(p => !p.IsClosed) ?? await _context.NewPageAsync();
        }

        if (ShouldBringBrowserFront())
        {
            try
            {
                await _page.BringToFrontAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "無法將瀏覽器視窗置頂");
            }
        }
        return _page;
    }

    private async Task EnsureLoggedInAsync(IPage page, CancellationToken cancellationToken)
    {
        if (await IsReportReadyAsync(page, cancellationToken))
        {
            return;
        }

        if (!page.Url.Contains("ecvdr.pchome.com.tw", StringComparison.OrdinalIgnoreCase)
            || !page.Url.Contains("007_001", StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync(_options.Pchome.ReportUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }

        if (await IsReportReadyAsync(page, cancellationToken))
        {
            return;
        }

        if (await IsLoginPageAsync(page, cancellationToken))
        {
            if (IsAutoLoginEnabled())
            {
                _credential ??= PchomeCredential.LoadFromEnvFile(Path.Combine(AppContext.BaseDirectory, ".env"));
                await TryFillLoginAsync(page, _credential, cancellationToken);
                await TryClickLoginAsync(page, cancellationToken);
                await WaitForOtpIfNeededAsync(page, cancellationToken);
            }
            else
            {
                _logger.LogWarning("偵測到登入頁，請手動登入。將等待登入完成後再繼續。");
            }
        }

        await WaitForReportReadyAsync(page, cancellationToken);
    }

    private static async Task<bool> IsLoginPageAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var inputs = await page.QuerySelectorAllAsync("input");
            var score = 0;
            foreach (var input in inputs)
            {
                var label = string.Join(" ", new[]
                {
                    await input.GetAttributeAsync("name"),
                    await input.GetAttributeAsync("id"),
                    await input.GetAttributeAsync("placeholder"),
                    await input.GetAttributeAsync("aria-label")
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

                if (label.Contains("廠商", StringComparison.OrdinalIgnoreCase))
                {
                    score++;
                }

                if (label.Contains("使用者", StringComparison.OrdinalIgnoreCase) || label.Contains("帳號", StringComparison.OrdinalIgnoreCase))
                {
                    score++;
                }

                if (label.Contains("密碼", StringComparison.OrdinalIgnoreCase) || label.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    score++;
                }
            }

            if (score >= 2)
            {
                return true;
            }

            var loginButton = page.Locator("button:has-text('登入')").First;
            return await loginButton.CountAsync() > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsReportReadyAsync(IPage page, CancellationToken cancellationToken)
    {
        if (await IsLoginPageAsync(page, cancellationToken))
        {
            return false;
        }

        if (page.Url.Contains("007_001", StringComparison.OrdinalIgnoreCase))
        {
            var frame = await FindReportFrameAsync(page, cancellationToken);
            try
            {
                var inputs = await frame.QuerySelectorAllAsync("input");
                if (inputs.Count > 0)
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private async Task WaitForReportReadyAsync(IPage page, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.Now.AddMinutes(5);
        while (DateTimeOffset.Now < timeoutAt)
        {
            if (await IsAnnouncementPageAsync(page, cancellationToken))
            {
                await page.GotoAsync(_options.Pchome.ReportUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            }

            if (await IsReportReadyAsync(page, cancellationToken))
            {
                return;
            }

            await Task.Delay(1000, cancellationToken);
        }

        MessageBox.Show("等待登入逾時，請確認是否已完成登入/OTP。", "需要人工確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static async Task<bool> IsAnnouncementPageAsync(IPage page, CancellationToken cancellationToken)
    {
        var confirmButton = page.Locator("button:has-text('確定送出'), input[type='button'][value*='確定']").First;
        if (await confirmButton.CountAsync() == 0)
        {
            return false;
        }

        var agreeLabels = page.Locator("label:has-text('我同意')");
        return await agreeLabels.CountAsync() > 0;
    }


    private async Task TryFillLoginAsync(IPage page, PchomeCredential credential, CancellationToken cancellationToken)
    {
        var vendorInput = page.Locator("#userAccount").First;
        var userInput = page.Locator("#subUser").First;
        var passInput = page.Locator("#userPassword").First;
        var rememberMe = page.Locator("#rememberMe").First;

        if (await vendorInput.CountAsync() > 0)
        {
            await vendorInput.FillAsync(credential.VendorId);
        }

        if (await userInput.CountAsync() > 0)
        {
            await userInput.FillAsync(credential.UserId);
        }

        if (await passInput.CountAsync() > 0)
        {
            await passInput.FillAsync(credential.Password);
        }

        if (await rememberMe.CountAsync() > 0)
        {
            try
            {
                if (!await rememberMe.IsCheckedAsync())
                {
                    await rememberMe.ClickAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "無法勾選記住帳號");
            }
        }

        if (await vendorInput.CountAsync() == 0 || await userInput.CountAsync() == 0 || await passInput.CountAsync() == 0)
        {
            _logger.LogWarning("登入欄位未完全填入，將持續嘗試或等待登入完成。");
        }
    }

    private static async Task TryClickLoginAsync(IPage page, CancellationToken cancellationToken)
    {
        var loginButton = page.Locator("button:has-text('登入'), input[type='submit'][value*='登入'], #loginBtn, #login, button#login").First;
        if (await loginButton.CountAsync() > 0)
        {
            await loginButton.ClickAsync();
            return;
        }

        var submitButton = page.Locator("input[type='submit']").First;
        if (await submitButton.CountAsync() > 0)
        {
            await submitButton.ClickAsync();
        }
    }

    private async Task WaitForOtpIfNeededAsync(IPage page, CancellationToken cancellationToken)
    {
        await Task.Delay(1000, cancellationToken);
        var otpInput = page.Locator("input[placeholder*='OTP'], input[placeholder*='驗證'], input[name*='otp'], input[id*='otp']");
        if (await otpInput.CountAsync() > 0)
        {
            MessageBox.Show("請完成 OTP 驗證後按下確定。", "需要 OTP", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 60000 });
    }

    private async Task NavigateToReportAsync(IPage page, CancellationToken cancellationToken)
    {
        if (!page.Url.Contains("007_001", StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync(_options.Pchome.ReportUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
    }

    private async Task<bool> FillQueryAsync(IPage page, string productId, CancellationToken cancellationToken)
    {
        var taiwanNow = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, TimeZoneHelper.Resolve(_options.Schedule.Timezone));
        var endDate = taiwanNow.Date.AddDays(-1);
        var startDate = endDate.AddDays(-29);
        var startText = startDate.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var endText = endDate.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);

        var frame = await FindReportFrameAsync(page, cancellationToken);
        _logger.LogInformation("報表Frame: {Url}", frame.Url);
        await EnsureTimeRangeSelectedAsync(frame, cancellationToken);
        await FillDateInputsAsync(frame, startText, endText, cancellationToken);
        await FillProductIdAsync(frame, productId, cancellationToken);
        if (IsPauseBeforeSearchEnabled())
        {
            _logger.LogWarning("已暫停在查詢前，請手動按下「查詢」。完成後會自動繼續。");
            await WaitForManualSearchAsync(frame, cancellationToken);
            return true;
        }
        else
        {
            await ClickSearchAsync(frame, cancellationToken);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 60000 });
            if (await TryShortZeroSalesDetectAsync(page, frame, cancellationToken))
            {
                return true;
            }
            try
            {
                await frame.WaitForFunctionAsync(
                    @"() => {
                        const tables = Array.from(document.querySelectorAll('table'));
                        for (const t of tables) {
                            const head = Array.from(t.querySelectorAll('thead th')).map(th => (th.textContent || '').trim());
                            const bodyRows = t.querySelectorAll('tbody tr').length;
                            if (head.some(h => h.includes('總計')) || bodyRows > 0) return true;
                        }
                        return false;
                    }",
                    null,
                    new FrameWaitForFunctionOptions { Timeout = 60000 });
            }
            catch
            {
                // ignore
            }

            return await EnsureResultsMatchWithRetryAsync(page, frame, productId, cancellationToken);
        }
    }

    private async Task FillDateInputsAsync(IFrame frame, string startText, string endText, CancellationToken cancellationToken)
    {
        await EnsureDateRangeModeAsync(frame, cancellationToken);
        var inputs = await frame.QuerySelectorAllAsync("input");
        var candidates = new List<IElementHandle>();
        foreach (var input in inputs)
        {
            var type = (await input.GetAttributeAsync("type")) ?? string.Empty;
            if (!type.Equals("text", StringComparison.OrdinalIgnoreCase) && !type.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = string.Join(" ", new[]
            {
                await input.GetAttributeAsync("name"),
                await input.GetAttributeAsync("id"),
                await input.GetAttributeAsync("placeholder"),
                await input.GetAttributeAsync("aria-label")
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (label.Contains("開始", StringComparison.OrdinalIgnoreCase) || label.Contains("結束", StringComparison.OrdinalIgnoreCase)
                || label.Contains("start", StringComparison.OrdinalIgnoreCase) || label.Contains("end", StringComparison.OrdinalIgnoreCase)
                || label.Contains("from", StringComparison.OrdinalIgnoreCase) || label.Contains("to", StringComparison.OrdinalIgnoreCase)
                || label.Contains("日期", StringComparison.OrdinalIgnoreCase) || label.Contains("yyyy", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(input);
            }
        }

        if (candidates.Count < 2)
        {
            var textInputs = new List<IElementHandle>();
            foreach (var input in inputs)
            {
                var type = (await input.GetAttributeAsync("type")) ?? string.Empty;
                if (type.Equals("text", StringComparison.OrdinalIgnoreCase) || type.Equals("date", StringComparison.OrdinalIgnoreCase))
                {
                    textInputs.Add(input);
                }
            }

            if (textInputs.Count >= 2)
            {
                candidates = textInputs.Take(2).ToList();
            }
        }

        if (candidates.Count >= 2)
        {
            await SetInputValueAsync(frame, candidates[0], startText, cancellationToken);
            await SetInputValueAsync(frame, candidates[1], endText, cancellationToken);
            var ok = await ValidateDateInputsAsync(frame, startText, endText, cancellationToken);
            if (!ok)
            {
                await ForceSetDateInputsAsync(frame, startText, endText, cancellationToken);
            }
        }
        else
        {
            await LogInputDiagnosticsAsync(frame, cancellationToken);
            await LogSelectDiagnosticsAsync(frame, cancellationToken);
            await ForceSetDateInputsAsync(frame, startText, endText, cancellationToken);
        }
    }

    private static async Task<bool> ValidateDateInputsAsync(IFrame frame, string startText, string endText, CancellationToken cancellationToken)
    {
        try
        {
            var values = await frame.EvaluateAsync<string[]>(
                @"() => Array.from(document.querySelectorAll('input'))
                    .map(i => i.value || '')");
            return values.Any(v => v.Contains(startText, StringComparison.OrdinalIgnoreCase))
                && values.Any(v => v.Contains(endText, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static async Task ForceSetDateInputsAsync(IFrame frame, string startText, string endText, CancellationToken cancellationToken)
    {
        try
        {
            await frame.EvaluateAsync(
                @"(args) => {
                    const inputs = Array.from(document.querySelectorAll('input'));
                    const dateInputs = inputs.filter(i => {
                        const t = (i.getAttribute('type') || '').toLowerCase();
                        const p = (i.getAttribute('placeholder') || '') + ' ' + (i.getAttribute('aria-label') || '') + ' ' + (i.getAttribute('name') || '') + ' ' + (i.getAttribute('id') || '');
                        return t === 'text' || t === 'date' || /日期|yyyy|start|end|from|to/i.test(p);
                    });
                    if (dateInputs.length >= 2) {
                        dateInputs[0].removeAttribute('readonly');
                        dateInputs[1].removeAttribute('readonly');
                        dateInputs[0].value = args.start;
                        dateInputs[1].value = args.end;
                        dateInputs[0].dispatchEvent(new Event('input', { bubbles: true }));
                        dateInputs[0].dispatchEvent(new Event('change', { bubbles: true }));
                        dateInputs[0].dispatchEvent(new Event('blur', { bubbles: true }));
                        dateInputs[1].dispatchEvent(new Event('input', { bubbles: true }));
                        dateInputs[1].dispatchEvent(new Event('change', { bubbles: true }));
                        dateInputs[1].dispatchEvent(new Event('blur', { bubbles: true }));
                    }
                }",
                new { start = startText, end = endText });
        }
        catch
        {
            // ignore
        }
    }

    private async Task FillProductIdAsync(IFrame frame, string productId, CancellationToken cancellationToken)
    {
        var inputs = await frame.QuerySelectorAllAsync("input");
        foreach (var input in inputs)
        {
            var label = string.Join(" ", new[]
            {
                await input.GetAttributeAsync("name"),
                await input.GetAttributeAsync("id"),
                await input.GetAttributeAsync("placeholder"),
                await input.GetAttributeAsync("aria-label")
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (label.Contains("商品", StringComparison.OrdinalIgnoreCase) || label.Contains("商品ID", StringComparison.OrdinalIgnoreCase) || label.Contains("產品", StringComparison.OrdinalIgnoreCase))
            {
                await input.FillAsync(productId);
                return;
            }
        }

        await LogInputDiagnosticsAsync(frame, cancellationToken);
        MessageBox.Show("找不到商品ID輸入框，請手動輸入後按下查詢。", "需要人工確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static async Task SetInputValueAsync(IFrame frame, IElementHandle input, string value, CancellationToken cancellationToken)
    {
        try
        {
            await input.ClickAsync();
            await input.PressAsync("Control+A");
            await input.TypeAsync(value, new ElementHandleTypeOptions { Delay = 30 });
            await input.PressAsync("Tab");
            await frame.EvaluateAsync(
                @"(el) => {
                    try { el.dispatchEvent(new Event('input', { bubbles: true })); } catch (e) {}
                    try { el.dispatchEvent(new Event('change', { bubbles: true })); } catch (e) {}
                    try { el.dispatchEvent(new Event('blur', { bubbles: true })); } catch (e) {}
                }",
                input);
        }
        catch
        {
            await frame.EvaluateAsync(
                @"(args) => {
                    const el = args.el;
                    try { el.removeAttribute('readonly'); } catch (e) {}
                    el.value = args.value;
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                    el.dispatchEvent(new Event('blur', { bubbles: true }));
                }",
                new { el = input, value });
        }
    }

    private async Task EnsureDateRangeModeAsync(IFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            var selects = frame.Locator("select");
            var count = await selects.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var select = selects.Nth(i);
                var options = select.Locator("option");
                var optionCount = await options.CountAsync();
                for (var j = 0; j < optionCount; j++)
                {
                    var option = options.Nth(j);
                    var text = (await option.InnerTextAsync()).Trim();
                    if (text.Contains("自選日期", StringComparison.OrdinalIgnoreCase) || text.Contains("一年", StringComparison.OrdinalIgnoreCase))
                    {
                        await select.SelectOptionAsync(new[] { new SelectOptionValue { Label = text } });
                        return;
                    }
                }
            }

            var customOption = frame.Locator("text=自選日期").First;
            if (await customOption.CountAsync() > 0)
            {
                await customOption.ClickAsync();
                await frame.WaitForTimeoutAsync(300);
                return;
            }

            var oneYearOption = frame.Locator("text=一年內").First;
            if (await oneYearOption.CountAsync() > 0)
            {
                await oneYearOption.ClickAsync();
                await frame.WaitForTimeoutAsync(300);
                return;
            }

            var labelOption = frame.Locator("label:has-text('自選日期')").First;
            if (await labelOption.CountAsync() > 0)
            {
                await labelOption.ClickAsync();
                await frame.WaitForTimeoutAsync(300);
                return;
            }

            var rangeLabel = frame.Locator("label:has-text('查詢日期'), label:has-text('時間'), label:has-text('區間')").First;
            if (await rangeLabel.CountAsync() > 0)
            {
                await rangeLabel.ClickAsync();
                await frame.WaitForTimeoutAsync(300);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "日期模式切換失敗");
        }
    }

    private async Task EnsureTimeRangeSelectedAsync(IFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            var selectedByScript = await frame.EvaluateAsync<bool>(
                @"() => {
                    const selects = Array.from(document.querySelectorAll('select'));
                    const visible = selects.filter(s => s.getClientRects().length > 0 && s.offsetParent !== null);
                    const candidates = visible.length > 0 ? visible : selects;
                    for (const sel of candidates) {
                        const options = Array.from(sel.options || []);
                        let idx = options.findIndex(o => /自選日期/i.test(o.text || ''));
                        if (idx < 0) idx = options.findIndex(o => /一年/i.test(o.text || ''));
                        if (idx >= 0) {
                            if (sel.selectedIndex === idx && options.length > 1) {
                                const alt = idx === 0 ? 1 : 0;
                                sel.selectedIndex = alt;
                                sel.dispatchEvent(new Event('input', { bubbles: true }));
                                sel.dispatchEvent(new Event('change', { bubbles: true }));
                            }
                            sel.selectedIndex = idx;
                            sel.dispatchEvent(new Event('input', { bubbles: true }));
                            sel.dispatchEvent(new Event('change', { bubbles: true }));
                            return true;
                        }
                    }
                    return false;
                }");
            if (selectedByScript)
            {
                var selectedText = await frame.EvaluateAsync<string?>(
                    @"() => {
                        const selects = Array.from(document.querySelectorAll('select'));
                        for (const sel of selects) {
                            const opt = sel.options && sel.options[sel.selectedIndex];
                            const text = opt ? (opt.text || '').trim() : '';
                            if (/自選日期|一年/i.test(text)) return text;
                        }
                        return null;
                    }");
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    _logger.LogInformation("查詢時間範圍已選擇: {Text}", selectedText);
                }
                return;
            }

            var selects = frame.Locator("select");
            var count = await selects.CountAsync();
            if (count >= 2)
            {
                var timeSelect = selects.Nth(1);
                var options = timeSelect.Locator("option");
                var optionCount = await options.CountAsync();
                for (var j = 0; j < optionCount; j++)
                {
                    var option = options.Nth(j);
                    var text = (await option.InnerTextAsync()).Trim();
                    if (text.Contains("自選日期", StringComparison.OrdinalIgnoreCase))
                    {
                        await timeSelect.SelectOptionAsync(new[] { new SelectOptionValue { Label = text } });
                        return;
                    }
                }
            }

            for (var i = 0; i < count; i++)
            {
                var select = selects.Nth(i);
                var options = select.Locator("option");
                var optionCount = await options.CountAsync();
                for (var j = 0; j < optionCount; j++)
                {
                    var option = options.Nth(j);
                    var text = (await option.InnerTextAsync()).Trim();
                    if (text.Contains("自選", StringComparison.OrdinalIgnoreCase) || text.Contains("一年", StringComparison.OrdinalIgnoreCase) || text.Contains("範圍", StringComparison.OrdinalIgnoreCase))
                    {
                        await select.SelectOptionAsync(new[] { new SelectOptionValue { Label = text } });
                        return;
                    }
                }
            }

            // 如果頁面顯示「請選擇查詢時間範圍」，再嘗試點擊下拉
            var warning = frame.Locator("text=請選擇查詢時間範圍").First;
            if (await warning.CountAsync() > 0 && count > 0)
            {
                var target = count >= 2 ? selects.Nth(1) : selects.First;
                await target.ClickAsync();
                await target.PressAsync("ArrowDown");
                await target.PressAsync("Enter");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查詢時間範圍選擇失敗");
        }
    }

    private async Task LogSelectDiagnosticsAsync(IFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            var selects = await frame.QuerySelectorAllAsync("select");
            var index = 0;
            foreach (var select in selects)
            {
                index++;
                var options = await select.QuerySelectorAllAsync("option");
                var texts = new List<string>();
                foreach (var option in options)
                {
                    var text = (await option.InnerTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        texts.Add(text);
                    }
                }

                _logger.LogWarning("下拉選單[{Index}] 選項: {Options}", index, string.Join(" | ", texts));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下拉選單診斷失敗");
        }
    }

    private static async Task ClickSearchAsync(IFrame frame, CancellationToken cancellationToken)
    {
        var delayMs = GetSearchClickDelayMs();
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        var searchButton = frame.Locator("button:has-text('查詢')").First;
        if (await searchButton.CountAsync() > 0)
        {
            await searchButton.ClickAsync();
            return;
        }

        var altButton = frame.Locator("input[type='submit']").First;
        if (await altButton.CountAsync() > 0)
        {
            await altButton.ClickAsync();
        }
    }

    private static int GetSearchClickDelayMs()
    {
        var raw = Environment.GetEnvironmentVariable("SEARCH_CLICK_DELAY_MS");
        if (int.TryParse(raw, out var value) && value >= 0)
        {
            return value;
        }

        return 1000;
    }

    private async Task<bool> EnsureResultsMatchWithRetryAsync(IPage page, IFrame frame, string productId, CancellationToken cancellationToken)
    {
        var baseId = productId.Trim();
        if (string.IsNullOrWhiteSpace(baseId))
        {
            return true;
        }

        var zeroCheck = await CheckZeroSalesBannerAsync(page, frame, cancellationToken);
        if (zeroCheck.IsZero)
        {
            return true;
        }

        var maxAttempts = GetResultMatchMaxAttempts();
        var timeoutMs = GetResultMatchTimeoutMs();
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await frame.WaitForFunctionAsync(
                    @"(pid) => {
                        const rows = Array.from(document.querySelectorAll('td[field=""ProdId""]'));
                        const matched = rows.some(td => (td.textContent || '').trim().startsWith(pid));
                        return matched;
                    }",
                    baseId,
                    new FrameWaitForFunctionOptions { Timeout = timeoutMs });
                return true;
            }
            catch (Exception ex)
            {
                zeroCheck = await CheckZeroSalesBannerAsync(page, frame, cancellationToken);
                if (zeroCheck.IsZero)
                {
                    return true;
                }

                _logger.LogWarning(ex, "查詢結果未與商品ID對齊（第 {Attempt} 次）：{ProductId}", attempt, productId);
                if (attempt < maxAttempts)
                {
                    await Task.Delay(600, cancellationToken);
                    await ClickSearchAsync(frame, cancellationToken);
                }
            }
        }

        _logger.LogWarning("查詢結果仍未對齊，將略過本次商品：{ProductId}", productId);
        return false;
    }

    private sealed class ZeroSalesDiagnostics
    {
        public bool IsZero { get; set; }
        public bool HasAttention { get; set; }
        public string? AttentionText { get; set; }
        public string? AttentionHtml { get; set; }
        public bool HasRows { get; set; }
        public bool HasProd { get; set; }
        public bool BodyHasPhrase { get; set; }
    }

    private void LogAttentionIfChanged(ZeroSalesDiagnostics diag)
    {
        if (!diag.HasAttention)
        {
            return;
        }

        var text = diag.AttentionText ?? string.Empty;
        if (string.Equals(text, _lastAttentionText, StringComparison.Ordinal))
        {
            return;
        }

        _lastAttentionText = text;
        var display = string.IsNullOrWhiteSpace(text) ? "空白" : text;
        var html = string.IsNullOrWhiteSpace(diag.AttentionHtml) ? "空白" : diag.AttentionHtml;
        if (html.Length > 200)
        {
            html = html.Substring(0, 200) + "...";
        }
        _logger.LogInformation("#ui-attention 出現：{Text} (html={Html})", display, html);
    }

    private void LogZeroSalesDiagnostics(ZeroSalesDiagnostics diag)
    {
        var attentionText = string.IsNullOrWhiteSpace(diag.AttentionText) ? "空白" : diag.AttentionText;
        _logger.LogInformation(
            "偵測到零銷量提示，略過結果對齊等待。attention={HasAttention}, attentionText={AttentionText}, hasRows={HasRows}, hasProd={HasProd}, bodyHasPhrase={BodyHasPhrase}",
            diag.HasAttention,
            attentionText,
            diag.HasRows,
            diag.HasProd,
            diag.BodyHasPhrase);
    }

    private async Task<ZeroSalesDiagnostics> CheckZeroSalesBannerAsync(IPage page, IFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            var inFrame = await frame.EvaluateAsync<ZeroSalesDiagnostics>(
                @"() => {
                    const text = document.body ? (document.body.innerText || '') : '';
                    const attention = document.querySelector('#ui-attention');
                    const attentionText = attention ? (attention.textContent || '').trim() : '';
                    const attentionHtml = attention ? (attention.innerHTML || '').trim() : '';
                    const bodyHasPhrase = /查詢時間區間內/.test(text) && /賣出數/.test(text) && /為\s*0/.test(text);
                    const hasRows = document.querySelectorAll('table tbody tr').length > 0;
                    const hasProd = document.querySelectorAll('td[field=""ProdId""]').length > 0;
                    const isZero = bodyHasPhrase
                        || (attentionText && /賣出數/.test(attentionText) && /為\s*0/.test(attentionText))
                        || (attentionHtml && /賣出數/.test(attentionHtml) && /為\s*0/.test(attentionHtml))
                        || (attention && !hasRows && !hasProd);
                    return {
                        isZero,
                        hasAttention: !!attention,
                        attentionText,
                        attentionHtml,
                        hasRows,
                        hasProd,
                        bodyHasPhrase
                    };
                }");
            if (inFrame != null)
            {
                LogAttentionIfChanged(inFrame);
                return inFrame;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var inPage = await page.EvaluateAsync<ZeroSalesDiagnostics>(
                @"() => {
                    const text = document.body ? (document.body.innerText || '') : '';
                    const attention = document.querySelector('#ui-attention');
                    const attentionText = attention ? (attention.textContent || '').trim() : '';
                    const attentionHtml = attention ? (attention.innerHTML || '').trim() : '';
                    const bodyHasPhrase = /查詢時間區間內/.test(text) && /賣出數/.test(text) && /為\s*0/.test(text);
                    const hasRows = document.querySelectorAll('table tbody tr').length > 0;
                    const hasProd = document.querySelectorAll('td[field=""ProdId""]').length > 0;
                    const isZero = bodyHasPhrase
                        || (attentionText && /賣出數/.test(attentionText) && /為\s*0/.test(attentionText))
                        || (attentionHtml && /賣出數/.test(attentionHtml) && /為\s*0/.test(attentionHtml))
                        || (attention && !hasRows && !hasProd);
                    return {
                        isZero,
                        hasAttention: !!attention,
                        attentionText,
                        attentionHtml,
                        hasRows,
                        hasProd,
                        bodyHasPhrase
                    };
                }");
            if (inPage != null)
            {
                LogAttentionIfChanged(inPage);
                return inPage;
            }
        }
        catch
        {
            // ignore
        }

        return new ZeroSalesDiagnostics { IsZero = false };
    }

    private async Task<bool> TryShortZeroSalesDetectAsync(IPage page, IFrame frame, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var diag = await CheckZeroSalesBannerAsync(page, frame, cancellationToken);
            if (diag.IsZero)
            {
                return true;
            }

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }

    private static int GetResultMatchTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("RESULT_MATCH_TIMEOUT_MS");
        if (int.TryParse(raw, out var value) && value >= 1000)
        {
            return value;
        }

        return 8000;
    }

    private static int GetResultMatchMaxAttempts()
    {
        var raw = Environment.GetEnvironmentVariable("RESULT_MATCH_RETRY");
        if (int.TryParse(raw, out var value) && value >= 1)
        {
            return value;
        }

        return 2;
    }

    private static bool IsPauseBeforeSearchEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("PAUSE_BEFORE_SEARCH"), "1", StringComparison.OrdinalIgnoreCase);

    private async Task WaitForManualSearchAsync(IFrame frame, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.Now.AddMinutes(10);
        while (DateTimeOffset.Now < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rows = frame.Locator("table tbody tr");
                if (await rows.CountAsync() > 0)
                {
                    _logger.LogInformation("偵測到查詢結果，繼續流程。");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "等待手動查詢時發生例外，將重試。");
            }

            await Task.Delay(500, cancellationToken);
        }

        _logger.LogWarning("等待手動查詢逾時（10 分鐘），本次流程將結束。");
    }

    private async Task<int?> SumTotalColumnAsync(IFrame frame, string productId, CancellationToken cancellationToken)
    {
        try
        {
            var domResult = await frame.EvaluateAsync<DomSumResult>(
                @"(pid) => {
                    const target = (pid || '').trim();
                    const totalCells = Array.from(document.querySelectorAll('td[field=""Total""]'));
                    const prodCells = Array.from(document.querySelectorAll('td[field=""ProdId""]'));
                    if (target && prodCells.length > 0) {
                        const rows = Array.from(document.querySelectorAll('table tr'));
                        let sum = 0;
                        const samples = [];
                        for (const row of rows) {
                            const prodCell = row.querySelector('td[field=""ProdId""]');
                            const totalCell = row.querySelector('td[field=""Total""]');
                            if (!prodCell || !totalCell) continue;
                            const prodText = (prodCell.textContent || '').trim();
                            if (!prodText.startsWith(target)) continue;
                            const text = (totalCell.textContent || '').trim();
                            if (samples.length < 5) samples.push(text);
                            const cleaned = text.replace(/,/g, '').replace(/[^\d\-]/g, '');
                            if (cleaned && !isNaN(Number(cleaned))) {
                                sum += Number(cleaned);
                            }
                        }
                        return { sum, samples };
                    }
                    if (target) {
                        return { sum: -1, samples: [] };
                    }

                    const tables = Array.from(document.querySelectorAll('table'));
                    let bestSum = -1;
                    let bestSamples = [];

                    for (const table of tables) {
                        const headerCells = Array.from(table.querySelectorAll('thead th, thead td'));
                        let headers = headerCells.length > 0
                            ? headerCells
                            : Array.from(table.querySelectorAll('tr:first-child th, tr:first-child td'));

                        if (headers.length === 0) continue;

                        let totalIndex = -1;
                        for (const h of headers) {
                            const text = (h.textContent || '').trim();
                            if (text.includes('總計')) {
                                totalIndex = h.cellIndex ?? -1;
                                break;
                            }
                        }

                        if (totalIndex < 0) continue;

                        const rows = Array.from(table.querySelectorAll('tbody tr'));
                        if (rows.length === 0) continue;

                        let sum = 0;
                        const samples = [];
                        for (const row of rows) {
                            const cells = Array.from(row.querySelectorAll('td'));
                            const cell = cells[totalIndex];
                            if (!cell) continue;
                            const text = (cell.textContent || '').trim();
                            if (samples.length < 5) samples.push(text);
                            const cleaned = text.replace(/,/g, '').replace(/[^\d\-]/g, '');
                            if (cleaned && !isNaN(Number(cleaned))) {
                                sum += Number(cleaned);
                            }
                        }

                        if (sum > bestSum) {
                            bestSum = sum;
                            bestSamples = samples;
                        }
                    }

                    return { sum: bestSum, samples: bestSamples };
                }",
                productId);

            if (domResult != null && domResult.Sum >= 0)
            {
                if (domResult.Sum == 0 && domResult.Samples.Length > 0)
                {
                    _logger.LogWarning("總計欄位樣本（前 5 筆）: {Samples}", string.Join(" | ", domResult.Samples));
                }
                return domResult.Sum;
            }

            if (domResult != null && domResult.Sum < 0 && !string.IsNullOrWhiteSpace(productId))
            {
                return null;
            }
        }
        catch
        {
            // ignore and fallback
        }

        if (!string.IsNullOrWhiteSpace(productId))
        {
            return null;
        }

        var tables = frame.Locator("table");
        var tableCount = await tables.CountAsync();
        if (tableCount == 0)
        {
            return null;
        }

        for (var t = 0; t < tableCount; t++)
        {
            var table = tables.Nth(t);
            var headers = table.Locator("thead th");
            var headerCount = await headers.CountAsync();
            if (headerCount == 0)
            {
                headers = table.Locator("tr").First.Locator("th,td");
                headerCount = await headers.CountAsync();
            }

            var totalIndex = -1;
            var hasProductId = false;
            for (var i = 0; i < headerCount; i++)
            {
                var text = (await headers.Nth(i).InnerTextAsync()).Trim();
                if (text.Contains("商品ID", StringComparison.OrdinalIgnoreCase) || text.Contains("商品", StringComparison.OrdinalIgnoreCase))
                {
                    hasProductId = true;
                }
                if (text.Contains("總計", StringComparison.OrdinalIgnoreCase))
                {
                    totalIndex = i;
                }
            }

            if (totalIndex < 0 || !hasProductId)
            {
                continue;
            }

            var rows = table.Locator("tbody tr");
            var rowCount = await rows.CountAsync();
            if (rowCount == 0)
            {
                continue;
            }

            var sum = 0;
            for (var i = 0; i < rowCount; i++)
            {
                var cell = rows.Nth(i).Locator("td").Nth(totalIndex);
                var text = (await cell.InnerTextAsync()).Trim();
                var cleaned = new string(text.Where(c => char.IsDigit(c) || c == '-').ToArray());
                if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    sum += value;
                }
            }

            return sum;
        }

        return null;
    }

    private static async Task<bool> TableContainsProductIdAsync(IFrame frame, string productId, CancellationToken cancellationToken)
    {
        try
        {
            return await frame.EvaluateAsync<bool>(
                @"(pid) => {
                    const target = (pid || '').trim();
                    if (!target) return false;
                    const cells = Array.from(document.querySelectorAll('td[field=""ProdId""], td[data-field=""ProdId""]'));
                    return cells.some(c => (c.textContent || '').trim().startsWith(target));
                }",
                productId);
        }
        catch
        {
            return false;
        }
    }

    private sealed class DomSumResult
    {
        public int Sum { get; set; }
        public string[] Samples { get; set; } = Array.Empty<string>();
    }

    private async Task<IFrame> FindReportFrameAsync(IPage page, CancellationToken cancellationToken)
    {
        foreach (var frame in page.Frames)
        {
            if (frame.Url.Contains("007_001", StringComparison.OrdinalIgnoreCase))
            {
                return frame;
            }

            var inputs = await frame.QuerySelectorAllAsync("input");
            foreach (var input in inputs)
            {
                var label = string.Join(" ", new[]
                {
                    await input.GetAttributeAsync("name"),
                    await input.GetAttributeAsync("id"),
                    await input.GetAttributeAsync("placeholder"),
                    await input.GetAttributeAsync("aria-label")
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

                if (label.Contains("商品", StringComparison.OrdinalIgnoreCase) || label.Contains("商品ID", StringComparison.OrdinalIgnoreCase) || label.Contains("產品", StringComparison.OrdinalIgnoreCase))
                {
                    return frame;
                }
            }
        }

        return page.MainFrame;
    }

    private async Task LogInputDiagnosticsAsync(IFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            var inputs = await frame.QuerySelectorAllAsync("input");
            var index = 0;
            foreach (var input in inputs)
            {
                index++;
                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
                var aria = await input.GetAttributeAsync("aria-label") ?? string.Empty;
                var type = await input.GetAttributeAsync("type") ?? string.Empty;
                _logger.LogWarning("輸入框偵測[{Index}] type={Type} name={Name} id={Id} placeholder={Placeholder} aria={Aria}", index, type, name, id, placeholder, aria);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "輸入框診斷失敗");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (ShouldKeepBrowserOpen())
        {
            return;
        }

        if (_context != null)
        {
            await _context.CloseAsync();
        }

        _playwright?.Dispose();
        _lock.Dispose();
    }

    private static bool ShouldKeepBrowserOpen()
        => string.Equals(Environment.GetEnvironmentVariable("KEEP_BROWSER_OPEN"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("KEEP_BROWSER_OPEN"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsAutoLoginEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("AUTO_LOGIN");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldBringBrowserFront()
        => string.Equals(Environment.GetEnvironmentVariable("BRING_BROWSER_FRONT"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("BRING_BROWSER_FRONT"), "true", StringComparison.OrdinalIgnoreCase);
}
