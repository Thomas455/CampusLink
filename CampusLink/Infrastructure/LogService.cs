using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        // userIndex 是「会话_IP_学号」的 hex，属于个人信息，日志里只留前 8 位
        private static readonly Regex UserIndexPattern = new Regex(
            "(userIndex\\s*[=:]\\s*['\"]?)([0-9a-fA-F]{16,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 学号（userId）属于账号信息，日志里只保留后 3 位；operatorUserId 不处理
        private static readonly Regex UserIdPattern = new Regex(
            "(?<!operator)(userId\\s*[=:]\\s*['\"]?)([0-9A-Za-z_.@-]{2,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly object _fileLock = new object();
        private readonly string _logDirectory;
        private readonly bool _writeToFile;

        public LogService() : this(true)
        {
        }

        /// <summary>writeToFile=false 用于测试，避免污染用户目录下的日志。</summary>
        public LogService(bool writeToFile, string logDirectory = null)
        {
            _writeToFile = writeToFile;
            _logDirectory = string.IsNullOrWhiteSpace(logDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CampusLink", "logs")
                : logDirectory;
            if (writeToFile)
            {
                try { Directory.CreateDirectory(_logDirectory); } catch { }
            }
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

        /// <summary>
        /// 清洗历史日志：旧版本可能把完整学号写进 tag / userId= / userIndex=。
        /// 每次启动调用一次，只改写确实包含敏感内容的文件。
        /// </summary>
        public void SanitizeHistory(System.Collections.Generic.IEnumerable<string> accountIds)
        {
            if (!_writeToFile) return;
            try
            {
                var ids = (accountIds ?? new string[0])
                    .Where(id => !string.IsNullOrWhiteSpace(id) && id.Trim().Length > 3)
                    .Select(id => id.Trim())
                    .Distinct()
                    .OrderByDescending(id => id.Length)
                    .ToList();
                if (!Directory.Exists(_logDirectory)) return;

                lock (_fileLock)
                {
                    foreach (var file in Directory.GetFiles(_logDirectory, "*.log"))
                    {
                        string text;
                        try { text = File.ReadAllText(file); } catch { continue; }
                        if (string.IsNullOrEmpty(text)) continue;

                        var cleaned = text;
                        foreach (var id in ids)
                        {
                            cleaned = cleaned.Replace(id, "***" + id.Substring(id.Length - 3));
                        }
                        cleaned = UserIdPattern.Replace(cleaned, m =>
                        {
                            var value = m.Groups[2].Value;
                            return m.Groups[1].Value + (value.Length <= 3 ? "***" : "***" + value.Substring(value.Length - 3));
                        });
                        cleaned = UserIndexPattern.Replace(cleaned,
                            m => m.Groups[1].Value + m.Groups[2].Value.Substring(0, 8) + "…");

                        if (!string.Equals(cleaned, text, StringComparison.Ordinal))
                        {
                            try { File.WriteAllText(file, cleaned, new UTF8Encoding(false)); } catch { }
                        }
                    }
                }
            }
            catch
            {
                // 历史日志清洗失败不影响启动
            }
        }

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var masked = SecretPattern.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "***");
            masked = UserIdPattern.Replace(masked, m =>
            {
                var value = m.Groups[2].Value;
                return m.Groups[1].Value + (value.Length <= 3 ? "***" : "***" + value.Substring(value.Length - 3));
            });
            return UserIndexPattern.Replace(masked, m => m.Groups[1].Value + m.Groups[2].Value.Substring(0, 8) + "…");
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
            if (!_writeToFile) return;
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
