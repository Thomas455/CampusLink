using System;
using System.Diagnostics;
using System.IO;
using CampusLink.Infrastructure;

namespace CampusLink.Services
{
    /// <summary>调用系统外壳打开目录 / 链接，失败写日志而不是静默吞掉。</summary>
    public static class ShellHelper
    {
        public static void OpenFolder(string path, LogService log = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                log?.Info("已打开目录：" + path);
            }
            catch (Exception ex)
            {
                log?.Error("打开目录失败：" + path, ex);
            }
        }

        public static void OpenUrl(string url, LogService log = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                log?.Info("已在浏览器打开：" + url);
            }
            catch (Exception ex)
            {
                log?.Error("打开链接失败：" + url, ex);
            }
        }
    }
}
