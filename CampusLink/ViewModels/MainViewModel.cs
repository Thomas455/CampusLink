using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    public class MainViewModel : ObservableObject, IDisposable
    {
        private readonly LogService _log;
        private readonly AccountStore _store;
        private readonly KeepaliveService _keepalive = new KeepaliveService();
        private readonly AutoReconnectService _autoReconnect;

        private AppSettings _settings;
        private AccountItemViewModel _selected;
        private string _editorPassword = "";
        private string _editorError = "";
        private string _editorHint = "";
        private bool _isNewAccount;
        private bool _disposed;

        public MainViewModel(LogService log, AccountStore store)
        {
            _log = log;
            _store = store;
            _settings = store.LoadSettings();

            _autoReconnect = new AutoReconnectService(log);
            _autoReconnect.CheckDue += OnReconnectTick;
            _keepalive.Tick += OnKeepaliveTick;

            Settings = new SettingsViewModel(_settings, log, OnSettingsSaved);
            SelfService = new SelfServiceViewModel(log, () => _settings);
            Diagnostics = new DiagnosticsViewModel(log, () => _settings);
            About = new AboutViewModel(log, _store.DataDirectory, log.LogDirectory);

            Accounts = new ObservableCollection<AccountItemViewModel>();

            NewAccountCommand = new RelayCommand(StartNewAccount);
            SaveAccountCommand = new RelayCommand(SaveAccount);
            CancelEditCommand = new RelayCommand(CancelEdit);
            DeleteAccountCommand = new RelayCommand(DeleteAccount, () => SelectedAccount != null);
            ForceLogoutCommand = new AsyncRelayCommand(ForceLogoutAsync, () => SelectedAccount != null, OnCommandError);
            LoginAllCommand = new AsyncRelayCommand(LoginAllAsync, () => !IsAnyBusy(), OnCommandError);
            ProbeAllCommand = new AsyncRelayCommand(ProbeAllAsync, () => !IsAnyBusy(), OnCommandError);
            OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_store.DataDirectory));
            OpenLogFolderCommand = new RelayCommand(() => OpenFolder(_log.LogDirectory));

            LoadAccounts();
            SelectFirstAccount();
        }

        public event EventHandler<CaptchaEventArgs> CaptchaRequested;
        public event Action<string> TrayTextChanged;

        public ObservableCollection<AccountItemViewModel> Accounts { get; }

        public LogService Log => _log;

        public SettingsViewModel Settings { get; }
        public SelfServiceViewModel SelfService { get; }
        public DiagnosticsViewModel Diagnostics { get; }
        public AboutViewModel About { get; }

        public ICommand NewAccountCommand { get; }
        public ICommand SaveAccountCommand { get; }
        public ICommand CancelEditCommand { get; }
        public ICommand DeleteAccountCommand { get; }
        public ICommand ForceLogoutCommand { get; }
        public ICommand LoginAllCommand { get; }
        public ICommand ProbeAllCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand OpenLogFolderCommand { get; }

        // ---------------------------------------------------------------- 选中账号 / 编辑器

        public AccountItemViewModel SelectedAccount
        {
            get => _selected;
            set
            {
                if (!SetProperty(ref _selected, value)) return;
                Diagnostics.Account = value;
                LoadEditor(value);
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionTitle));
                UpdateTrayText();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasSelection => _selected != null;

        public string SelectionTitle => _selected == null ? "未选择账号" : _selected.Title;

        public bool IsNewAccount
        {
            get => _isNewAccount;
            set
            {
                if (!SetProperty(ref _isNewAccount, value)) return;
                OnPropertyChanged(nameof(EditorTitle));
            }
        }

        public string EditorTitle => IsNewAccount ? "新建账号" : (_selected == null ? "账号详情" : "编辑：" + _selected.Title);

        public string EditorDisplayName { get; set; } = "";
        public string EditorUserId { get; set; } = "";
        public string EditorMac { get; set; } = "";
        public string EditorService { get; set; } = "";
        public string EditorPortalHost { get; set; } = "";
        public string EditorSourceIp { get; set; } = "";
        public string EditorRemark { get; set; } = "";
        public bool EditorAutoLogin { get; set; } = true;
        public bool EditorAutoReconnect { get; set; } = true;
        public bool EditorAutoRegisterMac { get; set; }

        public string EditorPasswordHint => IsNewAccount
            ? "新账号必填"
            : "留空表示不修改已有密码";

        public string EditorError
        {
            get => _editorError;
            set { _editorError = value; OnPropertyChanged(); }
        }

        public string EditorHint
        {
            get => _editorHint;
            set { _editorHint = value; OnPropertyChanged(); }
        }

        public void SetEditorPassword(string password)
        {
            _editorPassword = password ?? "";
        }

        private void LoadEditor(AccountItemViewModel item)
        {
            if (item == null)
            {
                EditorDisplayName = "";
                EditorUserId = "";
                EditorMac = "";
                EditorService = "";
                EditorPortalHost = _settings.DefaultPortalHost;
                EditorSourceIp = "";
                EditorRemark = "";
                EditorAutoLogin = true;
                EditorAutoReconnect = true;
                EditorAutoRegisterMac = false;
                _editorPassword = "";
            }
            else
            {
                var profile = item.Profile;
                EditorDisplayName = profile.DisplayName;
                EditorUserId = profile.UserId;
                EditorMac = profile.Mac;
                EditorService = profile.Service;
                EditorPortalHost = profile.PortalHost;
                EditorSourceIp = profile.SourceIp;
                EditorRemark = profile.Remark;
                EditorAutoLogin = profile.AutoLogin;
                EditorAutoReconnect = profile.AutoReconnect;
                EditorAutoRegisterMac = profile.AutoRegisterMac;
                _editorPassword = "";
            }

            EditorError = "";
            EditorHint = "";
            OnPropertyChanged(nameof(EditorDisplayName));
            OnPropertyChanged(nameof(EditorUserId));
            OnPropertyChanged(nameof(EditorMac));
            OnPropertyChanged(nameof(EditorService));
            OnPropertyChanged(nameof(EditorPortalHost));
            OnPropertyChanged(nameof(EditorSourceIp));
            OnPropertyChanged(nameof(EditorRemark));
            OnPropertyChanged(nameof(EditorAutoLogin));
            OnPropertyChanged(nameof(EditorAutoReconnect));
            OnPropertyChanged(nameof(EditorAutoRegisterMac));
            OnPropertyChanged(nameof(EditorPasswordHint));
            OnPropertyChanged(nameof(EditorTitle));
        }

        private void StartNewAccount()
        {
            IsNewAccount = true;
            SelectedAccount = null;
            LoadEditor(null);
            EditorPortalHost = _settings.DefaultPortalHost;
            OnPropertyChanged(nameof(EditorPortalHost));
            OnPropertyChanged(nameof(EditorTitle));
        }

        private void SaveAccount()
        {
            var userId = (EditorUserId ?? "").Trim();
            if (userId.Length == 0)
            {
                EditorError = "学号不能为空";
                return;
            }

            var portalHost = string.IsNullOrWhiteSpace(EditorPortalHost)
                ? _settings.DefaultPortalHost
                : EditorPortalHost.Trim();

            if (IsNewAccount)
            {
                if (string.IsNullOrWhiteSpace(_editorPassword))
                {
                    EditorError = "新账号必须填写密码";
                    return;
                }

                var profile = new AccountProfile
                {
                    DisplayName = (EditorDisplayName ?? "").Trim(),
                    UserId = userId,
                    Password = _editorPassword,
                    Mac = (EditorMac ?? "").Trim(),
                    Service = (EditorService ?? "").Trim(),
                    PortalHost = portalHost,
                    SourceIp = (EditorSourceIp ?? "").Trim(),
                    AutoLogin = EditorAutoLogin,
                    AutoReconnect = EditorAutoReconnect,
                    AutoRegisterMac = EditorAutoRegisterMac,
                    Remark = (EditorRemark ?? "").Trim()
                };

                var item = CreateItem(profile);
                Accounts.Add(item);
                IsNewAccount = false;
                SelectedAccount = item;
                PersistAccounts();
                EditorHint = "已创建账号：" + profile.Title;
                _log.Info("新增账号：" + profile.UserId);
                return;
            }

            if (SelectedAccount == null)
            {
                EditorError = "请先选择账号，或点「新建账号」";
                return;
            }

            var existing = SelectedAccount;
            var updated = existing.Profile.Clone();
            updated.DisplayName = (EditorDisplayName ?? "").Trim();
            updated.UserId = userId;
            updated.Mac = (EditorMac ?? "").Trim();
            updated.Service = (EditorService ?? "").Trim();
            updated.PortalHost = portalHost;
            updated.SourceIp = (EditorSourceIp ?? "").Trim();
            updated.AutoLogin = EditorAutoLogin;
            updated.AutoReconnect = EditorAutoReconnect;
            updated.AutoRegisterMac = EditorAutoRegisterMac;
            updated.Remark = (EditorRemark ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(_editorPassword)) updated.Password = _editorPassword;

            var endpointChanged = existing.Profile.PortalHost != updated.PortalHost
                                  || existing.Profile.SourceIp != updated.SourceIp;

            if (endpointChanged)
            {
                _keepalive.Stop(existing.Profile.Id);
                existing.MarkOffline("配置已修改，需要重新登录");
            }

            existing.ReplaceProfile(updated);
            _editorPassword = "";
            PersistAccounts();
            EditorHint = "已保存";
            _log.Info("更新账号：" + updated.UserId);
            OnPropertyChanged(nameof(SelectionTitle));
            UpdateTrayText();
        }

        private void CancelEdit()
        {
            _editorPassword = "";
            EditorError = "";
            EditorHint = "";
            if (IsNewAccount)
            {
                IsNewAccount = false;
                SelectFirstAccount();
            }
            else
            {
                LoadEditor(SelectedAccount);
            }
        }

        private void DeleteAccount()
        {
            var item = SelectedAccount;
            if (item == null) return;

            var answer = MessageBox.Show(
                "确定要删除账号「" + item.Title + "」吗？\n只会删除本机保存的配置，不会影响校园网服务器。",
                "删除账号", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            _keepalive.Stop(item.Profile.Id);
            Accounts.Remove(item);
            SelectedAccount = Accounts.FirstOrDefault();
            PersistAccounts();
            UpdateTrayText();
            _log.Info("删除账号：" + item.Profile.UserId);
        }

        // ---------------------------------------------------------------- 批量操作

        private async Task ForceLogoutAsync()
        {
            var item = SelectedAccount;
            if (item == null) return;

            if (string.IsNullOrWhiteSpace(item.Profile.Password))
            {
                EditorHint = "没有保存密码，无法按账号密码强制下线";
                return;
            }

            var answer = MessageBox.Show(
                "将用账号密码下线「" + item.Title + "」的所有在线会话（包括其他设备上的）。\n确定继续？",
                "强制下线", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            var result = await item.Client.LogoutAllAsync(item.Profile.UserId, item.Profile.Password, item.Tag)
                .ConfigureAwait(true);
            if (result.Success)
            {
                _keepalive.Stop(item.Profile.Id);
                item.MarkOffline("已强制下线该账号所有会话");
                EditorHint = "已强制下线：" + item.Title;
                UpdateTrayText();
            }
            else
            {
                EditorHint = "强制下线失败：" + result.Message;
            }
        }

        private async Task LoginAllAsync()        {
            var items = Accounts.ToList();
            if (items.Count == 0)
            {
                EditorHint = "没有可登录的账号";
                return;
            }

            var success = 0;
            foreach (var item in items)
            {
                if (await item.LoginAsync(true).ConfigureAwait(true)) success++;
            }

            EditorHint = "批量登录完成：" + success + "/" + items.Count + " 在线";
            _log.Info(EditorHint);
            UpdateTrayText();
        }

        private async Task ProbeAllAsync()
        {
            foreach (var item in Accounts.ToList())
            {
                await item.ProbeAsync().ConfigureAwait(true);
            }
            EditorHint = "全部账号状态已刷新";
            UpdateTrayText();
        }

        // ---------------------------------------------------------------- 后台服务

        public void StartBackgroundServices()
        {
            if (_settings.AutoReconnectEnabled)
            {
                _autoReconnect.Start(_settings.ReconnectCheckSeconds);
            }
        }

        public async void RunStartupAutoLogin()
        {
            if (!_settings.AutoLoginOnStartup) return;
            foreach (var item in Accounts.ToList())
            {
                if (!item.Profile.AutoLogin) continue;
                try
                {
                    await item.LoginAsync(true).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _log.Error("启动自动登录失败", ex, item.Tag);
                }
            }
            UpdateTrayText();
        }

        private async Task OnReconnectTick()
        {
            if (_disposed || !_settings.AutoReconnectEnabled) return;

            foreach (var item in Accounts.ToList())
            {
                if (_disposed) return;
                if (!item.Profile.AutoReconnect || item.IsBusy) continue;
                if (item.IsOnline) continue;

                try
                {
                    await item.ProbeAsync().ConfigureAwait(true);
                    if (item.Session.State == LinkState.NeedAuth)
                    {
                        _log.Info("检测到掉线，自动重连：" + item.Tag, item.Tag);
                        await item.LoginAsync(true).ConfigureAwait(true);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("自动重连异常：" + item.Tag + " -> " + ex.Message, item.Tag);
                }
            }
        }

        private void OnKeepaliveTick(object sender, KeepaliveTickEventArgs e)
        {
            var item = Accounts.FirstOrDefault(a => a.Profile.Id == e.AccountId);
            if (item == null) return;

            item.MarkKeepalive(e.Success, e.Message, e.Interval);
            if (e.Success) return;

            _keepalive.Stop(e.AccountId);
            if (_settings.AutoReconnectEnabled && item.Profile.AutoReconnect && !_disposed)
            {
                item.MarkOffline("保活失败，准备重连");
                Task.Run(async () =>
                {
                    try { await item.LoginAsync(true).ConfigureAwait(false); }
                    catch (Exception ex) { _log.Warn("保活失败后重连异常：" + ex.Message, item.Tag); }
                });
            }
        }

        private void ReconcileKeepalive(AccountItemViewModel item)
        {
            if (item == null) return;
            if (item.IsOnline && _settings.KeepaliveEnabled && !string.IsNullOrWhiteSpace(item.Session.UserIndex))
            {
                _keepalive.Start(item.Profile.Id,
                    () => item.Client.KeepaliveAsync(item.Session.UserIndex, item.Tag),
                    item.Session.KeepaliveInterval);
            }
            else
            {
                _keepalive.Stop(item.Profile.Id);
            }
        }

        private void OnSettingsSaved(AppSettings settings)
        {
            _settings = settings;
            _store.SaveSettings(_settings);

            if (_settings.AutoReconnectEnabled) _autoReconnect.UpdateInterval(_settings.ReconnectCheckSeconds);

            foreach (var item in Accounts.ToList()) ReconcileKeepalive(item);

            EditorHint = "设置已保存";
            UpdateTrayText();
        }

        private void UpdateTrayText()
        {
            var item = SelectedAccount;
            string text;
            if (item == null)
            {
                text = "CampusLink · 未选择账号";
            }
            else
            {
                text = "CampusLink · " + item.Title + " · " + item.StateText;
                if (!string.IsNullOrWhiteSpace(item.Session.LastMessage))
                {
                    text += " · " + item.Session.LastMessage;
                }
            }
            TrayTextChanged?.Invoke(text);
        }

        private void OnCommandError(Exception ex)
        {
            _log.Error("命令执行异常", ex);
            EditorHint = "操作异常：" + ex.Message;
        }

        // ---------------------------------------------------------------- 持久化

        public void PersistAccounts()
        {
            _store.SaveAccounts(Accounts.Select(a => a.Profile));
        }

        public void PersistSettings()
        {
            _store.SaveSettings(_settings);
        }

        private void LoadAccounts()
        {
            var profiles = _store.LoadAccounts();
            foreach (var profile in profiles)
            {
                Accounts.Add(CreateItem(profile));
            }

            if (profiles.Count == 0)
            {
                _log.Info("没有已保存的账号，点「新建账号」开始配置。");
            }
        }

        private AccountItemViewModel CreateItem(AccountProfile profile)
        {
            var item = new AccountItemViewModel(profile, _log, () => _settings);
            item.CaptchaRequested += (sender, args) => CaptchaRequested?.Invoke(this, args);
            item.StateChanged += (sender, args) =>
            {
                ReconcileKeepalive(item);
                UpdateTrayText();
            };
            item.LoginFailed += (sender, args) =>
            {
                _log.Warn("[" + item.Tag + "] 登录失败：" + args.Message, item.Tag);
            };
            return item;
        }

        private void SelectFirstAccount()
        {
            SelectedAccount = Accounts.FirstOrDefault();
        }

        private bool IsAnyBusy()
        {
            return Accounts.Any(a => a.IsBusy);
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _keepalive.Dispose();
            _autoReconnect.Dispose();
        }
    }
}
