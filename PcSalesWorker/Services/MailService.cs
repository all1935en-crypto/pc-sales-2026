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
