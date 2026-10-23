using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CampusLink.Services;
using CampusLink.ViewModels;
using CampusLink.Views;
using Wpf.Ui.Controls;

namespace CampusLink
{
    public partial class MainWindow : FluentWindow
    {
        private readonly MainViewModel _viewModel;
        private readonly TrayService _tray;
        private readonly AccountsPage _accountsPage;
        private readonly InfoPage _infoPage;
        private readonly DiagnosticsPage _diagnosticsPage;
        private readonly LogsPage _logsPage;
        private readonly SettingsPage _settingsPage;
        private readonly AboutPage _aboutPage;
        private bool _explicitExit;
        private bool _balloonShown;

        public MainWindow(MainViewModel viewModel, TrayService tray)
        {
            InitializeComponent();

            _viewModel = viewModel;
            _tray = tray;
            DataContext = viewModel;

            _accountsPage = new AccountsPage { DataContext = viewModel };
            _infoPage = new InfoPage { DataContext = viewModel.Info };
            _diagnosticsPage = new DiagnosticsPage { DataContext = viewModel.Diagnostics };
            _logsPage = new LogsPage(viewModel.Log);
            _settingsPage = new SettingsPage { DataContext = viewModel.Settings };
            _aboutPage = new AboutPage { DataContext = viewModel.About };

            _viewModel.CaptchaRequested += ViewModel_OnCaptchaRequested;
            _viewModel.TrayTextChanged += OnTrayTextChanged;
            _viewModel.Update.CheckFinished += (sender, manual) =>
            {
                if (manual) ShowUpdateDialog();
            };

            _tray.ShowRequested += (s, e) => RestoreWindow();
            _tray.LoginRequested += (s, e) =>
            {
                var target = _viewModel.SelectedAccount;
                if (target != null && target.LoginCommand.CanExecute(null)) target.LoginCommand.Execute(null);
            };
            _tray.LogoutRequested += (s, e) =>
            {
                var target = _viewModel.SelectedAccount;
                if (target != null && target.LogoutCommand.CanExecute(null)) target.LogoutCommand.Execute(null);
            };
            _tray.ExitRequested += (s, e) =>
            {
                _explicitExit = true;
                Application.Current.Shutdown();
            };

            Navigate("accounts");
        }

        private void ViewModel_OnCaptchaRequested(object sender, CaptchaEventArgs e)
        {
            var dialog = new CaptchaDialog(e.Image, e.Title, e.Message)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var ok = dialog.ShowDialog();
            e.Code = ok == true ? dialog.Code : "";
        }

        private void UpdateBanner_OnClick(object sender, RoutedEventArgs e)
        {
            ShowUpdateDialog();
        }

        private void UpdateDismiss_OnClick(object sender, RoutedEventArgs e)
        {
            _viewModel.Update.Dismiss();
        }

        private void ShowUpdateDialog()
        {
            var dialog = new UpdateDialog
            {
                DataContext = _viewModel.Update,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.ShowDialog();
        }

        private void OnTrayTextChanged(string text)
        {
            _tray.SetStatus(text);
        }

        private void Nav_OnClick(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            if (button?.Tag == null) return;
            Navigate(button.Tag.ToString());
        }

        private void Navigate(string key)
        {
            _viewModel.Log.Debug("切换页面：" + key);
            switch (key)
            {
                case "info":
                    PageHost.Content = _infoPage;
                    break;
                case "diagnostics":
                    PageHost.Content = _diagnosticsPage;
                    break;
                case "logs":
                    PageHost.Content = _logsPage;
                    break;
                case "settings":
                    PageHost.Content = _settingsPage;
                    break;
                case "about":
                    PageHost.Content = _aboutPage;
                    break;
                default:
                    PageHost.Content = _accountsPage;
                    key = "accounts";
                    break;
            }

            Highlight(key);
        }

        private void Highlight(string key)
        {
            NavAccounts.Appearance = key == "accounts" ? ControlAppearance.Primary : ControlAppearance.Secondary;
            NavInfo.Appearance = key == "info" ? ControlAppearance.Primary : ControlAppearance.Secondary;
            NavDiagnostics.Appearance = key == "diagnostics" ? ControlAppearance.Primary : ControlAppearance.Secondary;
            NavLogs.Appearance = key == "logs" ? ControlAppearance.Primary : ControlAppearance.Secondary;
            NavSettings.Appearance = key == "settings" ? ControlAppearance.Primary : ControlAppearance.Secondary;
            NavAbout.Appearance = key == "about" ? ControlAppearance.Primary : ControlAppearance.Secondary;
        }

        private void OpenDataFolder_OnClick(object sender, RoutedEventArgs e)
        {
            Services.ShellHelper.OpenFolder(_viewModel.Settings.DataDirectory, _viewModel.Log);
        }

        private void OpenLogFolder_OnClick(object sender, RoutedEventArgs e)
        {
            Services.ShellHelper.OpenFolder(_viewModel.Log.LogDirectory, _viewModel.Log);
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        /// <summary>第二个实例启动时通过广播消息调用，把已有窗口唤到前台。</summary>
        public void RestoreFromExternal()
        {
            RestoreWindow();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_explicitExit && _viewModel.Settings.CloseToTray)
            {
                e.Cancel = true;
                // 关闭前也落一次盘，避免配置丢失
                _viewModel.PersistAccounts();
                Hide();
                if (!_balloonShown && _viewModel.Settings.ShowBalloonNotifications)
                {
                    _balloonShown = true;
                    _tray.ShowBalloon("CampusLink 仍在运行", "已最小化到托盘，双击图标可恢复窗口。");
                }
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
            {
                Hide();
            }
        }
    }
}
