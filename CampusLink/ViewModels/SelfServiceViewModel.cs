using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    /// <summary>
    /// 自助服务页：只能使用「账号」页里已保存的账号（学号 + 密码），
    /// 取验证码图片人工输入后做登录校验 / 默认密码检测。
    /// </summary>
    public class SelfServiceViewModel : ObservableObject
    {
        private readonly LogService _log;
        private readonly Func<AppSettings> _settings;
        private SelfServiceClient _client;
        private string _publicKey;

        private AccountItemViewModel _selectedAccount;
        private string _captcha = "";
        private string _candidatePassword = "";
        private string _status;
        private string _result = "";
        private BitmapSource _captchaImage;
        private bool _busy;

        public SelfServiceViewModel(LogService log, Func<AppSettings> settings,
            ObservableCollection<AccountItemViewModel> accounts)
        {
            _log = log;
            _settings = settings;
            Accounts = accounts;
            _status = accounts.Count == 0
                ? "还没有账号。请先到「账号」页添加账号，再回来做登录校验。"
                : "选择账号 → 获取验证码 → 输入验证码后提交。";

            FetchCaptchaCommand = new AsyncRelayCommand(FetchCaptchaAsync,
                () => _selectedAccount != null && !Busy, OnError);
            LoginCommand = new AsyncRelayCommand(() => AttemptAsync(AvailablePassword(), "登录校验"),
                () => CanOperate, OnError);
            CheckDefaultPasswordCommand = new AsyncRelayCommand(
                () => AttemptAsync(BuildCandidate(), "默认密码检测"), () => CanOperate, OnError);
            OpenSiteCommand = new RelayCommand(OpenSite);
            ClearCaptchaCommand = new RelayCommand(() =>
            {
                CaptchaImage = null;
                Captcha = "";
                _client = null;
                _publicKey = null;
                Status = "验证码已清空，请重新获取。";
            });
        }

        public ObservableCollection<AccountItemViewModel> Accounts { get; }

        public ICommand FetchCaptchaCommand { get; }
        public ICommand LoginCommand { get; }
        public ICommand CheckDefaultPasswordCommand { get; }
        public ICommand OpenSiteCommand { get; }
        public ICommand ClearCaptchaCommand { get; }

        public AccountItemViewModel SelectedAccount
        {
            get => _selectedAccount;
            set
            {
                if (!SetProperty(ref _selectedAccount, value)) return;
                OnPropertyChanged(nameof(AccountUserId));
                OnPropertyChanged(nameof(PasswordHint));
                OnPropertyChanged(nameof(CanOperate));
                // 换账号后旧的验证码会话就作废了
                CaptchaImage = null;
                Captcha = "";
                _client = null;
                _publicKey = null;
                Result = "";
                Status = value == null
                    ? "请选择一个账号。"
                    : "已选择 " + value.Title + "，点「获取验证码」开始。";
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string AccountUserId => _selectedAccount?.Profile.UserId ?? "";

        public string PasswordHint
        {
            get
            {
                if (_selectedAccount == null) return "使用账号页里保存的密码";
                return string.IsNullOrWhiteSpace(_selectedAccount.Profile.Password)
                    ? "⚠ 该账号没有保存密码，请到「账号」页编辑补充"
                    : "使用账号页里保存的密码";
            }
        }

        public bool HasAccounts => Accounts.Count > 0;

        public bool CanOperate => _selectedAccount != null && !Busy && _client != null;

        public string CandidatePassword
        {
            get => _candidatePassword;
            set { _candidatePassword = value; OnPropertyChanged(); OnPropertyChanged(nameof(CandidatePreview)); }
        }

        public string CandidatePreview =>
            "候选默认密码：" + (string.IsNullOrWhiteSpace(BuildCandidate()) ? "（待填写模板或学号）" : BuildCandidate());

        public string Captcha
        {
            get => _captcha;
            set { _captcha = value; OnPropertyChanged(); }
        }

        public BitmapSource CaptchaImage
        {
            get => _captchaImage;
            set { _captchaImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCaptcha)); }
        }

        public bool HasCaptcha => _captchaImage != null;

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string Result
        {
            get => _result;
            set { _result = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasResult)); }
        }

        public bool HasResult => !string.IsNullOrWhiteSpace(_result);

        public bool Busy
        {
            get => _busy;
            set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanOperate)); CommandManager.InvalidateRequerySuggested(); }
        }

        public string SelfServiceUrl => SelfServiceClient.NormalizeBase(_settings().SelfServiceBaseUrl) + "/";

        private string AvailablePassword()
        {
            var account = _selectedAccount;
            if (account == null) return "";
            return account.Profile.Password ?? "";
        }

        private string BuildCandidate()
        {
            if (!string.IsNullOrWhiteSpace(_candidatePassword)) return _candidatePassword;
            var template = _settings().DefaultPasswordTemplate;
            if (string.IsNullOrWhiteSpace(template)) template = "{uid}@Gzhu";
            return template.Replace("{uid}", AccountUserId ?? "");
        }

        public async System.Threading.Tasks.Task FetchCaptchaAsync()
        {
            if (_selectedAccount == null)
            {
                Status = "请先选择一个已保存的账号。";
                return;
            }

            Busy = true;
            try
            {
                Result = "";
                var client = new SelfServiceClient(_settings().SelfServiceBaseUrl, _log);
                var challenge = await client.BeginLoginAsync("自助服务").ConfigureAwait(true);

                if (challenge.CaptchaImage == null)
                {
                    Status = "获取验证码失败：" + challenge.Detail;
                    _client = null;
                    _publicKey = null;
                    return;
                }

                _client = client;
                _publicKey = challenge.PublicKey;
                CaptchaImage = ToBitmap(challenge.CaptchaImage);
                Captcha = "";
                Status = "验证码已刷新，请照图输入 4 位字符。";
            }
            finally
            {
                Busy = false;
            }
        }

        private async System.Threading.Tasks.Task AttemptAsync(string password, string action)
        {
            if (_client == null || string.IsNullOrWhiteSpace(_publicKey))
            {
                Status = "请先点「获取验证码」。";
                return;
            }
            if (_selectedAccount == null)
            {
                Status = "请先选择一个账号。";
                return;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                Status = action == "默认密码检测"
                    ? "候选密码为空，请填写默认密码模板（例如 {uid}@Gzhu）。"
                    : "该账号没有保存密码，请到「账号」页编辑补充。";
                return;
            }
            if (string.IsNullOrWhiteSpace(_captcha))
            {
                Status = "请先输入验证码。";
                return;
            }

            var userId = AccountUserId;
            Busy = true;
            try
            {
                Status = action + "中…";
                var result = await _client.LoginAsync(userId, password, _publicKey, _captcha, "自助服务").ConfigureAwait(true);

                if (result.Success)
                {
                    if (action == "默认密码检测")
                    {
                        Result = "⚠ 该账号仍然可以使用默认密码登录：" + password
                                 + "\n结论：默认密码没改，建议立刻在自助服务里修改。";
                        _log.Warn("账号 " + userId + " 疑似仍在使用默认密码");
                    }
                    else
                    {
                        Result = "✅ 登录成功，账号密码有效。\n如需改密/查余额，点「打开自助服务」在浏览器里操作（自助服务需要验证码，网页端更稳）。";
                        _log.Info("自助服务登录校验成功：" + userId);
                    }
                    Status = "完成。";
                }
                else if (result.CaptchaWrong)
                {
                    Status = "验证码错误：" + result.Message;
                    Result = "";
                }
                else if (result.PasswordWrong)
                {
                    if (action == "默认密码检测")
                    {
                        Result = "✅ 默认密码登录被拒绝，说明密码已经改过（安全）。\n服务器返回：" + result.Message;
                        _log.Info("账号 " + userId + " 默认密码检测通过");
                    }
                    else
                    {
                        Result = "❌ 登录失败：" + result.Message;
                    }
                    Status = "完成。";
                }
                else
                {
                    Status = "结果不确定：" + result.Message;
                    Result = result.RawText == null ? "" : result.RawText.Substring(0, Math.Min(400, result.RawText.Length));
                }
            }
            finally
            {
                Busy = false;
                CaptchaImage = null;
                Captcha = "";
            }
        }

        private void OpenSite()
        {
            ShellHelper.OpenUrl(SelfServiceUrl, _log);
            Status = "已在浏览器打开自助服务。";
        }

        private static BitmapSource ToBitmap(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private void OnError(Exception ex)
        {
            Busy = false;
            Status = "操作异常：" + ex.Message;
            _log.Error("自助服务操作异常", ex);
        }
    }
}
