using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CampusLink.Infrastructure
{
    public enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>窗口右下角弹出的一条提示。</summary>
    public class ToastItem : ObservableObject
    {
        public ToastItem(string message, ToastKind kind, string title, int seconds)
        {
            Message = message ?? "";
            Kind = kind;
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(kind) : title;
            Seconds = seconds;
            CreatedAt = DateTime.Now;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string Message { get; }

        public string Title { get; }

        public ToastKind Kind { get; }

        public int Seconds { get; }

        public DateTime CreatedAt { get; }

        public string Accent => Kind == ToastKind.Success ? "#22C55E"
            : Kind == ToastKind.Warning ? "#F59E0B"
            : Kind == ToastKind.Error ? "#EF4444"
            : "#38BDF8";

        public string Background => Kind == ToastKind.Success ? "#12281C"
            : Kind == ToastKind.Warning ? "#2A2112"
            : Kind == ToastKind.Error ? "#2A1416"
            : "#132132";

        public string Icon => Kind == ToastKind.Success ? "\uE73E"
            : Kind == ToastKind.Warning ? "\uE7BA"
            : Kind == ToastKind.Error ? "\uE711"
            : "\uE946";

        private static string DefaultTitle(ToastKind kind)
        {
            switch (kind)
            {
                case ToastKind.Success: return "成功";
                case ToastKind.Warning: return "注意";
                case ToastKind.Error: return "失败";
                default: return "提示";
            }
        }
    }

    /// <summary>
    /// 应用内通知：右下角堆叠弹出，按绿/黄/红/蓝分级，自动消失。
    /// 所有界面反馈统一走这里，不再靠替换卡片文字。
    /// </summary>
    public class ToastService
    {
        private const int MaxItems = 4;

        public ToastService()
        {
            DismissCommand = new RelayCommand<ToastItem>(Dismiss);
        }

        public ObservableCollection<ToastItem> Items { get; } = new ObservableCollection<ToastItem>();

        public ICommand DismissCommand { get; }

        public void Success(string message, string title = null, int seconds = 4) => Show(message, ToastKind.Success, title, seconds);
        public void Warning(string message, string title = null, int seconds = 5) => Show(message, ToastKind.Warning, title, seconds);
        public void Error(string message, string title = null, int seconds = 6) => Show(message, ToastKind.Error, title, seconds);
        public void Info(string message, string title = null, int seconds = 4) => Show(message, ToastKind.Info, title, seconds);

        public void Show(string message, ToastKind kind = ToastKind.Info, string title = null, int seconds = 4)
        {
            if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(title)) return;
            if (seconds <= 0) seconds = 4;

            var item = new ToastItem(message, kind, title, seconds);
            UiDispatcher.Post(() =>
            {
                Items.Add(item);
                while (Items.Count > MaxItems) Items.RemoveAt(0);
            });

            // 到点自动移除（不依赖 DispatcherTimer，避免泄漏）
            Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
                UiDispatcher.Post(() => Items.Remove(item)), TaskScheduler.Default);
        }

        public void Dismiss(ToastItem item)
        {
            if (item == null) return;
            UiDispatcher.Post(() => Items.Remove(item));
        }
    }
}
