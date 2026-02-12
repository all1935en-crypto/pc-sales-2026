namespace PcSalesWorker.Services;

public static class TimeZoneHelper
{
    public static TimeZoneInfo Resolve(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (string.Equals(timeZoneId, "Asia/Taipei", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time");
                }
                catch (TimeZoneNotFoundException)
                {
                    return TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
                }
            }

            return TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        }
    }
}
