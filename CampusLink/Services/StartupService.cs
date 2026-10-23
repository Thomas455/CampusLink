using System;
using Microsoft.Win32;

namespace CampusLink.Services
{
    /// <summary>
    /// 开机自启（HKCU\...\Run），不需要管理员权限。
    /// </summary>
    public static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CampusLink";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var value = key?.GetValue(ValueName) as string;
                    return !string.IsNullOrWhiteSpace(value);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Apply(bool enabled, out string error)
        {
            error = null;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        error = "无法打开注册表启动项";
                        return false;
                    }

                    if (enabled)
                    {
                        var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                        if (string.IsNullOrEmpty(exe))
                        {
                            error = "无法获取程序路径";
                            return false;
                        }
                        key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
