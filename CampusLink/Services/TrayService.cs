using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace CampusLink.Services
{
    /// <summary>
    /// 托盘图标与右键菜单。使用 WinForms NotifyIcon，稳定且没有额外依赖。
    /// 图标取自内嵌的 Resources/app.ico，避免依赖 exe 图标提取（提取失败会显示空白）。
    /// </summary>
    public class TrayService : IDisposable
    {
        private readonly Infrastructure.LogService _log;
        private NotifyIcon _icon;
        private Icon _appIcon;
        private bool _disposed;

        public TrayService(Infrastructure.LogService log)
        {
            _log = log;
        }

        public event EventHandler ShowRequested;
        public event EventHandler LoginRequested;
        public event EventHandler LogoutRequested;
        public event EventHandler ExitRequested;

        public bool IsVisible => _icon != null && _icon.Visible;

        public void Initialize()
        {
            if (_icon != null) return;

            _appIcon = LoadAppIcon();
            _icon = new NotifyIcon
            {
                Icon = _appIcon,
                Text = "CampusLink 校园网助手",
                Visible = true
            };
            _icon.DoubleClick += (s, e) => ShowRequested?.Invoke(this, EventArgs.Empty);

            var menu = new ContextMenuStrip();

            var showItem = new ToolStripMenuItem("显示主窗口");
            showItem.Click += (s, e) => ShowRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(showItem);

            menu.Items.Add(new ToolStripSeparator());

            var loginItem = new ToolStripMenuItem("登录当前账号");
            loginItem.Click += (s, e) => LoginRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(loginItem);

            var logoutItem = new ToolStripMenuItem("下线当前账号");
            logoutItem.Click += (s, e) => LogoutRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(logoutItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exitItem);

            _icon.ContextMenuStrip = menu;
            _log?.Info("托盘图标已创建（通知区域，可能被折叠到 ^ 里）");
        }

        private Icon LoadAppIcon()
        {
            try
            {
                var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
                if (resource?.Stream != null)
                {
                    using (var stream = resource.Stream)
                    {
                        return new Icon(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("加载内嵌托盘图标失败：" + ex.Message);
            }

            try
            {
                var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    var icon = Icon.ExtractAssociatedIcon(exe);
                    if (icon != null) return icon;
                }
            }
            catch
            {
            }

            return SystemIcons.Application;
        }

        public void SetStatus(string text)
        {
            if (_icon == null) return;
            try
            {
                _icon.Text = string.IsNullOrWhiteSpace(text) ? "CampusLink 校园网助手" : Truncate(text, 63);
            }
            catch
            {
                // NotifyIcon.Text 有长度限制，忽略
            }
        }

        public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            if (_icon == null) return;
            try
            {
                _icon.ShowBalloonTip(4000, title, message, icon);
            }
            catch
            {
            }
        }

        private static string Truncate(string text, int length)
        {
            return text.Length <= length ? text : text.Substring(0, length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
            if (_appIcon != null)
            {
                _appIcon.Dispose();
                _appIcon = null;
            }
        }
    }
}
