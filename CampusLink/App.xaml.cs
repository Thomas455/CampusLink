using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CampusLink.Infrastructure;
using CampusLink.Services;
using CampusLink.ViewModels;

namespace CampusLink
{
    public partial class App : Application
    {
        public LogService Log { get; private set; }

        public AccountStore Store { get; private set; }

        public MainViewModel ViewModel { get; private set; }

        public TrayService Tray { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 已有实例在跑：把它的窗口唤出来，本进程直接退出，避免多个托盘图标/两份配置互相覆盖
            if (!SingleInstance.TryAcquire())
            {
                SingleInstance.NotifyExisting();
                Shutdown();
                return;
            }

            UiDispatcher.Initialize(Dispatcher);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            Log = new LogService();
            Store = new AccountStore(Log);
            ViewModel = new MainViewModel(Log, Store);

#if DEBUG
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new LogTraceListener(Log));
#endif

            Tray = new TrayService(Log);
            Tray.Initialize();

            var window = new MainWindow(ViewModel, Tray);
            MainWindow = window;
            window.SourceInitialized += (sender, args) => SingleInstance.Attach(window, window.RestoreFromExternal);

            // 注意：必须在 Show() 之前挂 Loaded，否则可能错过第一次 Loaded 事件
            window.Loaded += (sender, args) =>
            {
                Log.Info("窗口已加载，开始启动流程");
                ViewModel.RunStartupAutoLogin();
                ViewModel.StartupUpdateCheck();
                if (ViewModel.Settings.ShowBalloonNotifications)
                {
                    Tray.ShowBalloon("CampusLink 已启动",
                        "托盘图标在通知区域（Windows 11 可能折叠在 ^ 里）。关闭窗口默认退出，可在设置里改成最小化到托盘。");
                }
            };

            window.Show();

            ViewModel.StartBackgroundServices();

            if (e.Args != null && e.Args.Length > 0 && e.Args[0].IndexOf("silent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                window.Hide();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                ViewModel?.PersistSettings();
                ViewModel?.PersistAccounts();
                ViewModel?.Dispose();
                Tray?.Dispose();
            }
            catch
            {
            }
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log?.Error("未处理的界面异常", e.Exception);
            e.Handled = true;
            MessageBox.Show(
                "程序遇到异常：" + e.Exception.Message + "\n\n详细信息已写入日志。",
                "CampusLink", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log?.Error("未处理的线程异常", e.ExceptionObject as Exception);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Log?.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        }
    }
}
