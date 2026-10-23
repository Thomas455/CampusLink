using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);

        private AppSettings _settings;
        private AccountItemViewModel _selected;
        private string _editorPassword = "";
        private string _editorError = "";
        private string _editorHint = "";
        private bool _isEditorOpen;
        private bool _editorIsNew = true;
        private string _editorTitle = "添加账号";
        private NetworkAdapterInfo _selectedAdapter;
        private bool _disposed;

        public MainViewModel(LogService log, AccountStore store)
        {
            _log = log;
            _store = store;
            _settings = store.LoadSettings();

            _autoReconnect = new AutoReconnectService(log);
            _autoReconnect.CheckDue += OnReconnectTick;
            _keepalive.Tick += OnKeepaliveTick;

            Accounts = new ObservableCollection<AccountItemViewModel>();
            Adapters = new ObservableCollection<NetworkAdapterInfo>();

            Settings = new SettingsViewModel(_settings, log, OnSettingsSaved);
            SelfService = new SelfServiceViewModel(log, () => _settings, Accounts);
            Diagnostics = new DiagnosticsViewModel(log, () => _settings);
            About = new AboutViewModel(log, _store.DataDirectory, _log.LogDirectory);

            AddAccountCommand = new RelayCommand(OpenAddEditor);
            EditAccountCommand = new RelayCommand<AccountItemViewModel>(OpenEditEditor);
            SaveAccountCommand = new RelayCommand(SaveAccount);
            CancelEditCommand = new RelayCommand(CloseEditor);
            DeleteAccountCommand = new RelayCommand(DeleteAccount, () => SelectedAccount != null);
            ForceLogoutCommand = new AsyncRelayCommand(ForceLogoutAsync, () => SelectedAccount != null, OnCommandError);
            RefreshAdaptersCommand = new RelayCommand(ReloadAdapters);
            LoginAllCommand = new AsyncRelayCommand(LoginAllAsync, () => !IsAnyBusy(), OnCommandError);
            ProbeAllCommand = new AsyncRelayCommand(ProbeAllAsync, () => !IsAnyBusy(), OnCommandError);
            OpenDataFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_store.DataDirectory, _log));
            OpenLogFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_log.LogDirectory, _log));

            ReloadAdapters();
            LoadAccounts();
            SelectFirstAccount();
        }

        public event EventHandler<CaptchaEventArgs> CaptchaRequested;
        public event Action<string> TrayTextChanged;

        public ObservableCollection<AccountItemViewModel> Accounts { get; }

        public ObservableCollection<NetworkAdapterInfo> Adapters { get; }

        public LogService Log => _log;

        public SettingsViewModel Settings { get; }
        public SelfServiceViewModel SelfService { get; }
        public DiagnosticsViewModel Diagnostics { get; }
        public AboutViewModel About { get; }

        public ICommand AddAccountCommand { get; }
        public ICommand EditAccountCommand { get; }
        public ICommand SaveAccountCommand { get; }
        public ICommand CancelEditCommand { get; }
        public ICommand DeleteAccountCommand { get; }
        public ICommand ForceLogoutCommand { get; }
        public ICommand RefreshAdaptersCommand { get; }
        public ICommand LoginAllCommand { get; }
        public ICommand ProbeAllCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand OpenLogFolderCommand { get; }

        // ---------------------------------------------------------------- 选中账号

        public AccountItemViewModel SelectedAccount
        {
            get => _selected;
            set
            {
                if (!SetProperty(ref _selected, value)) return;
                Diagnostics.Account = value;
                if (SelfService != null && value != null) SelfService.SelectedAccount = value;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionTitle));
                UpdateTrayText();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasSelection => _selected != null;

        public string SelectionTitle => _selected == null ? "未选择账号" : _selected.Title;

        // ---------------------------------------------------------------- 编辑器

        public bool IsEditorOpen
        {
            get => _isEditorOpen;
            private set
            {
                if (!SetProperty(ref _isEditorOpen, value)) return;
                OnPropertyChanged(nameof(IsEditorClosed));
            }
        }

        public bool IsEditorClosed => !_isEditorOpen;

        public string EditorTitle
        {
            get => _editorTitle;
            private set => SetProperty(ref _editorTitle, value);
        }

        public string EditorDisplayName { get; set; } = "";
        public string EditorUserId { get; set; } = "";
        public string EditorService { get; set; } = "";
        public string EditorPortalHost { get; set; } = "";
        public string EditorRemark { get; set; } = "";
        public bool EditorAutoLogin { get; set; } = true;
        public bool EditorAutoReconnect { get; set; } = true;
        public bool EditorAutoRegisterMac { get; set; }

        public NetworkAdapterInfo SelectedAdapter
        {
            get => _selectedAdapter;
            set
            {
                if (!SetProperty(ref _selectedAdapter, value)) return;
                OnPropertyChanged(nameof(AdapterHint));
            }
        }

        public string AdapterHint
        {
            get
            {
                if (SelectedAdapter == null) return "未选择网卡：将使用探测到的 MAC，且不绑定源 IP。";
                return "认证 MAC：" + RuijieRsa.NormalizeMac(SelectedAdapter.Mac)
                       + (string.IsNullOrWhiteSpace(SelectedAdapter.Ipv4) ? "（该网卡没有 IPv4）" : "，源 IP：" + SelectedAdapter.Ipv4);
            }
        }

        public string EditorPasswordHint => _editorIsNew ? "新账号必填" : "留空表示不修改已有密码";

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

        private void OpenAddEditor()
        {
            _editorIsNew = true;
            IsEditorOpen = true;
            EditorTitle = "添加账号";
            EditorDisplayName = "";
            EditorUserId = "";
            EditorService = "";
            EditorPortalHost = _settings.DefaultPortalHost;
            EditorRemark = "";
            EditorAutoLogin = true;
            EditorAutoReconnect = true;
            EditorAutoRegisterMac = false;
            _editorPassword = "";
            SelectedAdapter = Adapters.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Ipv4)) ?? Adapters.FirstOrDefault();

            EditorError = "";
            EditorHint = "";
            PushEditorFields();
            OnPropertyChanged(nameof(EditorPasswordHint));
        }

        private void OpenEditEditor(AccountItemViewModel item)
        {
            if (item == null) return;
            SelectedAccount = item;
            _editorIsNew = false;
            IsEditorOpen = true;
            EditorTitle = "编辑：" + item.Title;

            var profile = item.Profile;
            EditorDisplayName = profile.DisplayName;
            EditorUserId = profile.UserId;
            EditorService = profile.Service;
            EditorPortalHost = profile.PortalHost;
            EditorRemark = profile.Remark;
            EditorAutoLogin = profile.AutoLogin;
            EditorAutoReconnect = profile.AutoReconnect;
            EditorAutoRegisterMac = profile.AutoRegisterMac;
            _editorPassword = "";
            SelectedAdapter = Adapters.FirstOrDefault(a => string.Equals(a.Id, profile.NicId, StringComparison.OrdinalIgnoreCase));

            EditorError = "";
            EditorHint = "";
            PushEditorFields();
            OnPropertyChanged(nameof(EditorPasswordHint));
        }

        private void PushEditorFields()
        {
            OnPropertyChanged(nameof(EditorDisplayName));
            OnPropertyChanged(nameof(EditorUserId));
            OnPropertyChanged(nameof(EditorService));
            OnPropertyChanged(nameof(EditorPortalHost));
            OnPropertyChanged(nameof(EditorRemark));
            OnPropertyChanged(nameof(EditorAutoLogin));
            OnPropertyChanged(nameof(EditorAutoReconnect));
            OnPropertyChanged(nameof(EditorAutoRegisterMac));
            OnPropertyChanged(nameof(SelectedAdapter));
            OnPropertyChanged(nameof(AdapterHint));
            OnPropertyChanged(nameof(EditorTitle));
            OnPropertyChanged(nameof(EditorPasswordHint));
        }

        private void CloseEditor()
        {
            _editorPassword = "";
            EditorError = "";
            IsEditorOpen = false;
            OnPropertyChanged(nameof(IsEditorOpen));
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
            var nicId = SelectedAdapter == null ? "" : SelectedAdapter.Id;

            if (_editorIsNew)
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
                    NicId = nicId,
                    Service = (EditorService ?? "").Trim(),
                    PortalHost = portalHost,
                    AutoLogin = EditorAutoLogin,
                    AutoReconnect = EditorAutoReconnect,
                    AutoRegisterMac = EditorAutoRegisterMac,
                    Remark = (EditorRemark ?? "").Trim()
                };

                var item = CreateItem(profile);
                Accounts.Add(item);
                SelectedAccount = item;
                _log.Info("新增账号：" + profile.UserId);
            }
            else
            {
                var existing = SelectedAccount;
                if (existing == null)
                {
                    EditorError = "请先在左侧选择要编辑的账号";
                    return;
                }

                var updated = existing.Profile.Clone();
                updated.DisplayName = (EditorDisplayName ?? "").Trim();
                updated.UserId = userId;
                updated.NicId = nicId;
                updated.Service = (EditorService ?? "").Trim();
                updated.PortalHost = portalHost;
                updated.AutoLogin = EditorAutoLogin;
                updated.AutoReconnect = EditorAutoReconnect;
                updated.AutoRegisterMac = EditorAutoRegisterMac;
                updated.Remark = (EditorRemark ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(_editorPassword)) updated.Password = _editorPassword;

                var endpointChanged = existing.Profile.NicId != updated.NicId
                                      || existing.Profile.PortalHost != updated.PortalHost;

                if (endpointChanged)
                {
                    _keepalive.Stop(existing.Profile.Id);
                    existing.MarkOffline("网卡/门户已修改，需要重新登录");
                }

                existing.ReplaceProfile(updated);
                _log.Info("更新账号：" + updated.UserId);
            }

            _editorPassword = "";
            PersistAccounts();
            EditorHint = "已保存：" + userId;
            CloseEditor();
            OnPropertyChanged(nameof(SelectionTitle));
            OnPropertyChanged(nameof(EditorPasswordHint));
            UpdateTrayText();
            CommandManager.InvalidateRequerySuggested();

            // 保存账号后主动同步一次它的状态
            RefreshOneAccount(SelectedAccount, "账号已保存");
        }

        private void DeleteAccount()
        {
            var item = SelectedAccount;
            if (item == null)
            {
                _editorError = "请先选择要删除的账号";
                OnPropertyChanged(nameof(EditorError));
                return;
            }

            var answer = MessageBox.Show(
                "确定要删除账号「" + item.Title + "」吗？\n只删除本机保存的配置，不影响校园网服务器上的账号。",
                "删除账号", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            _keepalive.Stop(item.Profile.Id);
            Accounts.Remove(item);

            if (_isEditorOpen && _editorIsNew == false && SelectedAccount == item)
            {
                IsEditorOpen = false;
            }

            SelectedAccount = Accounts.FirstOrDefault();
            PersistAccounts();
            UpdateTrayText();
            _log.Info("删除账号：" + item.Profile.UserId);
            CommandManager.InvalidateRequerySuggested();
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

        private async Task LoginAllAsync()
        {
            var items = Accounts.ToList();
            if (items.Count == 0)
            {
                EditorHint = "没有可登录的账号";
                return;
            }

            var verified = 0;
            var online = 0;
            foreach (var item in items)
            {
                if (await item.LoginAsync(true).ConfigureAwait(true))
                {
                    online++;
                    if (item.Session.Verified) verified++;
                }
            }

            EditorHint = "批量结果：" + online + "/" + items.Count + " 网络可用，其中 " + verified + " 个账号真正通过门户校验";
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
                if (item.IsOnline && item.Session.Verified) continue;

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
            if (item.Session.Verified && _settings.KeepaliveEnabled && !string.IsNullOrWhiteSpace(item.Session.UserIndex))
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

            EditorHint = "设置已保存，正在同步账号状态…";
            UpdateTrayText();
            RefreshAllAccounts("设置已保存");
        }

        /// <summary>保存设置/账号后主动探测一次状态，避免界面停留在旧状态。</summary>
        private async void RefreshOneAccount(AccountItemViewModel item, string reason)
        {
            if (item == null) return;
            if (!await _refreshGate.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                await Task.Delay(250).ConfigureAwait(true);
                if (item.IsBusy) return;
                await item.ProbeAsync().ConfigureAwait(true);
                if (item.Profile.AutoLogin && item.Session.State == LinkState.NeedAuth)
                {
                    await item.LoginAsync(true).ConfigureAwait(true);
                }
                _log.Debug("已刷新账号状态（" + reason + "）：" + item.Tag, item.Tag);
            }
            catch (Exception ex)
            {
                _log.Warn("刷新账号状态失败：" + ex.Message, item.Tag);
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private async void RefreshAllAccounts(string reason)
        {
            if (Accounts.Count == 0) return;
            if (!await _refreshGate.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                await Task.Delay(250).ConfigureAwait(true);
                foreach (var item in Accounts.ToList())
                {
                    if (item.IsBusy) continue;
                    await item.ProbeAsync().ConfigureAwait(true);
                    if (item.Profile.AutoLogin && item.Session.State == LinkState.NeedAuth)
                    {
                        await item.LoginAsync(true).ConfigureAwait(true);
                    }
                }
                _log.Info("账号状态已刷新（" + reason + "）");
                EditorHint = "账号状态已刷新";
            }
            catch (Exception ex)
            {
                _log.Warn("批量刷新账号状态失败：" + ex.Message);
            }
            finally
            {
                _refreshGate.Release();
            }
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
                _log.Info("没有已保存的账号，点「添加账号」开始配置。");
            }
            else
            {
                _log.Info("已载入 " + profiles.Count + " 个账号");
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

        public void ReloadAdapters()
        {
            var currentId = SelectedAdapter == null ? null : SelectedAdapter.Id;
            Adapters.Clear();
            foreach (var adapter in NetworkAdapterService.GetAdapters())
            {
                Adapters.Add(adapter);
            }

            if (currentId != null)
            {
                SelectedAdapter = Adapters.FirstOrDefault(a => string.Equals(a.Id, currentId, StringComparison.OrdinalIgnoreCase));
            }
            _log.Debug("网卡列表已刷新，共 " + Adapters.Count + " 块");
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
