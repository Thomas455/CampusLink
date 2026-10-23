using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    /// <summary>
    /// 诊断页：把每一步的原始请求/响应打印出来，方便在真实校园网里确认
    /// queryString、passwordEncrypt、RSA 公钥、服务列表等关键信息。
    /// </summary>
    public class DiagnosticsViewModel : ObservableObject
    {
        private readonly LogService _log;
        private readonly Func<AppSettings> _settings;
        private readonly StringBuilder _buffer = new StringBuilder();

        private AccountItemViewModel _account;
        private string _output = "";
        private string _status = "选择一个账号后，可逐项调用门户接口。";
        private bool _busy;

        public DiagnosticsViewModel(LogService log, Func<AppSettings> settings)
        {
            _log = log;
            _settings = settings;

            ProbeCommand = new AsyncRelayCommand(ProbeAsync, () => CanRun, OnError);
            PageInfoCommand = new AsyncRelayCommand(PageInfoAsync, () => CanRun, OnError);
            LoginPageCommand = new AsyncRelayCommand(LoginPageAsync, () => CanRun, OnError);
            ServicesCommand = new AsyncRelayCommand(ServicesAsync, () => CanRun, OnError);
            OnlineInfoCommand = new AsyncRelayCommand(OnlineInfoAsync, () => CanRun, OnError);
            CopyCommand = new RelayCommand(CopyOutput);
            ClearCommand = new RelayCommand(() => { _buffer.Clear(); Output = ""; Status = "已清空。"; });
        }

        public ICommand ProbeCommand { get; }
        public ICommand PageInfoCommand { get; }
        public ICommand LoginPageCommand { get; }
        public ICommand ServicesCommand { get; }
        public ICommand OnlineInfoCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand ClearCommand { get; }

        public AccountItemViewModel Account
        {
            get => _account;
            set
            {
                _account = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AccountHeader));
                OnPropertyChanged(nameof(HasAccount));
                OnPropertyChanged(nameof(CanRun));
            }
        }

        public bool HasAccount => _account != null;

        public bool CanRun => _account != null && !_busy;

        public string AccountHeader => _account == null
            ? "未选择账号"
            : _account.Profile.UserId + " @ " + _account.Client.Host;

        public string Output
        {
            get => _output;
            set { _output = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        private void Append(string title, string content)
        {
            _buffer.AppendLine("===== " + title + "  " + DateTime.Now.ToString("HH:mm:ss") + " =====");
            _buffer.AppendLine(content ?? "(空)");
            _buffer.AppendLine();
            Output = _buffer.ToString();
        }

        private async System.Threading.Tasks.Task ProbeAsync()
        {
            if (_account == null) return;
            _busy = true;
            Status = "探测中…";
            try
            {
                var result = await _account.Client.ProbeAsync(_settings().ProbeUrlList(), _account.Tag).ConfigureAwait(true);
                var text = "探测地址: " + result.ProbeUrl
                           + "\n结果: " + (result.Ok ? "成功" : "失败")
                           + "\n需要认证: " + result.NeedsAuth
                           + "\n已在线: " + result.Online
                           + "\nMAC: " + result.Mac
                           + "\nqueryString: " + result.QueryString
                           + "\nLocation: " + result.RedirectUrl
                           + "\n说明: " + result.Detail;
                Append("探测 (GET)", text);
                Status = result.NeedsAuth ? "拿到 queryString，可继续看登录页配置。" : result.Detail;

                if (result.NeedsAuth)
                {
                    _account.Session.QueryString = result.QueryString;
                    _account.Session.DetectedMac = result.Mac;
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private async System.Threading.Tasks.Task PageInfoAsync()
        {
            if (!await EnsureQueryStringAsync().ConfigureAwait(true)) return;
            _busy = true;
            Status = "查询 pageInfo…";
            try
            {
                var result = await _account.Client.GetPageInfoAsync(_account.Session.QueryString, _account.Tag).ConfigureAwait(true);
                var text = "成功: " + result.Success
                           + "\nisAutoLogin: " + result.IsAutoLogin
                           + "\nvalidCodeUrl: " + result.ValidCodeUrl
                           + "\n公告: " + result.Announcement
                           + "\n通知: " + result.SystemNotice
                           + "\n\n原始响应:\n" + result.RawText;
                Append("pageInfo", text);
                Status = result.Success ? "pageInfo 成功（无副作用）" : result.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async System.Threading.Tasks.Task LoginPageAsync()
        {
            if (!await EnsureQueryStringAsync().ConfigureAwait(true)) return;
            _busy = true;
            Status = "读取登录页…";
            try
            {
                var page = await _account.Client.GetLoginPageAsync(_account.Session.QueryString, _account.Tag).ConfigureAwait(true);
                var text = "passwordEncrypt: " + (page.PasswordEncrypt == "" ? "(未找到)" : page.PasswordEncrypt)
                           + "\npublicKeyExponent: " + page.PublicKeyExponent
                           + "\npublicKeyModulus 长度: " + (page.PublicKeyModulus == null ? 0 : page.PublicKeyModulus.Length)
                           + "\n检测到验证码字段: " + page.HasCaptcha
                           + "\n\n页面片段(前 1200 字符):\n" + Truncate(page.RawHtml, 1200);
                Append("登录页 index.jsp", text);
                Status = page.HasRsaKey ? "已拿到 RSA 公钥。" : "未找到 RSA 公钥（可能是明文模式）。";
            }
            finally
            {
                _busy = false;
            }
        }

        private async System.Threading.Tasks.Task ServicesAsync()
        {
            if (!await EnsureQueryStringAsync().ConfigureAwait(true)) return;
            _busy = true;
            Status = "查询 getServices…";
            try
            {
                var result = await _account.Client.GetServicesAsync(_account.Session.QueryString, _account.Tag).ConfigureAwait(true);
                var builder = new StringBuilder();
                builder.AppendLine("成功: " + result.Success);
                builder.AppendLine("默认服务: " + result.DefaultService);
                foreach (var item in result.Services) builder.AppendLine(" - " + item);
                builder.AppendLine();
                builder.AppendLine("原始响应:");
                builder.AppendLine(result.RawText);
                Append("getServices", builder.ToString());
                Status = result.Success ? "服务数：" + result.Services.Count : result.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async System.Threading.Tasks.Task OnlineInfoAsync()
        {
            if (_account == null) return;
            if (string.IsNullOrWhiteSpace(_account.Session.UserIndex))
            {
                Status = "当前没有 userIndex，请先登录。";
                return;
            }
            _busy = true;
            Status = "查询在线信息…";
            try
            {
                var result = await _account.Client.GetOnlineUserInfoAsync(_account.Session.UserIndex, _account.Tag).ConfigureAwait(true);
                var text = "成功: " + result.Success
                           + "\nuserId: " + result.UserId
                           + "\nuserIp: " + result.UserIp
                           + "\nuserMac: " + result.UserMac
                           + "\nuserUrl: " + result.UserUrl
                           + "\n\n原始响应:\n" + result.RawText;
                Append("getOnlineUserInfo", text);
                Status = result.Success ? "查询完成。" : result.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async System.Threading.Tasks.Task<bool> EnsureQueryStringAsync()
        {
            if (_account == null) return false;
            if (!string.IsNullOrWhiteSpace(_account.Session.QueryString)) return true;

            Status = "还没有 queryString，先探测…";
            await ProbeAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(_account.Session.QueryString))
            {
                Status = "未拿到 queryString（可能已在线或不在校园网环境）。";
                return false;
            }
            return true;
        }

        private void CopyOutput()
        {
            try
            {
                if (!string.IsNullOrEmpty(Output)) Clipboard.SetText(Output);
                Status = "已复制到剪贴板。";
            }
            catch (Exception ex)
            {
                Status = "复制失败：" + ex.Message;
            }
        }

        private void OnError(Exception ex)
        {
            _busy = false;
            Status = "操作异常：" + ex.Message;
            _log.Error("诊断操作异常", ex);
        }

        private static string Truncate(string text, int length)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= length ? text : text.Substring(0, length) + "…";
        }
    }
}
