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
    private const long MaxFileSizeBytes = 1024 * 1024;
    private const int MaxFilesPerDay = 3;

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
            byte[] lineBytes = EncodeBoundedLine(line);

            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(filePath) && new FileInfo(filePath).Length + lineBytes.LongLength > MaxFileSizeBytes)
                    RotateFiles(filePath);

                using (FileStream stream = new(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    stream.Write(lineBytes);

                DeleteExpiredFiles();
            }
        }
        catch
        {
        }
    }

    private static byte[] EncodeBoundedLine(string line)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(line);
        if (encoded.LongLength <= MaxFileSizeBytes)
            return encoded;

        const string truncationMarker = " [entrada truncada]";
        int suffixBytes = Encoding.UTF8.GetByteCount(truncationMarker + Environment.NewLine);
        int maxContentBytes = (int)MaxFileSizeBytes - suffixBytes;
        StringBuilder truncated = new();
        int contentBytes = 0;

        foreach (Rune rune in line.EnumerateRunes())
        {
            if (contentBytes + rune.Utf8SequenceLength > maxContentBytes)
                break;

            truncated.Append(rune);
            contentBytes += rune.Utf8SequenceLength;
        }

        truncated.Append(truncationMarker);
        truncated.Append(Environment.NewLine);
        return Encoding.UTF8.GetBytes(truncated.ToString());
    }

    private static void RotateFiles(string filePath)
    {
        for (int index = MaxFilesPerDay - 1; index >= 1; index--)
        {
            string sourcePath = index == 1 ? filePath : $"{filePath}.{index - 1}";
            string destinationPath = $"{filePath}.{index}";

            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            if (File.Exists(sourcePath))
                File.Move(sourcePath, destinationPath);
        }
    }

    private static void DeleteExpiredFiles()
    {
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (string file in Directory.EnumerateFiles(LogDirectory, "zumbidojao-*.log*"))
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
