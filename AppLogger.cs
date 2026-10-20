using System.Text;

namespace ZumbidoJao;

internal static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZumbidoJao",
        "Logs");

    private const int RetentionDays = 30;

    public static void Error(string category, string operation, Exception exception)
    {
        Write("ERROR", category, operation, exception);
    }

    public static void Warning(string category, string operation, Exception? exception = null)
    {
        Write("WARN", category, operation, exception);
    }

    private static void Write(string level, string category, string operation, Exception? exception)
    {
        try
        {
            string timestamp = DateTimeOffset.Now.ToString("O");
            string details = exception is null
                ? string.Empty
                : $" | Exception={exception.GetType().FullName} | Message={exception.Message} | StackTrace={exception.StackTrace}";
            string line = $"{timestamp} | Level={level} | Category={category} | Operation={operation}{details}{Environment.NewLine}";
            string filePath = Path.Combine(LogDirectory, $"zumbidojao-{DateTime.Now:yyyy-MM-dd}.log");

            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(filePath, line, Encoding.UTF8);
                DeleteExpiredFiles();
            }
        }
        catch
        {
        }
    }

    private static void DeleteExpiredFiles()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (string file in Directory.EnumerateFiles(LogDirectory, "zumbidojao-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
        }
    }
}
