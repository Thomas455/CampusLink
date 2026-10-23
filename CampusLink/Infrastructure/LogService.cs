using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CampusLink.Infrastructure
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Tag { get; set; }
        public string Message { get; set; }

        public string TimeText => Time.ToString("HH:mm:ss");
        public string LevelText
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Debug: return "DBG";
                    case LogLevel.Info: return "INF";
                    case LogLevel.Warn: return "WRN";
                    default: return "ERR";
                }
            }
        }

        public override string ToString()
        {
            return Time.ToString("HH:mm:ss") + " [" + LevelText + "]" + (string.IsNullOrEmpty(Tag) ? "" : " {" + Tag + "}") + " " + Message;
        }
    }

    /// <summary>
    /// 把 WPF 绑定错误之类的调试输出接进日志（仅 DEBUG 构建挂载）。
    /// </summary>
    public class LogTraceListener : System.Diagnostics.TraceListener
    {
        private readonly LogService _log;

        public LogTraceListener(LogService log)
        {
            _log = log;
        }

        public override void Write(string message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _log.Warn("TRACE " + message.Trim());
        }

        public override void WriteLine(string message)
        {
            Write(message);
        }
    }

    /// <summary>
    /// 内存日志（界面）+ 按天滚动文件日志。写入前会脱敏 password/pass/pwd 等字段。
    /// </summary>
    public class LogService
    {
        private const int MaxEntries = 2000;
        private static readonly Regex SecretPattern = new Regex(
            "(password|passwd|pass|pwd|verifycode)(\\s*[=:]\\s*)([^&\\s\"',}]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly object _fileLock = new object();
        private readonly string _logDirectory;

        public LogService()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CampusLink", "logs");
            try { Directory.CreateDirectory(_logDirectory); } catch { }
        }

        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        public string LogDirectory => _logDirectory;

        public void Debug(string message, string tag = null) => Add(LogLevel.Debug, message, tag);
        public void Info(string message, string tag = null) => Add(LogLevel.Info, message, tag);
        public void Warn(string message, string tag = null) => Add(LogLevel.Warn, message, tag);
        public void Error(string message, string tag = null) => Add(LogLevel.Error, message, tag);

        public void Error(string message, Exception ex, string tag = null)
        {
            Add(LogLevel.Error, message + " :: " + (ex == null ? "" : ex.GetType().Name + ": " + ex.Message), tag);
        }

        public void Clear()
        {
            UiDispatcher.Post(() => Entries.Clear());
        }

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return SecretPattern.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "***");
        }

        private void Add(LogLevel level, string message, string tag)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Tag = tag,
                Message = Redact(message)
            };

            UiDispatcher.Post(() =>
            {
                Entries.Add(entry);
                while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
            });

            WriteFile(entry);
        }

        private void WriteFile(LogEntry entry)
        {
            try
            {
                lock (_fileLock)
                {
                    var path = Path.Combine(_logDirectory, DateTime.Now.ToString("yyyyMMdd") + ".log");
                    var line = entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + entry.LevelText + "]"
                               + (string.IsNullOrEmpty(entry.Tag) ? "" : " {" + entry.Tag + "}") + " " + entry.Message
                               + Environment.NewLine;
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志文件写不进去不影响主流程
            }
        }
    }
}
