using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CampusLink.Infrastructure
{
    /// <summary>
    /// 后台线程统一回到 UI 线程的入口，避免每个调用点都写 Dispatcher.Invoke。
    /// </summary>
    public static class UiDispatcher
    {
        public static Dispatcher Dispatcher { get; private set; }

        public static void Initialize(Dispatcher dispatcher)
        {
            Dispatcher = dispatcher;
        }

        public static void Post(Action action)
        {
            if (action == null) return;
            var dispatcher = Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }

        public static Task InvokeAsync(Action action)
        {
            var dispatcher = Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
        }
    }
}
