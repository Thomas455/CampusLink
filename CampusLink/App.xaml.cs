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

            Tray = new TrayService();
            Tray.Initialize();

            var window = new MainWindow(ViewModel, Tray);
            MainWindow = window;
            window.Show();

            ViewModel.StartBackgroundServices();

            if (e.Args != null && e.Args.Length > 0 && e.Args[0].IndexOf("silent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                window.Hide();
            }

            window.Loaded += (sender, args) => ViewModel.RunStartupAutoLogin();
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
