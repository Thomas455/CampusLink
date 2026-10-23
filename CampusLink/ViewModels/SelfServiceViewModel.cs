using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    /// <summary>
    /// 自助服务页：取公钥 + 验证码 → 登录校验 / 默认密码检测。
    /// 验证码需要人工识别（离线 C# 没有可靠的 OCR 方案），因此图片直接显示在界面上。
    /// </summary>
    public class SelfServiceViewModel : ObservableObject
    {
        private readonly LogService _log;
        private readonly Func<AppSettings> _settings;
        private SelfServiceClient _client;
        private string _publicKey;

        private string _userId = "";
        private string _password = "";
        private string _captcha = "";
        private string _candidatePassword = "";
        private string _status = "先填写学号与密码，然后点「获取验证码」。";
        private string _result = "";
        private BitmapSource _captchaImage;
        private bool _busy;

        public SelfServiceViewModel(LogService log, Func<AppSettings> settings)
        {
            _log = log;
            _settings = settings;

            FetchCaptchaCommand = new AsyncRelayCommand(FetchCaptchaAsync, () => !Busy, OnError);
            LoginCommand = new AsyncRelayCommand(() => AttemptAsync(Password, "登录校验"), () => !Busy, OnError);
            CheckDefaultPasswordCommand = new AsyncRelayCommand(
                () => AttemptAsync(BuildCandidate(), "默认密码检测"), () => !Busy, OnError);
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

        public ICommand FetchCaptchaCommand { get; }
        public ICommand LoginCommand { get; }
        public ICommand CheckDefaultPasswordCommand { get; }
        public ICommand OpenSiteCommand { get; }
        public ICommand ClearCaptchaCommand { get; }

        public string UserId
        {
            get => _userId;
            set { _userId = value; OnPropertyChanged(); OnPropertyChanged(nameof(CandidatePreview)); }
        }

        public string Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(); }
        }

        /// <summary>由 PasswordBox 在代码里同步进来。</summary>
        public string CandidatePassword
        {
            get => _candidatePassword;
            set { _candidatePassword = value; OnPropertyChanged(); OnPropertyChanged(nameof(CandidatePreview)); }
        }

        public string CandidatePreview =>
            "候选默认密码：" + (string.IsNullOrWhiteSpace(BuildCandidate()) ? "（待填写学号模板）" : BuildCandidate());

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
            set { _busy = value; OnPropertyChanged(); }
        }

        public string SelfServiceUrl => SelfServiceClient.NormalizeBase(_settings().SelfServiceBaseUrl) + "/";

        private string BuildCandidate()
        {
            if (!string.IsNullOrWhiteSpace(_candidatePassword)) return _candidatePassword;
            var template = _settings().DefaultPasswordTemplate;
            if (string.IsNullOrWhiteSpace(template)) template = "{uid}@Gzhu";
            return template.Replace("{uid}", _userId ?? "");
        }

        public async System.Threading.Tasks.Task FetchCaptchaAsync()
        {
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
            if (string.IsNullOrWhiteSpace(_userId))
            {
                Status = "请先填写学号。";
                return;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                Status = "请先填写密码。";
                return;
            }
            if (string.IsNullOrWhiteSpace(_captcha))
            {
                Status = "请先输入验证码。";
                return;
            }

            Busy = true;
            try
            {
                Status = action + "中…";
                var result = await _client.LoginAsync(_userId, password, _publicKey, _captcha, "自助服务").ConfigureAwait(true);

                if (result.Success)
                {
                    if (action == "默认密码检测")
                    {
                        Result = "⚠ 该账号仍然可以使用默认密码登录：" + password
                                 + "\n结论：默认密码没改，建议立刻在自助服务里修改。";
                        _log.Warn("账号 " + _userId + " 疑似仍在使用默认密码");
                    }
                    else
                    {
                        Result = "✅ 登录成功，账号密码有效。\n如需改密/查余额，点「打开自助服务」在浏览器里操作（自助服务需要验证码，网页端更稳）。";
                        _log.Info("自助服务登录校验成功：" + _userId);
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
                        _log.Info("账号 " + _userId + " 默认密码检测通过");
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

                // 验证码是一次性的，本次提交后必须重新获取
                CaptchaImage = null;
                Captcha = "";
            }
            finally
            {
                Busy = false;
                CaptchaImage = null;
            }
        }

        private void OpenSite()
        {
            try
            {
                Process.Start(new ProcessStartInfo(SelfServiceUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Status = "打开浏览器失败：" + ex.Message;
            }
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
