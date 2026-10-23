using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    /// <summary>
    /// 账号信息页：根据 userIndex 调 getOnlineUserInfo（必要时先从成功页发现 userIndex），
    /// 把门户返回的完整信息结构化展示。
    /// </summary>
    public class InfoViewModel : ObservableObject
    {
        private readonly LogService _log;
        private readonly ToastService _toast;
        private readonly Func<SchoolPreset> _preset;
        private AccountItemViewModel _account;
        private bool _busy;
        private string _status = "选择一个账号后点「刷新信息」";
        private string _rawJson = "";
        private bool _hasData;
        private bool _showRaw;

        public InfoViewModel(LogService log, ToastService toast, Func<SchoolPreset> activePreset)
        {
            _log = log;
            _toast = toast;
            _preset = activePreset;

            RefreshCommand = new AsyncRelayCommand(() => LoadAsync(false), () => Account != null && !Busy, OnError);
            CopyCommand = new RelayCommand(CopyInfo, () => HasData);
            OpenPortalCommand = new RelayCommand(() =>
            {
                var host = _preset()?.PortalHost;
                if (string.IsNullOrWhiteSpace(host)) host = Account?.Client.Host;
                ShellHelper.OpenUrl("http://" + PortalClient.NormalizeHost(host), _log);
                _toast.Info("已在浏览器打开门户", "打开门户");
            });
            OpenSelfServiceCommand = new RelayCommand(() =>
            {
                var url = _preset()?.SelfServiceUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    _toast.Warning("当前学校预设没有配置自助服务地址", "无法打开");
                    return;
                }
                ShellHelper.OpenUrl(url, _log);
                _toast.Info("已在浏览器打开自助服务", "打开自助服务");
            });
            ToggleRawCommand = new RelayCommand(() => ShowRaw = !ShowRaw);
        }

        public ICommand RefreshCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand OpenPortalCommand { get; }
        public ICommand OpenSelfServiceCommand { get; }
        public ICommand ToggleRawCommand { get; }

        public ObservableCollection<AccountInfoSection> Sections { get; } = new ObservableCollection<AccountInfoSection>();

        public AccountItemViewModel Account
        {
            get => _account;
            set
            {
                if (!SetProperty(ref _account, value)) return;
                UiDispatcher.Post(() =>
                {
                    Sections.Clear();
                    HasData = false;
                    RawJson = "";
                });
                OnPropertyChanged(nameof(AccountTitle));
                OnPropertyChanged(nameof(AccountSubtitle));
                OnPropertyChanged(nameof(HasAccount));
                CommandManager.InvalidateRequerySuggested();

                if (value == null)
                {
                    Status = "还没有选择账号";
                    return;
                }

                Status = value.IsOnline
                    ? "已选择 " + value.Title + "，正在获取信息…"
                    : "账号当前不在线，点「刷新信息」会尝试通过在线会话发现 userIndex";

                if (value.IsOnline)
                {
                    _ = LoadAsync(true);
                }
            }
        }

        public bool HasAccount => _account != null;

        public string AccountTitle => _account == null ? "未选择账号" : _account.Title;

        public string AccountSubtitle => _account == null
            ? "在「账号」页选择账号后这里会显示它的在线信息"
            : _account.Subtitle + "  ·  " + _account.StateText;

        public bool Busy
        {
            get => _busy;
            private set { if (SetProperty(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public bool HasData
        {
            get => _hasData;
            private set
            {
                if (!SetProperty(ref _hasData, value)) return;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string RawJson
        {
            get => _rawJson;
            private set => SetProperty(ref _rawJson, value);
        }

        public bool ShowRaw
        {
            get => _showRaw;
            set => SetProperty(ref _showRaw, value);
        }

        public string LastUpdatedText => "数据来自门户 getOnlineUserInfo";

        public async System.Threading.Tasks.Task LoadAsync(bool silent)
        {
            var account = Account;
            if (account == null)
            {
                Status = "还没有选择账号";
                return;
            }

            Busy = true;
            Status = "正在获取账号信息…";
            try
            {
                var info = await account.FetchFullInfoAsync().ConfigureAwait(true);
                if (info == null)
                {
                    HasData = false;
                    Status = "没有取到在线信息（可能当前不在线）";
                    if (!silent) _toast.Warning(Status, "查询失败");
                    return;
                }

                if (!info.Success && info.Raw == null)
                {
                    HasData = false;
                    Status = "查询失败：" + info.Message;
                    if (!silent) _toast.Error(info.Message, "查询失败");
                    return;
                }

                // 可能在后台线程触发（自动重连/保活），集合改动必须回到 UI 线程
                await UiDispatcher.InvokeAsync(() =>
                {
                    Build(info);
                    HasData = true;
                    Status = "更新于 " + DateTime.Now.ToString("HH:mm:ss") + "（会话 " + (account.Session.UserIndex.Length > 8 ? account.Session.UserIndex.Substring(0, 8) + "…" : "") + "）";
                }).ConfigureAwait(true);
                if (!silent) _toast.Success("账号信息已刷新", "账号信息");
            }
            catch (Exception ex)
            {
                Status = "查询失败：" + ex.Message;
                _log.Warn("账号信息查询失败：" + ex.Message, account.Tag);
                if (!silent) _toast.Error(ex.Message, "查询失败");
            }
            finally
            {
                Busy = false;
            }
        }

        private void Build(OnlineUserInfoResult info)
        {
            Sections.Clear();

            var account = new AccountInfoSection("账号");
            account.Fields.Add(new AccountInfoField("学号", MaskOr(info.UserId)));
            account.Fields.Add(new AccountInfoField("姓名", info.UserName));
            account.Fields.Add(new AccountInfoField("用户组", info.UserGroup));
            account.Fields.Add(new AccountInfoField("登录方式", LoginTypeText(info.LoginType)));
            account.Fields.Add(new AccountInfoField("服务", string.IsNullOrWhiteSpace(info.Service) ? "默认" : info.Service));
            Sections.Add(account);

            var network = new AccountInfoSection("网络");
            network.Fields.Add(new AccountInfoField("IP 地址", info.UserIp));
            network.Fields.Add(new AccountInfoField("MAC", RuijieRsa.NormalizeMac(info.UserMac)));
            network.Fields.Add(new AccountInfoField("门户地址", Account?.Client.Host ?? ""));
            network.Fields.Add(new AccountInfoField("门户 IP", info.PortalIp));
            network.Fields.Add(new AccountInfoField("是否本账号", MatchText(Account)));
            network.Fields.Add(new AccountInfoField("跳转地址", info.UserUrl));
            Sections.Add(network);

            if (info.Balls.Count > 0)
            {
                var balls = new AccountInfoSection("套餐与余额");
                foreach (var ball in info.Balls)
                {
                    balls.Fields.Add(new AccountInfoField(
                        string.IsNullOrWhiteSpace(ball.DisplayName) ? ball.Type : ball.DisplayName,
                        FormatBall(ball)));
                }
                Sections.Add(balls);
            }

            var notice = StripHtml(info.Announcement);
            if (!string.IsNullOrWhiteSpace(notice) || !string.IsNullOrWhiteSpace(info.WelcomeTip))
            {
                var section = new AccountInfoSection("门户通知");
                if (!string.IsNullOrWhiteSpace(info.WelcomeTip)) section.Fields.Add(new AccountInfoField("欢迎语", info.WelcomeTip));
                if (!string.IsNullOrWhiteSpace(notice)) section.Fields.Add(new AccountInfoField("公告", notice));
                Sections.Add(section);
            }

            var session = new AccountInfoSection("会话");
            session.Fields.Add(new AccountInfoField("userIndex", MaskUserIndex(Account?.Session.UserIndex)));
            session.Fields.Add(new AccountInfoField("保活周期", Account == null ? "" : Account.Session.KeepaliveInterval + " 秒"));
            session.Fields.Add(new AccountInfoField("登录时间",
                Account?.Session.LastLoginTime == null ? "未知" : Account.Session.LastLoginTime.Value.ToString("yyyy-MM-dd HH:mm:ss")));
            Sections.Add(session);

            RawJson = info.RawText ?? (info.Raw == null ? "" : info.Raw.ToString());
        }

        private static string MaskOr(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return "";
            // 信息页展示给本人，保留完整学号；日志里才脱敏
            return userId;
        }

        private static string MatchText(AccountItemViewModel account)
        {
            if (account == null) return "";
            switch (account.Session.OnlineAccountMatches)
            {
                case true: return "是（与账号页所选账号一致）";
                case false: return "否（在线的是 " + AccountProfile.MaskUserId(account.Session.OnlineUserId) + "）";
                default: return "未知";
            }
        }

        private static string LoginTypeText(string loginType)
        {
            switch ((loginType ?? "").Trim())
            {
                case "3": return "MAC 免认证 / 自动登录";
                case "1": return "账号密码";
                case "2": return "二维码";
                default: return string.IsNullOrWhiteSpace(loginType) ? "未知" : loginType;
            }
        }

        private static string FormatBall(AccountBallInfo ball)
        {
            var value = ball.Value ?? "";
            var type = (ball.Type ?? "").ToLowerInvariant();
            if (type == "periodtime" && long.TryParse(value, out var seconds) && seconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            if (type == "money")
            {
                return "¥ " + value;
            }
            if (type == "devicenum")
            {
                return value + " 台";
            }
            return value;
        }

        private static string MaskUserIndex(string userIndex)
        {
            if (string.IsNullOrWhiteSpace(userIndex)) return "";
            return userIndex.Length <= 8 ? "***" : userIndex.Substring(0, 8) + "…（" + userIndex.Length + " 字符）";
        }

        private static string StripHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return Regex.Replace(text, "<[^>]+>", " ").Replace("&nbsp;", " ").Trim();
        }

        private void CopyInfo()
        {
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine(AccountTitle + "  " + AccountSubtitle);
                builder.AppendLine("更新于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (var section in Sections)
                {
                    builder.AppendLine();
                    builder.AppendLine("【" + section.Title + "】");
                    foreach (var field in section.Fields)
                    {
                        builder.AppendLine(field.Label + "：" + field.Value);
                    }
                }
                System.Windows.Clipboard.SetText(builder.ToString());
                _toast.Success("账号信息已复制到剪贴板", "复制成功");
            }
            catch (Exception ex)
            {
                _toast.Error(ex.Message, "复制失败");
            }
        }

        private void OnError(Exception ex)
        {
            Busy = false;
            Status = "操作异常：" + ex.Message;
            _toast.Error(ex.Message, "操作异常");
            _log.Error("账号信息操作异常", ex);
        }
    }
}
