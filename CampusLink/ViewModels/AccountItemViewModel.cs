using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    public class CaptchaEventArgs : EventArgs
    {
        public byte[] Image { get; set; }
        public string Message { get; set; }
        public string Code { get; set; }
        public string Title { get; set; }
    }

    public class LoginFailedEventArgs : EventArgs
    {
        public string Message { get; set; }
    }

    public class AccountItemViewModel : ObservableObject
    {
        private readonly LogService _log;
        private readonly Func<AppSettings> _settings;
        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        private NetworkAdapterInfo _adapter;

        public AccountItemViewModel(AccountProfile profile, LogService log, Func<AppSettings> settings)
        {
            Profile = profile;
            _log = log;
            _settings = settings;
            RebuildClient();

            LoginCommand = new AsyncRelayCommand(() => LoginAsync(false), () => !IsBusy, OnCommandError);
            LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsBusy, OnCommandError);
            ProbeCommand = new AsyncRelayCommand(ProbeAsync, () => !IsBusy, OnCommandError);
            RegisterMacCommand = new AsyncRelayCommand(RegisterMacAsync, () => !IsBusy, OnCommandError);
            CancelMacCommand = new AsyncRelayCommand(CancelMacAsync, () => !IsBusy, OnCommandError);
            RefreshOnlineInfoCommand = new AsyncRelayCommand(RefreshOnlineInfoAsync, () => !IsBusy, OnCommandError);
        }

        public event EventHandler<CaptchaEventArgs> CaptchaRequested;
        public event EventHandler<LoginFailedEventArgs> LoginFailed;
        public event EventHandler StateChanged;

        public AccountProfile Profile { get; private set; }

        public SessionState Session { get; } = new SessionState();

        public PortalClient Client { get; private set; }

        public ICommand LoginCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand ProbeCommand { get; }
        public ICommand RegisterMacCommand { get; }
        public ICommand CancelMacCommand { get; }
        public ICommand RefreshOnlineInfoCommand { get; }

        public string Tag => Profile.Title;

        public string Title => Profile.Title;

        public NetworkAdapterInfo Adapter => _adapter;

        public string Subtitle
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                parts.Add(Profile.UserId);
                parts.Add(Client.Host);
                if (_adapter != null) parts.Add(_adapter.Name);
                else parts.Add("未绑定网卡");
                return string.Join("  ·  ", parts.ToArray());
            }
        }

        public string StateText => Session.StateText;

        public string StateColor
        {
            get
            {
                switch (Session.State)
                {
                    case LinkState.Online:
                        if (Session.Verified) return "#22C55E";
                        if (Session.OnlineAccountMatches == false) return "#F59E0B";
                        return "#0EA5E9";
                    case LinkState.NeedAuth: return "#F59E0B";
                    case LinkState.Probing:
                    case LinkState.LoggingIn: return "#3B82F6";
                    case LinkState.Error: return "#EF4444";
                    case LinkState.Offline: return "#94A3B8";
                    default: return "#64748B";
                }
            }
        }

        public string StateDetail => string.IsNullOrWhiteSpace(Session.LastMessage) ? "尚未检查" : Session.LastMessage;

        public string MacText
        {
            get
            {
                var mac = _adapter != null && !string.IsNullOrWhiteSpace(_adapter.Mac)
                    ? _adapter.Mac
                    : Session.DetectedMac;
                return string.IsNullOrWhiteSpace(mac) ? "MAC 未知" : "MAC " + RuijieRsa.NormalizeMac(mac);
            }
        }

        public string SessionText
        {
            get
            {
                if (!Session.IsOnline) return "未建立会话";
                if (!Session.Verified)
                {
                    if (!string.IsNullOrWhiteSpace(Session.OnlineUserId))
                    {
                        var suffix = Session.OnlineAccountMatches == true ? "（本账号）"
                            : Session.OnlineAccountMatches == false ? "（非本账号）" : "";
                        return "在线账号 " + Session.OnlineUserId + suffix;
                    }
                    return "网络已通（MAC 免认证，账号未识别）";
                }
                var index = Session.UserIndex ?? "";
                if (index.Length > 12) index = index.Substring(0, 12) + "...";
                return "userIndex " + index + "  ·  保活 " + Session.KeepaliveInterval + "s";
            }
        }

        public string LastCheckText => Session.LastCheckTime.HasValue
            ? "最近检查 " + Session.LastCheckTime.Value.ToString("HH:mm:ss")
            : "尚未检查";

        public bool IsOnline => Session.IsOnline;

        public bool IsBusy => Session.IsBusy;

        public string AutoFlags
        {
            get
            {
                var flags = new System.Collections.Generic.List<string>();
                if (Profile.AutoLogin) flags.Add("启动登录");
                if (Profile.AutoReconnect) flags.Add("掉线重连");
                if (Profile.AutoRegisterMac) flags.Add("自动无感");
                return flags.Count == 0 ? "无自动动作" : string.Join(" / ", flags.ToArray());
            }
        }

        public void ReplaceProfile(AccountProfile profile)
        {
            Profile = profile;
            RebuildClient();
            Refresh();
        }

        public void RebuildClient()
        {
            _adapter = NetworkAdapterService.Find(Profile.NicId);
            var sourceIp = _adapter == null ? null : _adapter.Ipv4;
            Client = new PortalClient(Profile.PortalHost, _log, sourceIp);
        }

        /// <summary>尝试登录；silent=true 时（自动重连）不弹验证码窗口。</summary>
        public async Task<bool> LoginAsync(bool silent)
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true)) return false;
            try
            {
                var settings = _settings();

                Session.State = LinkState.Probing;
                Session.LastMessage = "正在探测网络状态…";
                Session.LastCheckTime = DateTime.Now;
                Refresh();

                var probe = await Client.ProbeAsync(settings.ProbeUrlList(), Tag).ConfigureAwait(true);
                if (!probe.Ok)
                {
                    Fail(LinkState.Error, probe.Detail);
                    return false;
                }

                if (probe.Online)
                {
                    // 网络已通，但没有校验密码；尽量从成功页取 userIndex 再查在线账号是谁
                    Session.State = LinkState.Online;
                    Session.Verified = false;
                    Session.UserIndex = "";
                    Session.LastLoginTime = null;
                    Session.OnlineUserId = "";
                    Session.OnlineAccountMatches = null;
                    Session.LastMessage = "当前网络已在线（AC 返回成功页），正在识别在线账号…";
                    Refresh();

                    await RefreshOnlineIdentityAsync().ConfigureAwait(true);
                    RaiseStateChanged();
                    return true;
                }

                Session.QueryString = probe.QueryString;
                if (!string.IsNullOrWhiteSpace(probe.Mac)) Session.DetectedMac = probe.Mac;
                Session.State = LinkState.LoggingIn;
                Session.Verified = false;
                Session.LastMessage = "正在读取登录页配置…";
                Refresh();

                var page = await Client.GetLoginPageAsync(probe.QueryString, Tag).ConfigureAwait(true);

                var login = await Client.LoginAsync(
                    Profile, probe.QueryString, page, "", settings.PasswordEncryptMode, settings.RsaSeparator,
                    MacForAuth, Tag).ConfigureAwait(true);

                if (!login.Success && login.CaptchaRequired && !silent)
                {
                    var code = await RequestCaptchaAsync(login, settings).ConfigureAwait(true);
                    if (string.IsNullOrEmpty(code))
                    {
                        Fail(LinkState.NeedAuth, "已取消验证码输入");
                        return false;
                    }

                    login = await Client.LoginAsync(
                        Profile, probe.QueryString, page, code, settings.PasswordEncryptMode, settings.RsaSeparator,
                        MacForAuth, Tag).ConfigureAwait(true);
                }

                if (login.Success)
                {
                    Session.State = LinkState.Online;
                    Session.Verified = true;
                    Session.UserIndex = login.UserIndex;
                    Session.KeepaliveInterval = login.KeepaliveInterval;
                    Session.LastLoginTime = DateTime.Now;
                    Session.LastMessage = "登录成功（账号密码已通过门户校验）";
                    _log.Info("[" + Tag + "] 登录成功", Tag);
                    Refresh();
                    RaiseStateChanged();

                    if (Profile.AutoRegisterMac && !string.IsNullOrWhiteSpace(MacForAuth))
                    {
                        await TryRegisterMacAsync(silent: true).ConfigureAwait(true);
                    }
                    return true;
                }

                Fail(LinkState.Error, login.Message);
                LoginFailed?.Invoke(this, new LoginFailedEventArgs { Message = login.Message });
                return false;
            }
            catch (Exception ex)
            {
                Fail(LinkState.Error, ex.Message);
                _log.Error("[" + Tag + "] 登录异常", ex, Tag);
                return false;
            }
            finally
            {
                _busy.Release();
                Refresh();
            }
        }

        public string MacForAuth
        {
            get
            {
                if (_adapter != null && !string.IsNullOrWhiteSpace(_adapter.Mac)) return _adapter.Mac;
                return Session.DetectedMac;
            }
        }

        private async Task<string> RequestCaptchaAsync(LoginResult login, AppSettings settings)
        {
            var handler = CaptchaRequested;
            if (handler == null) return null;

            Session.LastMessage = "需要输入验证码";
            Refresh();

            var image = await Client.GetCaptchaImageAsync(login.ValidCodeUrl, Tag).ConfigureAwait(true);
            var args = new CaptchaEventArgs
            {
                Image = image,
                Title = "登录 " + Tag,
                Message = string.IsNullOrWhiteSpace(login.Message) ? "本次登录需要验证码" : login.Message
            };

            await UiDispatcher.InvokeAsync(() => handler(this, args)).ConfigureAwait(true);
            return args.Code;
        }

        public async Task LogoutAsync()
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                if (!Session.Verified || string.IsNullOrWhiteSpace(Session.UserIndex))
                {
                    Session.LastMessage = "当前没有本地登录会话。若是 MAC 免认证在线，需要「强制下线全部」才能踢掉。";
                    Refresh();
                    return;
                }

                var result = await Client.LogoutAsync(Session.UserIndex, Tag).ConfigureAwait(true);
                if (result.Success)
                {
                    Session.State = LinkState.Offline;
                    Session.Verified = false;
                    Session.UserIndex = "";
                    Session.LastMessage = "已下线";
                    _log.Info("[" + Tag + "] 已下线", Tag);
                }
                else
                {
                    Session.State = LinkState.Error;
                    Session.LastMessage = "下线失败：" + result.Message;
                }
                Refresh();
                RaiseStateChanged();
            }
            finally
            {
                _busy.Release();
            }
        }

        public async Task ProbeAsync()
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                Session.State = LinkState.Probing;
                Session.LastMessage = "正在探测…";
                Refresh();

                var probe = await Client.ProbeAsync(_settings().ProbeUrlList(), Tag).ConfigureAwait(true);
                Session.LastCheckTime = DateTime.Now;
                if (!probe.Ok)
                {
                    Session.State = LinkState.Error;
                    Session.LastMessage = probe.Detail;
                }
                else if (probe.Online)
                {
                    Session.State = LinkState.Online;
                    Session.Verified = false;
                    Session.UserIndex = "";
                    Session.OnlineUserId = "";
                    Session.OnlineAccountMatches = null;
                    Session.LastMessage = probe.Detail;
                    await RefreshOnlineIdentityAsync().ConfigureAwait(true);
                }
                else
                {
                    Session.State = LinkState.NeedAuth;
                    Session.Verified = false;
                    Session.QueryString = probe.QueryString;
                    Session.DetectedMac = probe.Mac;
                    Session.LastMessage = probe.Detail;
                }
                Refresh();
                RaiseStateChanged();
            }
            finally
            {
                _busy.Release();
            }
        }

        public async Task RegisterMacAsync()
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                await TryRegisterMacAsync(silent: false).ConfigureAwait(true);
            }
            finally
            {
                _busy.Release();
            }
        }

        private async Task<bool> TryRegisterMacAsync(bool silent)
        {
            var canUseSession = !string.IsNullOrWhiteSpace(Session.UserIndex)
                                && (Session.Verified || Session.OnlineAccountMatches == true);
            if (!canUseSession)
            {
                if (!silent)
                {
                    Session.LastMessage = Session.IsOnline && !Session.Verified
                        ? "当前是在线免认证状态，且没能确认在线账号就是本账号，暂不注册无感。"
                        : "需要先登录成功才能注册 MAC 无感。";
                }
                Refresh();
                return false;
            }

            var mac = MacForAuth;
            if (string.IsNullOrWhiteSpace(mac))
            {
                if (!silent) Session.LastMessage = "没有拿到 MAC，无法注册（请在账号里选择网卡）";
                Refresh();
                return false;
            }

            var result = await Client.RegisterMacAsync(mac, Session.UserIndex, Tag).ConfigureAwait(true);
            if (result.Success)
            {
                Session.LastMessage = "MAC 无感注册成功：" + RuijieRsa.NormalizeMac(mac);
                _log.Info("[" + Tag + "] " + Session.LastMessage, Tag);
            }
            else
            {
                Session.LastMessage = "MAC 无感注册失败：" + result.Message;
                _log.Warn("[" + Tag + "] " + Session.LastMessage, Tag);
            }
            Refresh();
            return result.Success;
        }

        public async Task CancelMacAsync()
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                var mac = MacForAuth;
                if (string.IsNullOrWhiteSpace(mac))
                {
                    Session.LastMessage = "没有 MAC 可解绑（请在账号里选择网卡）";
                    Refresh();
                    return;
                }

                var result = await Client.CancelMacAsync(Profile.UserId, mac, Tag).ConfigureAwait(true);
                Session.LastMessage = result.Success
                    ? "已解绑 MAC 无感：" + RuijieRsa.NormalizeMac(mac)
                    : "解绑失败：" + result.Message;
                _log.Info("[" + Tag + "] " + Session.LastMessage, Tag);
                Refresh();
            }
            finally
            {
                _busy.Release();
            }
        }

        /// <summary>
        /// 已在线（MAC 免认证）时，从成功页拿 userIndex，再用 getOnlineUserInfo 确认当前在线的账号，
        /// 并和本账号比对。这样"乱输密码也显示在线"的情况下能看出到底是谁在线。
        /// </summary>
        public async Task RefreshOnlineIdentityAsync()
        {
            var discovery = await Client.DiscoverOnlineSessionAsync(Tag).ConfigureAwait(true);
            if (!discovery.Found)
            {
                Session.LastMessage = "网络已在线（MAC 免认证），但没能从成功页取到 userIndex：" + discovery.Message;
                Refresh();
                return;
            }

            Session.UserIndex = discovery.UserIndex;

            var info = await Client.GetOnlineUserInfoAsync(discovery.UserIndex, Tag).ConfigureAwait(true);
            Session.OnlineUserId = !string.IsNullOrWhiteSpace(info.UserId) ? info.UserId : discovery.DecodedUserId;
            Session.OnlineUserIp = !string.IsNullOrWhiteSpace(info.UserIp) ? info.UserIp : discovery.DecodedIp;
            Session.OnlineUserMac = info.UserMac;

            if (!string.IsNullOrWhiteSpace(Session.OnlineUserId))
            {
                Session.OnlineAccountMatches = string.Equals(
                    Session.OnlineUserId, Profile.UserId, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Session.OnlineAccountMatches = null;
            }

            if (Session.OnlineAccountMatches == true)
            {
                Session.LastMessage = "当前在线账号：" + Session.OnlineUserId
                                      + (string.IsNullOrWhiteSpace(Session.OnlineUserIp) ? "" : "（IP " + Session.OnlineUserIp + "）")
                                      + "，与本账号一致（密码未被校验）";
            }
            else if (Session.OnlineAccountMatches == false)
            {
                Session.LastMessage = "⚠ 当前在线的是账号 " + Session.OnlineUserId
                                      + "，不是本账号 " + Profile.UserId + "（密码未被校验）";
            }
            else
            {
                Session.LastMessage = "网络已在线（MAC 免认证），但没能识别出在线账号";
            }

            _log.Info("[" + Tag + "] " + Session.LastMessage, Tag);
            Refresh();
        }

        public async Task RefreshOnlineInfoAsync()
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                if (!Session.IsOnline)
                {
                    Session.LastMessage = "当前判定为离线，请先点「登录」或「探测」。";
                    Refresh();
                    return;
                }

                if (!Session.Verified)
                {
                    await RefreshOnlineIdentityAsync().ConfigureAwait(true);
                    return;
                }

                var info = await Client.GetOnlineUserInfoAsync(Session.UserIndex, Tag).ConfigureAwait(true);
                if (info.Success)
                {
                    Session.LastMessage = "在线信息：" + (string.IsNullOrWhiteSpace(info.UserName) ? "" : info.UserName + " / ")
                                          + "账号 " + info.UserId + " / IP " + info.UserIp + " / MAC " + info.UserMac;
                }
                else
                {
                    Session.LastMessage = "在线信息查询失败：" + info.Message;
                }
                Refresh();
            }
            finally
            {
                _busy.Release();
            }
        }

        public void MarkKeepalive(bool success, string message, int interval)
        {
            if (success)
            {
                Session.LastMessage = "保活正常（每 " + interval + " 秒）";
                Session.LastCheckTime = DateTime.Now;
            }
            else
            {
                Session.State = LinkState.Error;
                Session.LastMessage = "保活失败：" + message;
            }
            Refresh();
        }

        public void MarkOffline(string reason)
        {
            Session.State = LinkState.Offline;
            Session.Verified = false;
            Session.UserIndex = "";
            Session.LastMessage = reason;
            Refresh();
            RaiseStateChanged();
        }

        public void Refresh()
        {
            UiDispatcher.Post(() =>
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateColor));
                OnPropertyChanged(nameof(StateDetail));
                OnPropertyChanged(nameof(MacText));
                OnPropertyChanged(nameof(SessionText));
                OnPropertyChanged(nameof(LastCheckText));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(AutoFlags));
                OnPropertyChanged(nameof(Adapter));
            });
        }

        private void Fail(LinkState state, string message)
        {
            Session.State = state;
            Session.LastMessage = message;
            Refresh();
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnCommandError(Exception ex)
        {
            _log.Error("[" + Tag + "] 操作异常", ex, Tag);
            Session.LastMessage = "操作异常：" + ex.Message;
            Refresh();
        }
    }
}
