using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PcSalesWorker.Services;

public sealed class MailService
{
    private readonly ILogger<MailService> _logger;
    private readonly string _mailPath;
    private MailSettings? _settings;

    public MailService(ILogger<MailService> logger)
    {
        _logger = logger;
        _mailPath = Path.Combine(AppContext.BaseDirectory, "mail.json");
    }

    public async Task SendErrorAsync(string subject, string body, CancellationToken cancellationToken)
    {
        await EnsureSettingsAsync(cancellationToken);
        if (_settings == null)
        {
            return;
        }

        if (!TryValidateSettings(_settings, out var reason))
        {
            _logger.LogWarning("mail.json 設定不完整，略過寄送錯誤通知：{Reason}", reason);
            return;
        }

        try
        {
            using var client = new SmtpClient(_settings.SmtpServer, _settings.SmtpPort)
            {
                EnableSsl = _settings.UseTls,
                Credentials = new NetworkCredential(_settings.Email, _settings.AppPassword)
            };

            using var message = new MailMessage
            {
                From = new MailAddress(_settings.Email, _settings.FromName),
                Subject = subject,
                Body = body
            };
            message.To.Add(_settings.ToEmail);

            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "寄送錯誤通知失敗，已略過。");
        }
    }

    private async Task EnsureSettingsAsync(CancellationToken cancellationToken)
    {
        if (_settings != null)
        {
            return;
        }

        if (!File.Exists(_mailPath))
        {
            _logger.LogWarning("找不到 mail.json，略過寄信。");
            return;
        }

        var json = await File.ReadAllTextAsync(_mailPath, cancellationToken);
        _settings = JsonSerializer.Deserialize<MailSettings>(json);
    }

    private static bool TryValidateSettings(MailSettings settings, out string reason)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpServer))
        {
            reason = "SmtpServer 空白";
            return false;
        }

        if (settings.SmtpPort <= 0)
        {
            reason = "SmtpPort 無效";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.Email))
        {
            reason = "Email 空白";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.ToEmail))
        {
            reason = "ToEmail 空白";
            return false;
        }

        if (!IsValidEmail(settings.Email))
        {
            reason = "Email 格式錯誤";
            return false;
        }

        if (!IsValidEmail(settings.ToEmail))
        {
            reason = "ToEmail 格式錯誤";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsValidEmail(string address)
    {
        try
        {
            _ = new MailAddress(address);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class MailSettings
{
    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public bool UseTls { get; set; }
    public string Email { get; set; } = string.Empty;
    public string AppPassword { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string ToEmail { get; set; } = string.Empty;
}
