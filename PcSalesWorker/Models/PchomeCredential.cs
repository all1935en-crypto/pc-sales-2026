namespace PcSalesWorker.Models;

public sealed class PchomeCredential
{
    public string VendorId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    public static PchomeCredential LoadFromEnvFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到 .env 檔案", path);
        }

        var lines = File.ReadAllLines(path);
        var vendor = string.Empty;
        var user = string.Empty;
        var pass = string.Empty;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("廠商帳號", StringComparison.OrdinalIgnoreCase))
            {
                vendor = line["廠商帳號".Length..].Trim();
                continue;
            }

            if (line.StartsWith("使用者帳號", StringComparison.OrdinalIgnoreCase))
            {
                user = line["使用者帳號".Length..].Trim();
                continue;
            }

            if (line.StartsWith("使用者密碼", StringComparison.OrdinalIgnoreCase))
            {
                pass = line["使用者密碼".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(vendor) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            throw new InvalidOperationException(".env 缺少廠商帳號、使用者帳號或使用者密碼。");
        }

        return new PchomeCredential
        {
            VendorId = vendor,
            UserId = user,
            Password = pass
        };
    }
}
