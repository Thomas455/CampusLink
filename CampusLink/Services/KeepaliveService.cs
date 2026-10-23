using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampusLink.Models;

namespace CampusLink.Services
{
    public class KeepaliveTickEventArgs : EventArgs
    {
        public string AccountId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public int Interval { get; set; }
    }

    /// <summary>
    /// 每个已登录会话一个定时器，按 login 返回的 keepaliveInterval 周期保活。
    /// </summary>
    public class KeepaliveService : IDisposable
    {
        private class Entry
        {
            public Timer Timer;
            public Func<Task<ApiResult>> Action;
            public int Interval;
            public int Running;
            public string AccountId;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly object _gate = new object();
        private bool _disposed;

        public event EventHandler<KeepaliveTickEventArgs> Tick;

        public void Start(string accountId, Func<Task<ApiResult>> keepaliveAction, int intervalSeconds)
        {
            if (string.IsNullOrEmpty(accountId) || keepaliveAction == null) return;

            var interval = Math.Max(30, Math.Min(3600, intervalSeconds));

            lock (_gate)
            {
                StopLocked(accountId);
                var entry = new Entry
                {
                    AccountId = accountId,
                    Action = keepaliveAction,
                    Interval = interval
                };
                // 立即触发一次，之后按周期
                entry.Timer = new Timer(OnTimer, entry, TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
                _entries[accountId] = entry;
            }
        }

        public void Stop(string accountId)
        {
            lock (_gate)
            {
                StopLocked(accountId);
            }
        }

        public void StopAll()
        {
            lock (_gate)
            {
                foreach (var key in new List<string>(_entries.Keys)) StopLocked(key);
            }
        }

        public bool IsRunning(string accountId)
        {
            lock (_gate) return !string.IsNullOrEmpty(accountId) && _entries.ContainsKey(accountId);
        }

        private void StopLocked(string accountId)
        {
            if (!_entries.TryGetValue(accountId, out var existing)) return;
            try { existing.Timer?.Dispose(); } catch { }
            _entries.Remove(accountId);
        }

        private async void OnTimer(object state)
        {
            var entry = state as Entry;
            if (entry == null || _disposed) return;
            if (Interlocked.Exchange(ref entry.Running, 1) == 1) return;

            try
            {
                var result = await entry.Action().ConfigureAwait(false);
                Tick?.Invoke(this, new KeepaliveTickEventArgs
                {
                    AccountId = entry.AccountId,
                    Success = result != null && result.Success,
                    Message = result?.Message ?? "",
                    Interval = entry.Interval
                });
            }
            catch (Exception ex)
            {
                Tick?.Invoke(this, new KeepaliveTickEventArgs
                {
                    AccountId = entry.AccountId,
                    Success = false,
                    Message = ex.Message,
                    Interval = entry.Interval
                });
            }
            finally
            {
                Interlocked.Exchange(ref entry.Running, 0);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            StopAll();
        }
    }
}
