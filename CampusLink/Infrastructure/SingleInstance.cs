using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace CampusLink.Infrastructure
{
    /// <summary>
    /// 单实例：第二次启动时把已经运行的窗口唤到前台，然后自己退出，
    /// 避免出现多个托盘图标 / 多个隐藏实例互相覆盖配置。
    /// </summary>
    public static class SingleInstance
    {
        private const string MutexName = "CampusLink.SingleInstance";
        private const string ShowMessage = "CampusLink.ShowFirstInstance";

        private static Mutex _mutex;
        private static int _showMessageId;
        private static HwndSource _source;

        /// <summary>返回 false 表示已有实例在运行（本进程应退出）。</summary>
        public static bool TryAcquire()
        {
            bool created;
            _mutex = new Mutex(true, MutexName, out created);
            return created;
        }

        /// <summary>在主窗口句柄创建后调用，注册"请显示自己"的广播消息。</summary>
        public static void Attach(Window window, Action onShow)
        {
            if (window == null) return;
            _showMessageId = HwndSourceInterop.RegisterWindowMessage(ShowMessage);
            var helper = new WindowInteropHelper(window);
            _source = HwndSource.FromHwnd(helper.Handle);
            _source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (_showMessageId != 0 && msg == _showMessageId)
                {
                    handled = true;
                    onShow?.Invoke();
                }
                return IntPtr.Zero;
            });
        }

        /// <summary>通知已运行的实例显示窗口。</summary>
        public static void NotifyExisting()
        {
            var id = HwndSourceInterop.RegisterWindowMessage(ShowMessage);
            if (id != 0) HwndSourceInterop.PostMessage(HwndSourceInterop.HwndBroadcast, id, IntPtr.Zero, IntPtr.Zero);
        }

        private static class HwndSourceInterop
        {
            public static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            public static extern int RegisterWindowMessage(string lpString);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        }
    }
}
