using System;
using System.IO;
using System.Text;
using PowerDesk.Core.Services;

namespace PowerDesk.Core.Logging;

public sealed class FileLogger : ILogger
{
    private static readonly object Sync = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB rolling

    public string LogFilePath { get; } = PathService.LogFile;

    public void Info(string message)  => Write("INFO ", message, null);
    public void Warn(string message)  => Write("WARN ", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Sync)
            {
                RollIfNeeded();
                var sb = new StringBuilder(256);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(' ').Append(level).Append(' ').Append(message);
                if (ex is not null)
                {
                    sb.AppendLine();
                    sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        sb.AppendLine();
                        sb.Append(ex.StackTrace);
                    }
                }
                sb.AppendLine();
                File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* swallow: logging must never crash the app */ }
    }

    private void RollIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogFilePath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var rolled = LogFilePath + ".1";
            try { if (File.Exists(rolled)) File.Delete(rolled); } catch { }
            try { File.Move(LogFilePath, rolled); } catch { }
        }
        catch { }
    }
}
