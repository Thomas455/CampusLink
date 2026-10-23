using System;
using System.Collections.Generic;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    public class OptionItem
    {
        public string Key { get; set; }
        public string Label { get; set; }

        public override string ToString() => Label;
    }

    /// <summary>设置页：直接编辑 AppSettings 的副本，点"保存"后统一应用。</summary>
    public class SettingsViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly LogService _log;
        private readonly Action<AppSettings> _onSaved;
        private bool _startWithWindows;
        private bool _savedOnce;

        public SettingsViewModel(AppSettings settings, LogService log, Action<AppSettings> onSaved)
        {
            _settings = settings.Clone();
            _log = log;
            _onSaved = onSaved;
            _startWithWindows = StartupService.IsEnabled();
            SaveCommand = new RelayCommand(Save);
            PasswordEncryptModes = new List<OptionItem>
            {
                new OptionItem { Key = "auto", Label = "自动（按登录页隐藏字段判断）" },
                new OptionItem { Key = "plain", Label = "强制明文" },
                new OptionItem { Key = "rsa", Label = "强制 RSA 加密" }
            };
        }

        public ICommand SaveCommand { get; }

        public List<OptionItem> PasswordEncryptModes { get; }

        public string DefaultPortalHost
        {
            get => _settings.DefaultPortalHost;
            set { _settings.DefaultPortalHost = value; OnPropertyChanged(); }
        }

        public string ProbeUrls
        {
            get => _settings.ProbeUrls;
            set { _settings.ProbeUrls = value; OnPropertyChanged(); }
        }

        public string SelectedPasswordEncryptMode
        {
            get => _settings.PasswordEncryptMode;
            set { _settings.PasswordEncryptMode = value; OnPropertyChanged(); }
        }

        public string RsaSeparator
        {
            get => _settings.RsaSeparator;
            set { _settings.RsaSeparator = value; OnPropertyChanged(); }
        }

        public bool AutoReconnectEnabled
        {
            get => _settings.AutoReconnectEnabled;
            set { _settings.AutoReconnectEnabled = value; OnPropertyChanged(); }
        }

        public int ReconnectCheckSeconds
        {
            get => _settings.ReconnectCheckSeconds;
            set
            {
                _settings.ReconnectCheckSeconds = Math.Max(10, Math.Min(3600, value));
                OnPropertyChanged();
            }
        }

        public bool KeepaliveEnabled
        {
            get => _settings.KeepaliveEnabled;
            set { _settings.KeepaliveEnabled = value; OnPropertyChanged(); }
        }

        public bool AutoLoginOnStartup
        {
            get => _settings.AutoLoginOnStartup;
            set { _settings.AutoLoginOnStartup = value; OnPropertyChanged(); }
        }

        public bool StartWithWindows
        {
            get => _startWithWindows;
            set { _startWithWindows = value; OnPropertyChanged(); }
        }

        public bool CloseToTray
        {
            get => _settings.CloseToTray;
            set { _settings.CloseToTray = value; OnPropertyChanged(); }
        }

        public bool MinimizeToTray
        {
            get => _settings.MinimizeToTray;
            set { _settings.MinimizeToTray = value; OnPropertyChanged(); }
        }

        public bool ShowBalloonNotifications
        {
            get => _settings.ShowBalloonNotifications;
            set { _settings.ShowBalloonNotifications = value; OnPropertyChanged(); }
        }

        public string SelfServiceBaseUrl
        {
            get => _settings.SelfServiceBaseUrl;
            set { _settings.SelfServiceBaseUrl = value; OnPropertyChanged(); }
        }

        public string DefaultPasswordTemplate
        {
            get => _settings.DefaultPasswordTemplate;
            set { _settings.DefaultPasswordTemplate = value; OnPropertyChanged(); }
        }

        public string DataDirectory => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusLink");

        public void Save()
        {
            _settings.StartWithWindows = _startWithWindows;

            if (!StartupService.Apply(_startWithWindows, out var error))
            {
                _log.Warn("设置开机自启失败：" + error);
            }

            _onSaved?.Invoke(_settings.Clone());
            _savedOnce = true;
            _log.Info("设置已保存");
            OnPropertyChanged(nameof(SaveHint));
        }

        public string SaveHint => _savedOnce ? "已保存，部分设置立即生效" : "修改后点保存生效";
    }
}
