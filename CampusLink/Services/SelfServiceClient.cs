using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CampusLink.Infrastructure;
using CampusLink.Models;

namespace CampusLink.Services
{
    /// <summary>
    /// 锐捷 SAM+ 自助服务系统（/selfservice/）客户端。
    /// 登录必须带验证码，因此流程拆成两步：先取验证码图片 + 公钥，再由界面录入验证码后提交。
    /// </summary>
    public class SelfServiceClient
    {
        private readonly HttpTransport _http;
        private readonly LogService _log;

        public SelfServiceClient(string baseUrl, LogService log)
        {
            _log = log;
            BaseUrl = NormalizeBase(baseUrl);
            _http = new HttpTransport();
        }

        public string BaseUrl { get; }

        private string LoginPageUrl => BaseUrl + "/module/scgroup/web/login_self.jsf";
        private string LoginPostUrl => BaseUrl + "/module/scgroup/web/login_judge.jsf";
        private string CaptchaUrl => BaseUrl + "/common/web/verifycode.jsp";

        /// <summary>取登录页公钥 + 验证码图片（绑定同一个 JSESSIONID）。</summary>
        public async Task<SelfServiceChallenge> BeginLoginAsync(string tag)
        {
            var challenge = new SelfServiceChallenge();

            var page = await _http.SendAsync(LoginPageUrl, "GET", null, null, null, 10000, true).ConfigureAwait(false);
            if (!page.Ok)
            {
                challenge.Detail = "打开登录页失败：" + page.Error;
                return challenge;
            }

            var html = page.Text;
            var keyMatch = Regex.Match(html, "id\\s*=\\s*[\"']publicKey[\"'][^>]*value\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
            if (!keyMatch.Success)
            {
                keyMatch = Regex.Match(html, "value\\s*=\\s*[\"']([0-9a-fA-F]{4,}&[0-9a-fA-F]{16,})[\"']");
            }

            if (!keyMatch.Success)
            {
                challenge.Detail = "登录页未找到 RSA 公钥，页面结构可能已变化";
                _log.Warn(challenge.Detail, tag);
                return challenge;
            }

            challenge.PublicKey = System.Net.WebUtility.HtmlDecode(keyMatch.Groups[1].Value).Trim();

            var captcha = await _http.SendAsync(
                CaptchaUrl + "?timestamp=" + DateTime.Now.Ticks,
                "GET", null, null, LoginPageUrl, 10000, false).ConfigureAwait(false);

            if (!captcha.Ok || captcha.Body == null || captcha.Body.Length == 0)
            {
                challenge.Detail = "验证码下载失败：" + (captcha.Error ?? ("HTTP " + captcha.StatusCode));
                _log.Warn(challenge.Detail, tag);
                return challenge;
            }

            challenge.CaptchaImage = captcha.Body;
            _log.Info("自助服务：已取得公钥与验证码（" + captcha.Body.Length + " 字节）", tag);
            return challenge;
        }

        /// <summary>提交登录（用于校验账号密码 / 默认密码检测）。</summary>
        public async Task<SelfServiceLoginResult> LoginAsync(string userId, string password, string publicKey, string captcha, string tag)
        {
            var result = new SelfServiceLoginResult();

            if (string.IsNullOrWhiteSpace(publicKey))
            {
                result.Message = "缺少 RSA 公钥，无法加密密码";
                return result;
            }

            string encrypted;
            try
            {
                var separator = publicKey.IndexOf('&');
                if (separator <= 0)
                {
                    result.Message = "公钥格式不认识：" + publicKey.Substring(0, Math.Min(24, publicKey.Length));
                    return result;
                }
                var exponent = publicKey.Substring(0, separator);
                var modulus = publicKey.Substring(separator + 1);
                encrypted = RuijieRsa.EncryptPassword(exponent, modulus, password);
            }
            catch (Exception ex)
            {
                result.Message = "密码加密失败：" + ex.Message;
                _log.Error("自助服务 RSA 加密失败", ex, tag);
                return result;
            }

            var body = "from=rsa"
                       + "&name=" + PortalClient.FormEncode(userId)
                       + "&password=" + PortalClient.FormEncode(encrypted)
                       + "&verify=" + PortalClient.FormEncode(captcha)
                       + "&verifyMsg=";

            _log.Debug("自助服务登录提交：name=" + userId + "，verify=" + captcha, tag);

            var response = await _http.SendAsync(LoginPostUrl, "POST", body,
                "application/x-www-form-urlencoded", LoginPageUrl, 15000, false).ConfigureAwait(false);

            if (!response.Ok)
            {
                result.Message = response.Error;
                return result;
            }

            result.RawText = response.Text;
            var text = response.Text ?? "";

            if (Regex.IsMatch(text, "index_self\\.jsf", RegexOptions.IgnoreCase))
            {
                result.Success = true;
                result.Message = "登录成功";
                _log.Info("自助服务：登录成功（" + userId + "）", tag);
                return result;
            }

            var redirect = Regex.Match(text, "self\\.location\\s*=\\s*['\"]login_self\\.jsf\\?([^'\"]*)['\"]", RegexOptions.IgnoreCase);
            if (redirect.Success)
            {
                var query = redirect.Groups[1].Value;
                if (Regex.IsMatch(query, "(^|&)verifyError=true", RegexOptions.IgnoreCase))
                {
                    result.CaptchaWrong = true;
                    result.Message = "验证码错误，请重新获取验证码";
                    return result;
                }

                var msgMatch = Regex.Match(query, "(^|&)errorMsg=([^&']*)");
                if (msgMatch.Success)
                {
                    result.Message = Uri.UnescapeDataString(msgMatch.Groups[2].Value.Replace("+", " "));
                    if (result.Message.IndexOf("验证码", StringComparison.Ordinal) >= 0 ||
                        result.Message.IndexOf("校验码", StringComparison.Ordinal) >= 0)
                    {
                        result.CaptchaWrong = true;
                    }
                    else
                    {
                        result.PasswordWrong = true;
                    }
                    return result;
                }

                result.Message = "返回登录页但未附带原因";
                return result;
            }

            // 兜底：直接回到登录表单
            if (text.IndexOf("loginform", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.PasswordWrong = true;
                result.Message = "登录被拒绝（账号或密码错误）";
                return result;
            }

            result.Message = "无法识别的登录响应";
            return result;
        }

        public static string NormalizeBase(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return "http://222.201.54.23:8080/selfservice";
            var value = baseUrl.Trim();
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "http://" + value;
            }
            return value.TrimEnd('/');
        }
    }
}
