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
        private readonly ToastService _toast;
        private readonly Func<AppSettings> _settings;
        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        private NetworkAdapterInfo _adapter;

        public AccountItemViewModel(AccountProfile profile, LogService log, Func<AppSettings> settings,
            ToastService toast)
        {
            Profile = profile;
            _log = log;
            _toast = toast;
            _settings = settings;
            RebuildClient();

            LoginCommand = new AsyncRelayCommand(() => LoginAsync(false), () => !IsBusy, OnCommandError);
            LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsBusy, OnCommandError);
            ProbeCommand = new AsyncRelayCommand(() => ProbeAsync(false), () => !IsBusy, OnCommandError);
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

        /// <summary>最近一次 getOnlineUserInfo 的完整结果（信息页用）。</summary>
        public OnlineUserInfoResult LastInfo { get; private set; }

        public ICommand LoginCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand ProbeCommand { get; }
        public ICommand RegisterMacCommand { get; }
        public ICommand CancelMacCommand { get; }
        public ICommand RefreshOnlineInfoCommand { get; }

        /// <summary>日志/接口里用的标识：显示名称或学号后 3 位。</summary>
        public string Tag => Profile.LogName;

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

        /// <summary>卡片上的持久状态说明（过程性提示走右下角 Toast）。</summary>
        public string StateDetail
        {
            get
            {
                switch (Session.State)
                {
                    case LinkState.Online:
                        if (Session.Verified) return "已通过门户登录（账号密码已校验）";
                        if (Session.OnlineAccountMatches == true) return "当前在线账号与本账号一致（密码未校验）";
                        if (Session.OnlineAccountMatches == false)
                            return "⚠ 当前在线的是其他账号 " + AccountProfile.MaskUserId(Session.OnlineUserId);
                        return "网络已在线（MAC 免认证）";
                    case LinkState.NeedAuth: return "需要认证：点「登录」";
                    case LinkState.Probing: return "正在探测网络…";
                    case LinkState.LoggingIn: return "正在登录…";
                    case LinkState.Error: return string.IsNullOrWhiteSpace(Session.LastMessage) ? "操作失败" : Session.LastMessage;
                    case LinkState.Offline: return "离线";
                    default: return "尚未检查";
                }
            }
        }

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
                    return Session.OnlineAccountMatches == true ? "在线账号与本账号一致" : "在线（未建立本地登录会话）";
                }
                return "保活 " + Session.KeepaliveInterval + "s";
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

        /// <summary>尝试登录；silent=true 时（自动重连）不弹验证码窗口、成功不弹提示。</summary>
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
                    if (!silent) _toast.Error("登录失败：" + probe.Detail);
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
                    Session.LastMessage = "网络已在线（AC 返回成功页）";
                    Refresh();

                    await RefreshOnlineIdentityAsync(silent: true).ConfigureAwait(true);
                    RaiseStateChanged();

                    if (!silent)
                    {
                        if (Session.OnlineAccountMatches == true)
                        {
                            _toast.Info("网络已在线，当前在线账号与本账号一致；本次未校验密码", "无需登录");
                        }
                        else if (Session.OnlineAccountMatches == false)
                        {
                            _toast.Warning("当前在线的是账号 " + AccountProfile.MaskUserId(Session.OnlineUserId)
                                           + "，不是本账号 " + AccountProfile.MaskUserId(Profile.UserId), "在线账号不一致");
                        }
                        else
                        {
                            _toast.Info("网络已在线（MAC 免认证），未能识别在线账号", "无需登录");
                        }
                    }
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
                        _toast.Warning("已取消验证码输入", "登录未完成");
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
                    if (!silent) _toast.Success("登录成功：" + Title, "已连接校园网");

                    // 顺手拉一次完整信息，供「账号信息」页使用
                    try
                    {
                        LastInfo = await Client.GetOnlineUserInfoAsync(Session.UserIndex, Tag).ConfigureAwait(true);
                    }
                    catch
                    {
                    }

                    if (Profile.AutoRegisterMac && !string.IsNullOrWhiteSpace(MacForAuth))
                    {
                        await TryRegisterMacAsync(silent: true).ConfigureAwait(true);
                    }
                    return true;
                }

                Fail(LinkState.Error, login.Message);
                LoginFailed?.Invoke(this, new LoginFailedEventArgs { Message = login.Message });
                _toast.Error(login.Message, "登录失败");
                return false;
            }
            catch (Exception ex)
            {
                Fail(LinkState.Error, ex.Message);
                _log.Error("[" + Tag + "] 登录异常", ex, Tag);
                _toast.Error(ex.Message, "登录异常");
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
                Title = "登录 " + Title,
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
                    Session.LastMessage = "当前没有本地登录会话";
                    Refresh();
                    _toast.Warning("当前没有本地登录会话；MAC 免认证在线时无法单独下线", "无法下线");
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
                    _toast.Success("已下线：" + Title, "已断开");
                }
                else
                {
                    Session.State = LinkState.Error;
                    Session.LastMessage = "下线失败：" + result.Message;
                    _toast.Error(result.Message, "下线失败");
                }
                Refresh();
                RaiseStateChanged();
            }
            finally
            {
                _busy.Release();
            }
        }

        /// <summary>静默探测（silent=true 时不弹提示，自动重连/刷新用）。</summary>
        public async Task ProbeAsync(bool silent = false)
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
                    if (!silent) _toast.Error(probe.Detail, "探测失败");
                }
                else if (probe.Online)
                {
                    Session.State = LinkState.Online;
                    Session.Verified = false;
                    Session.UserIndex = "";
                    Session.OnlineUserId = "";
                    Session.OnlineAccountMatches = null;
                    Session.LastMessage = probe.Detail;
                    await RefreshOnlineIdentityAsync(silent: true).ConfigureAwait(true);
                    if (!silent) _toast.Info("网络已在线（MAC 免认证）", "探测完成");
                }
                else
                {
                    Session.State = LinkState.NeedAuth;
                    Session.Verified = false;
                    Session.QueryString = probe.QueryString;
                    Session.DetectedMac = probe.Mac;
                    Session.LastMessage = probe.Detail;
                    if (!silent) _toast.Warning("当前未认证，需要登录", "探测完成");
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
                var ok = await TryRegisterMacAsync(silent: false).ConfigureAwait(true);
                if (ok) _toast.Success("MAC 无感注册成功：" + RuijieRsa.NormalizeMac(MacForAuth), "无感认证");
                else _toast.Error(Session.LastMessage, "无感注册失败");
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
                Session.LastMessage = Session.IsOnline && !Session.Verified
                    ? "当前是在线免认证状态，且没能确认在线账号就是本账号，暂不注册无感。"
                    : "需要先登录成功才能注册 MAC 无感。";
                Refresh();
                if (!silent) _toast.Warning(Session.LastMessage, "无法注册");
                return false;
            }

            var mac = MacForAuth;
            if (string.IsNullOrWhiteSpace(mac))
            {
                Session.LastMessage = "没有拿到 MAC，无法注册（请在账号里选择网卡）";
                Refresh();
                if (!silent) _toast.Error(Session.LastMessage, "无法注册");
                return false;
            }

            var result = await Client.RegisterMacAsync(mac, Session.UserIndex, Tag).ConfigureAwait(true);
            Session.LastMessage = result.Success
                ? "MAC 无感注册成功：" + RuijieRsa.NormalizeMac(mac)
                : "MAC 无感注册失败：" + result.Message;
            if (result.Success) _log.Info("[" + Tag + "] " + Session.LastMessage, Tag);
            else _log.Warn("[" + Tag + "] " + Session.LastMessage, Tag);
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
                    _toast.Error(Session.LastMessage, "无法解绑");
                    return;
                }

                var result = await Client.CancelMacAsync(Profile.UserId, mac, Tag).ConfigureAwait(true);
                Session.LastMessage = result.Success
                    ? "已解绑 MAC 无感：" + RuijieRsa.NormalizeMac(mac)
                    : "解绑失败：" + result.Message;
                _log.Info("[" + Tag + "] " + Session.LastMessage, Tag);
                Refresh();
                if (result.Success) _toast.Success(Session.LastMessage, "已解绑");
                else _toast.Error(result.Message, "解绑失败");
            }
            finally
            {
                _busy.Release();
            }
        }

        /// <summary>
        /// 已在线（MAC 免认证）时，从成功页拿 userIndex，再用 getOnlineUserInfo 确认当前在线的账号，
        /// 并和本账号比对。
        /// </summary>
        public async Task RefreshOnlineIdentityAsync(bool silent = true)
        {
            var discovery = await Client.DiscoverOnlineSessionAsync(Tag).ConfigureAwait(true);
            if (!discovery.Found)
            {
                Session.LastMessage = "网络已在线（MAC 免认证），但没能从成功页取到 userIndex：" + discovery.Message;
                Refresh();
                if (!silent) _toast.Warning(Session.LastMessage, "在线账号识别失败");
                return;
            }

            Session.UserIndex = discovery.UserIndex;
            LastInfo = await Client.GetOnlineUserInfoAsync(discovery.UserIndex, Tag).ConfigureAwait(true);

            Session.OnlineUserId = !string.IsNullOrWhiteSpace(LastInfo.UserId) ? LastInfo.UserId : discovery.DecodedUserId;
            Session.OnlineUserIp = !string.IsNullOrWhiteSpace(LastInfo.UserIp) ? LastInfo.UserIp : discovery.DecodedIp;
            Session.OnlineUserMac = LastInfo.UserMac;

            Session.OnlineAccountMatches = string.IsNullOrWhiteSpace(Session.OnlineUserId)
                ? (bool?)null
                : string.Equals(Session.OnlineUserId, Profile.UserId, StringComparison.OrdinalIgnoreCase);

            Session.LastMessage = Session.OnlineAccountMatches == true
                ? "当前在线账号与本账号一致"
                : Session.OnlineAccountMatches == false
                    ? "当前在线的是账号 " + AccountProfile.MaskUserId(Session.OnlineUserId)
                    : "网络已在线，但没能识别出在线账号";

            _log.Info("[" + Tag + "] " + Session.LastMessage, Tag);
            Refresh();
        }

        /// <summary>信息页：拉取本账号的完整在线信息（含发现 userIndex）。</summary>
        public async Task<OnlineUserInfoResult> FetchFullInfoAsync()
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true))
            {
                return LastInfo;
            }
            try
            {
                if (string.IsNullOrWhiteSpace(Session.UserIndex))
                {
                    var discovery = await Client.DiscoverOnlineSessionAsync(Tag).ConfigureAwait(true);
                    if (!discovery.Found)
                    {
                        _toast.Warning("拿不到 userIndex，无法查询信息：" + discovery.Message, "查询失败");
                        return null;
                    }
                    Session.UserIndex = discovery.UserIndex;
                }

                var info = await Client.GetOnlineUserInfoAsync(Session.UserIndex, Tag).ConfigureAwait(true);
                LastInfo = info;
                if (info.Success)
                {
                    Session.OnlineUserId = info.UserId;
                    Session.OnlineUserIp = info.UserIp;
                    Session.OnlineUserMac = info.UserMac;
                    if (!string.IsNullOrWhiteSpace(info.UserId))
                    {
                        Session.OnlineAccountMatches = string.Equals(info.UserId, Profile.UserId, StringComparison.OrdinalIgnoreCase);
                    }
                    Session.State = LinkState.Online;
                }
                Refresh();
                return info;
            }
            catch (Exception ex)
            {
                _log.Warn("[" + Tag + "] 查询账号信息失败：" + ex.Message, Tag);
                _toast.Error(ex.Message, "查询失败");
                return null;
            }
            finally
            {
                _busy.Release();
            }
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
                    _toast.Warning(Session.LastMessage, "未在线");
                    return;
                }

                await RefreshOnlineIdentityAsync(silent: true).ConfigureAwait(true);
                var info = LastInfo;
                if (info != null && info.Success)
                {
                    _toast.Success("在线信息已刷新：" + (string.IsNullOrWhiteSpace(info.UserName) ? "" : info.UserName + " / ")
                                   + "IP " + info.UserIp, "在线信息");
                }
                else
                {
                    _toast.Error(info == null ? "没有取得在线信息" : info.Message, "在线信息");
                }
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
                Session.LastCheckTime = DateTime.Now;
            }
            else
            {
                Session.State = LinkState.Error;
                Session.LastMessage = "保活失败：" + message;
                _toast.Warning("会话保活失败：" + message, "网络可能已断开");
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
            _toast.Error(ex.Message, "操作异常");
        }
    }
}
