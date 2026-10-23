using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;
using Microsoft.Win32;

namespace CampusLink.ViewModels
{
    public class OptionItem
    {
        public string Key { get; set; }
        public string Label { get; set; }

        public override string ToString() => Label;
    }

    /// <summary>
    /// 设置页：全局设置 + 学校预设管理。
    /// 学校名称/作者/门户地址/自助服务地址/默认密码模板都跟着预设走，一个预设一个 json 文件。
    /// </summary>
    public class SettingsViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly LogService _log;
        private readonly ToastService _toast;
        private readonly PresetService _presets;
        private readonly Action<AppSettings> _onSaved;
        private readonly Action<SchoolPreset> _onPresetActivated;

        private bool _startWithWindows;
        private SchoolPreset _selectedPreset;
        private bool _isNewPreset;
        private string _presetName = "";
        private string _presetAuthor = "";
        private string _presetPortalHost = "";
        private string _presetSelfServiceUrl = "";
        private string _presetPasswordTemplate = "";

        public SettingsViewModel(AppSettings settings, LogService log, ToastService toast, PresetService presets,
            Action<AppSettings> onSaved, Action<SchoolPreset> onPresetActivated)
        {
            _settings = settings.Clone();
            _log = log;
            _toast = toast;
            _presets = presets;
            _onSaved = onSaved;
            _onPresetActivated = onPresetActivated;
            _startWithWindows = StartupService.IsEnabled();

            Presets = new ObservableCollection<SchoolPreset>(_presets.LoadAll());
            _selectedPreset = Presets.FirstOrDefault(p => p.Id == _settings.ActivePresetId) ?? Presets.FirstOrDefault();
            LoadPresetFields(_selectedPreset);

            SaveCommand = new RelayCommand(Save);
            OpenTaskbarSettingsCommand = new RelayCommand(OpenTaskbarSettings);
            NewPresetCommand = new RelayCommand(NewPreset);
            SavePresetCommand = new RelayCommand(SavePreset);
            DeletePresetCommand = new RelayCommand(DeletePreset, () => SelectedPreset != null && !_isNewPreset);
            ActivatePresetCommand = new RelayCommand(ActivatePreset, () => SelectedPreset != null && !_isNewPreset);
            ImportPresetCommand = new RelayCommand(ImportPreset);
            ExportPresetCommand = new RelayCommand(ExportPreset, () => SelectedPreset != null && !_isNewPreset);
            OpenPresetFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_presets.Directory, _log));

            PasswordEncryptModes = new List<OptionItem>
            {
                new OptionItem { Key = "auto", Label = "自动（按登录页隐藏字段判断）" },
                new OptionItem { Key = "plain", Label = "强制明文" },
                new OptionItem { Key = "rsa", Label = "强制 RSA 加密" }
            };
        }

        public ICommand SaveCommand { get; }
        public ICommand OpenTaskbarSettingsCommand { get; }
        public ICommand NewPresetCommand { get; }
        public ICommand SavePresetCommand { get; }
        public ICommand DeletePresetCommand { get; }
        public ICommand ActivatePresetCommand { get; }
        public ICommand ImportPresetCommand { get; }
        public ICommand ExportPresetCommand { get; }
        public ICommand OpenPresetFolderCommand { get; }

        public List<OptionItem> PasswordEncryptModes { get; }

        public ObservableCollection<SchoolPreset> Presets { get; }

        public SchoolPreset SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (!SetProperty(ref _selectedPreset, value)) return;
                _isNewPreset = false;
                LoadPresetFields(value);
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsNewPreset
        {
            get => _isNewPreset;
            private set
            {
                if (!SetProperty(ref _isNewPreset, value)) return;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string PresetName
        {
            get => _presetName;
            set => SetProperty(ref _presetName, value);
        }

        public string PresetAuthor
        {
            get => _presetAuthor;
            set => SetProperty(ref _presetAuthor, value);
        }

        public string PresetPortalHost
        {
            get => _presetPortalHost;
            set => SetProperty(ref _presetPortalHost, value);
        }

        public string PresetSelfServiceUrl
        {
            get => _presetSelfServiceUrl;
            set => SetProperty(ref _presetSelfServiceUrl, value);
        }

        public string PresetPasswordTemplate
        {
            get => _presetPasswordTemplate;
            set => SetProperty(ref _presetPasswordTemplate, value);
        }

        public string ActivePresetHint
        {
            get
            {
                var active = _presets.Find(_settings.ActivePresetId);
                return active == null ? "当前没有启用预设" : "当前启用：" + active.Name;
            }
        }

        public string PresetsDirectory => _presets.Directory;

        // ---------------------------------------------------------------- 全局设置

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

        public bool CheckUpdateOnStartup
        {
            get => _settings.CheckUpdateOnStartup;
            set { _settings.CheckUpdateOnStartup = value; OnPropertyChanged(); }
        }

        public int UpdateCheckIntervalHours
        {
            get => _settings.UpdateCheckIntervalHours;
            set
            {
                _settings.UpdateCheckIntervalHours = Math.Max(1, Math.Min(168, value));
                OnPropertyChanged();
            }
        }

        public string DataDirectory => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusLink");

        public void Save()
        {
            _settings.StartWithWindows = _startWithWindows;
            _settings.ActivePresetId = SelectedPreset == null ? _settings.ActivePresetId : SelectedPreset.Id;

            if (!StartupService.Apply(_startWithWindows, out var error))
            {
                _log.Warn("设置开机自启失败：" + error);
                _toast.Warning("开机自启设置失败：" + error, "设置未完全生效");
            }

            _onSaved?.Invoke(_settings.Clone());
            _log.Info("设置已保存");
            _toast.Success("设置已保存", "设置");
        }

        // ---------------------------------------------------------------- 预设

        private void LoadPresetFields(SchoolPreset preset)
        {
            PresetName = preset?.Name ?? "";
            PresetAuthor = preset?.Author ?? "";
            PresetPortalHost = preset?.PortalHost ?? "";
            PresetSelfServiceUrl = preset?.SelfServiceUrl ?? "";
            PresetPasswordTemplate = preset?.DefaultPasswordTemplate ?? "{uid}@Gzhu";
            OnPropertyChanged(nameof(ActivePresetHint));
        }

        private void NewPreset()
        {
            _isNewPreset = true;
            SelectedPreset = null;
            PresetName = "";
            PresetAuthor = Environment.UserName;
            PresetPortalHost = "";
            PresetSelfServiceUrl = "";
            PresetPasswordTemplate = "{uid}@Gzhu";
            _toast.Info("填写学校信息后点「保存修改」即可创建预设", "新建预设");
        }

        private void SavePreset()
        {
            var name = (PresetName ?? "").Trim();
            if (name.Length == 0)
            {
                _toast.Error("学校名称不能为空", "无法保存预设");
                return;
            }

            var preset = _isNewPreset || SelectedPreset == null ? new SchoolPreset() : SelectedPreset.Clone();
            preset.Name = name;
            preset.Author = (PresetAuthor ?? "").Trim();
            preset.PortalHost = PortalClient.NormalizeHost(PresetPortalHost);
            preset.SelfServiceUrl = (PresetSelfServiceUrl ?? "").Trim();
            preset.DefaultPasswordTemplate = string.IsNullOrWhiteSpace(PresetPasswordTemplate)
                ? "{uid}"
                : PresetPasswordTemplate.Trim();

            if (string.IsNullOrWhiteSpace(preset.PortalHost))
            {
                _toast.Error("门户地址不能为空", "无法保存预设");
                return;
            }

            try
            {
                _presets.Save(preset);
                ReloadPresets(preset.Id);
                _isNewPreset = false;
                _toast.Success("预设已保存：" + preset.Name, "保存成功");
            }
            catch (Exception ex)
            {
                _log.Error("保存预设失败", ex);
                _toast.Error(ex.Message, "保存预设失败");
            }
        }

        private void DeletePreset()
        {
            var preset = SelectedPreset;
            if (preset == null) return;

            var answer = System.Windows.MessageBox.Show(
                "确定删除预设「" + preset.Name + "」吗？\n内置预设删除后会在下次启动时自动恢复。\n（只删除本机预设文件，不影响账号）",
                "删除预设", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.OK) return;

            try
            {
                _presets.Delete(preset);
                ReloadPresets(null);
                _toast.Success("预设已删除：" + preset.Name, "删除成功");
            }
            catch (Exception ex)
            {
                _toast.Error(ex.Message, "删除失败");
            }
        }

        private void ActivatePreset()
        {
            var preset = SelectedPreset;
            if (preset == null) return;

            _settings.ActivePresetId = preset.Id;
            _onSaved?.Invoke(_settings.Clone());
            _onPresetActivated?.Invoke(preset);
            OnPropertyChanged(nameof(ActivePresetHint));
            _toast.Success("已启用预设：" + preset.Name, "学校预设");
        }

        private void ImportPreset()
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入学校预设",
                Filter = "学校预设 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var preset = _presets.Import(dialog.FileName);
                ReloadPresets(preset.Id);
                _toast.Success("已导入预设：" + preset.Name, "导入成功");
            }
            catch (Exception ex)
            {
                _toast.Error(ex.Message, "导入失败");
            }
        }

        private void ExportPreset()
        {
            var preset = SelectedPreset;
            if (preset == null) return;

            var dialog = new SaveFileDialog
            {
                Title = "导出学校预设",
                Filter = "学校预设 (*.json)|*.json",
                FileName = PresetService.Slug(preset.Name) + ".json"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                _presets.Export(preset, dialog.FileName);
                _toast.Success("预设已导出：" + dialog.FileName, "导出成功");
            }
            catch (Exception ex)
            {
                _toast.Error(ex.Message, "导出失败");
            }
        }

        private void ReloadPresets(string selectId)
        {
            var list = _presets.LoadAll();
            Presets.Clear();
            foreach (var preset in list) Presets.Add(preset);

            SelectedPreset = string.IsNullOrWhiteSpace(selectId)
                ? Presets.FirstOrDefault()
                : Presets.FirstOrDefault(p => p.Id == selectId) ?? Presets.FirstOrDefault();
            LoadPresetFields(SelectedPreset);
            OnPropertyChanged(nameof(ActivePresetHint));
        }

        private void OpenTaskbarSettings()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _log.Warn("打开任务栏设置失败：" + ex.Message);
                _toast.Warning("打开任务栏设置失败：" + ex.Message);
            }
        }
    }
}
