using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using CampusLink.Infrastructure;

namespace CampusLink.Services
{
    /// <summary>
    /// 断网/掉线自愈：定时回调 + 网络地址变化时立即回调。
    /// 具体"要不要重新登录"由上层判断，这里只负责节奏。
    /// </summary>
    public class AutoReconnectService : IDisposable
    {
        private readonly LogService _log;
        private readonly object _gate = new object();
        private Timer _timer;
        private int _intervalSeconds;
        private int _running;
        private bool _disposed;

        public AutoReconnectService(LogService log)
        {
            _log = log;
        }

        /// <summary>返回 true 表示本次检查已经处理完，无需再关心。</summary>
        public event Func<Task> CheckDue;

        public void Start(int intervalSeconds)
        {
            _intervalSeconds = Math.Max(10, Math.Min(3600, intervalSeconds));
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            StartTimer();
        }

        public void UpdateInterval(int intervalSeconds)
        {
            _intervalSeconds = Math.Max(10, Math.Min(3600, intervalSeconds));
            StartTimer();
        }

        private void StartTimer()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                if (_disposed) return;
                _timer = new Timer(OnTimer, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(_intervalSeconds));
            }
        }

        private void OnNetworkChanged(object sender, EventArgs e)
        {
            // 地址变化后等两秒再检查，避免网络还在协商
            Task.Delay(2000).ContinueWith(_ => Fire(), TaskScheduler.Default);
        }

        private void OnTimer(object state)
        {
            Fire().ConfigureAwait(false);
        }

        private async Task Fire()
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            try
            {
                var handler = CheckDue;
                if (handler != null) await handler().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("自动重连检查异常：" + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
