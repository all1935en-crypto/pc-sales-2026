using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace PcSalesWorker.Services;

public static class ExceptionHelper
{
    public static void Report(ILogger logger, string message, Exception? ex = null)
    {
        if (ex == null)
        {
            logger.LogError("{Message}", message);
        }
        else
        {
            logger.LogError(ex, "{Message}", message);
        }

        MessageBox.Show(message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
