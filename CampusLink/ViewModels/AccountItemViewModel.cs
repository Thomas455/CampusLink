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
        private static readonly Lazy<string> LocalMac = new Lazy<string>(FindLocalMac);

        private readonly LogService _log;
        private readonly Func<AppSettings> _settings;
        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);

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

        public string Subtitle
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                parts.Add(Profile.UserId);
                parts.Add(Client.Host);
                if (!string.IsNullOrWhiteSpace(Profile.SourceIp)) parts.Add("源IP " + Profile.SourceIp);
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
                    case LinkState.Online: return "#22C55E";
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
                var mac = !string.IsNullOrWhiteSpace(Profile.Mac)
                    ? Profile.Mac
                    : (!string.IsNullOrWhiteSpace(Session.DetectedMac) ? Session.DetectedMac : LocalMac.Value);
                return string.IsNullOrWhiteSpace(mac) ? "MAC 未知" : "MAC " + RuijieRsa.NormalizeMac(mac);
            }
        }

        public string SessionText
        {
            get
            {
                if (!Session.IsOnline) return "未建立会话";
                if (string.IsNullOrWhiteSpace(Session.UserIndex)) return "在线 · 无本地会话（可能 MAC 无感）";
                var index = Session.UserIndex;
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
            Client = new PortalClient(Profile.PortalHost, _log, Profile.SourceIp);
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
                    Session.State = LinkState.Online;
                    Session.QueryString = "";
                    Session.LastMessage = probe.Detail;
                    Session.LastLoginTime = DateTime.Now;
                    Refresh();
                    RaiseStateChanged();
                    return true;
                }

                Session.QueryString = probe.QueryString;
                if (!string.IsNullOrWhiteSpace(probe.Mac)) Session.DetectedMac = probe.Mac;
                Session.State = LinkState.LoggingIn;
                Session.LastMessage = "正在读取登录页配置…";
                Refresh();

                var page = await Client.GetLoginPageAsync(probe.QueryString, Tag).ConfigureAwait(true);

                var login = await Client.LoginAsync(
                    Profile, probe.QueryString, page, "", settings.PasswordEncryptMode, settings.RsaSeparator,
                    Session.DetectedMac, Tag).ConfigureAwait(true);

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
                        Session.DetectedMac, Tag).ConfigureAwait(true);
                }

                if (login.Success)
                {
                    Session.State = LinkState.Online;
                    Session.UserIndex = login.UserIndex;
                    Session.KeepaliveInterval = login.KeepaliveInterval;
                    Session.LastLoginTime = DateTime.Now;
                    Session.LastMessage = "登录成功";
                    _log.Info("[" + Tag + "] 登录成功", Tag);
                    Refresh();
                    RaiseStateChanged();

                    if (Profile.AutoRegisterMac && !string.IsNullOrWhiteSpace(Session.DetectedMac))
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
                if (string.IsNullOrWhiteSpace(Session.UserIndex))
                {
                    Session.LastMessage = "没有可下线的会话";
                    Refresh();
                    return;
                }

                var result = await Client.LogoutAsync(Session.UserIndex, Tag).ConfigureAwait(true);
                if (result.Success)
                {
                    Session.State = LinkState.Offline;
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
                    Session.LastMessage = probe.Detail;
                }
                else
                {
                    Session.State = LinkState.NeedAuth;
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
            if (string.IsNullOrWhiteSpace(Session.UserIndex))
            {
                if (!silent) Session.LastMessage = "需要先登录才能注册 MAC 无感";
                Refresh();
                return false;
            }

            var mac = !string.IsNullOrWhiteSpace(Profile.Mac) ? Profile.Mac : Session.DetectedMac;
            if (string.IsNullOrWhiteSpace(mac))
            {
                if (!silent) Session.LastMessage = "没有拿到 MAC，无法注册（可手动填写 MAC）";
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
                var mac = !string.IsNullOrWhiteSpace(Profile.Mac) ? Profile.Mac : Session.DetectedMac;
                if (string.IsNullOrWhiteSpace(mac))
                {
                    Session.LastMessage = "没有 MAC 可解绑（可在账号里手动填写）";
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

        public async Task RefreshOnlineInfoAsync()
        {
            if (!await _busy.WaitAsync(0).ConfigureAwait(true)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(Session.UserIndex))
                {
                    Session.LastMessage = "没有在线会话";
                    Refresh();
                    return;
                }

                var info = await Client.GetOnlineUserInfoAsync(Session.UserIndex, Tag).ConfigureAwait(true);
                if (info.Success)
                {
                    Session.LastMessage = "在线信息：IP " + info.UserIp + " / MAC " + info.UserMac;
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

        private static string FindLocalMac()
        {
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var address = nic.GetPhysicalAddress();
                    if (address == null) continue;
                    var text = address.ToString();
                    if (text.Length >= 12) return text;
                }
            }
            catch
            {
            }
            return "";
        }

        private void OnCommandError(Exception ex)
        {
            _log.Error("[" + Tag + "] 操作异常", ex, Tag);
            Session.LastMessage = "操作异常：" + ex.Message;
            Refresh();
        }
    }
}
