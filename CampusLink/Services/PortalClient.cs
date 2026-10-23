using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CampusLink.Infrastructure;
using CampusLink.Models;
using Newtonsoft.Json.Linq;

namespace CampusLink.Services
{
    /// <summary>
    /// 锐捷 eportal 门户（InterFace.do）客户端。所有方法都不做界面更新，只返回结果对象。
    /// </summary>
    public class PortalClient
    {
        private readonly HttpTransport _http;
        private readonly LogService _log;

        public PortalClient(string portalHost, LogService log, string sourceIp)
        {
            Host = NormalizeHost(portalHost);
            BaseUrl = ToBaseUrl(Host);
            _http = new HttpTransport(sourceIp);
            _log = log;
        }

        public string Host { get; }

        public string BaseUrl { get; }

        // ---------------------------------------------------------------- 探测

        /// <summary>
        /// 依次访问探测地址，靠 AC 的 302 拦截拿到 queryString（登录必需，不能伪造）。
        /// </summary>
        public async Task<ProbeResult> ProbeAsync(IEnumerable<string> probeUrls, string tag)
        {
            var result = new ProbeResult();
            var targets = new List<string>();
            if (probeUrls != null) targets.AddRange(probeUrls);
            if (targets.Count == 0) targets.Add(BaseUrl + "/");

            var errors = new List<string>();

            foreach (var target in targets)
            {
                result.ProbeUrl = target;
                var response = await _http.SendAsync(target, "GET", null, null, null, 8000, false).ConfigureAwait(false);

                if (!response.Ok)
                {
                    errors.Add(target + " -> " + response.Error);
                    continue;
                }

                var location = response.RawLocation ?? "";

                if (location.IndexOf("redirectortosuccess", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Ok = true;
                    result.Online = true;
                    result.Detail = "已在线（AC 返回成功页）";
                    result.RedirectUrl = location;
                    _log.Info("探测：" + target + " -> 已在线", tag);
                    return result;
                }

                if (TryExtractQueryString(location, result))
                {
                    result.Ok = true;
                    result.NeedsAuth = true;
                    result.Detail = "未认证（已拿到 queryString）";
                    _log.Info("探测：" + target + " -> 未认证，MAC=" + result.Mac, tag);
                    return result;
                }

                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && location.Length > 0)
                {
                    result.Ok = true;
                    result.Online = true;
                    result.Detail = "未被 AC 拦截（可能已在线或不在校园网）：" + location;
                    result.RedirectUrl = location;
                    _log.Info("探测：" + target + " -> " + response.StatusCode + " " + location, tag);
                    return result;
                }

                if ((int)response.StatusCode == 200)
                {
                    // 未被拦截：要么已在线，要么该探测地址本身可直连
                    result.Ok = true;
                    result.Online = true;
                    result.Detail = "HTTP 未被拦截（可能已在线）";
                    _log.Info("探测：" + target + " -> 200 未被拦截", tag);
                    return result;
                }

                errors.Add(target + " -> HTTP " + response.StatusCode);
            }

            result.Ok = false;
            result.Detail = errors.Count > 0
                ? "所有探测地址都失败：" + string.Join("；", errors.ToArray())
                : "没有可用的探测地址";
            return result;
        }

        /// <summary>从 302 Location 里抠出 index.jsp? 之后的完整查询串和 mac。</summary>
        private static bool TryExtractQueryString(string location, ProbeResult result)
        {
            if (string.IsNullOrEmpty(location)) return false;
            var indexPos = location.IndexOf("index.jsp", StringComparison.OrdinalIgnoreCase);
            var questionPos = location.IndexOf('?');
            if (indexPos < 0 || questionPos < 0 || questionPos < indexPos) return false;

            var queryString = location.Substring(questionPos + 1);
            var hashPos = queryString.IndexOf('#');
            if (hashPos >= 0) queryString = queryString.Substring(0, hashPos);
            if (queryString.Length == 0) return false;

            result.QueryString = queryString;
            result.RedirectUrl = location;

            var macMatch = Regex.Match(queryString, "(?:^|&)mac=([^&]*)", RegexOptions.IgnoreCase);
            if (macMatch.Success)
            {
                result.Mac = Uri.UnescapeDataString(macMatch.Groups[1].Value);
            }
            return true;
        }

        // ---------------------------------------------------------------- 页面配置

        public async Task<PageInfoResult> GetPageInfoAsync(string queryString, string tag)
        {
            var response = await PostInterfaceAsync("pageInfo",
                "queryString=" + DoubleEncode(queryString), tag).ConfigureAwait(false);

            var result = new PageInfoResult { Success = response.Ok, RawText = response.Text, Message = response.Error };
            var json = JsonHelper.ParseObject(response.Text);
            if (json == null)
            {
                if (response.Ok) result.Message = "响应不是 JSON：" + Truncate(response.Text, 200);
                return result;
            }

            result.Raw = json;
            result.Success = string.Equals(JsonHelper.GetString(json, "result", "success"), "success", StringComparison.OrdinalIgnoreCase);
            if (!result.Success) result.Message = JsonHelper.GetString(json, "message", "pageInfo 失败");

            result.IsAutoLogin = JsonHelper.GetBool(json, "isAutoLogin", false);
            result.Announcement = JsonHelper.GetString(json, "announcement", "");
            result.SystemNotice = JsonHelper.GetString(json, "systemNotice", "");
            result.ValidCodeUrl = JsonHelper.GetString(json, "validCodeUrl", "");
            return result;
        }

        public async Task<ServicesResult> GetServicesAsync(string queryString, string tag)
        {
            var response = await PostInterfaceAsync("getServices",
                "queryString=" + DoubleEncode(queryString), tag).ConfigureAwait(false);

            var result = new ServicesResult { Success = response.Ok, RawText = response.Text, Message = response.Error };
            var json = JsonHelper.ParseObject(response.Text);
            if (json == null)
            {
                if (response.Ok) result.Message = "响应不是 JSON：" + Truncate(response.Text, 200);
                return result;
            }

            result.Success = string.Equals(JsonHelper.GetString(json, "result", "success"), "success", StringComparison.OrdinalIgnoreCase);
            if (!result.Success) result.Message = JsonHelper.GetString(json, "message", "getServices 失败");
            result.DefaultService = JsonHelper.GetString(json, "defaultService", "");

            var servicesToken = json["services"] ?? json["serviceJson"];
            if (servicesToken != null)
            {
                JArray array = servicesToken as JArray;
                if (array == null && servicesToken.Type == JTokenType.String)
                {
                    var raw = servicesToken.ToString();
                    if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith("["))
                    {
                        try { array = JArray.Parse(raw); } catch { }
                    }
                }
                if (array != null)
                {
                    foreach (var item in array)
                    {
                        var option = new ServiceOption
                        {
                            ServiceName = JsonHelper.GetString(item as JObject, "serviceName", ""),
                            ShowName = JsonHelper.GetString(item as JObject, "serviceShowName", "")
                        };
                        if (string.IsNullOrEmpty(option.ShowName)) option.ShowName = JsonHelper.GetString(item as JObject, "showName", "");
                        if (option.ServiceName.Length > 0 || option.ShowName.Length > 0) result.Services.Add(option);
                    }
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- 登录页

        /// <summary>抓取带合法 queryString 的登录页，解析 passwordEncrypt 与 RSA 公钥。</summary>
        public async Task<LoginPageInfo> GetLoginPageAsync(string queryString, string tag)
        {
            var url = BaseUrl + "/eportal/index.jsp?" + queryString;
            var response = await _http.SendAsync(url, "GET", null, null, BaseUrl + "/", 10000, false).ConfigureAwait(false);
            var info = new LoginPageInfo { RawHtml = response.Text };

            if (!response.Ok || string.IsNullOrEmpty(info.RawHtml))
            {
                _log.Warn("登录页获取失败：" + (response.Error ?? ("HTTP " + response.StatusCode)), tag);
                return info;
            }

            info.PasswordEncrypt = GetInputValue(info.RawHtml, "passwordEncrypt");
            info.PublicKeyExponent = GetInputValue(info.RawHtml, "publicKeyExponent");
            info.PublicKeyModulus = GetInputValue(info.RawHtml, "publicKeyModulus");
            info.ValidCodeUrl = GetInputValue(info.RawHtml, "validCodeUrl");

            // 兼容把公钥合并成一个字段（"exponent&modulus"）的固件
            if (!info.HasRsaKey)
            {
                var combined = GetInputValue(info.RawHtml, "publicKey");
                if (!string.IsNullOrWhiteSpace(combined))
                {
                    var parts = combined.Split('&');
                    if (parts.Length >= 2)
                    {
                        info.PublicKeyExponent = parts[0];
                        info.PublicKeyModulus = parts[1];
                    }
                }
            }

            info.HasCaptcha = info.RawHtml.IndexOf("validcode", StringComparison.OrdinalIgnoreCase) >= 0
                              || info.RawHtml.IndexOf("validateCode", StringComparison.OrdinalIgnoreCase) >= 0;

            _log.Info("登录页：passwordEncrypt=" + (info.PasswordEncrypt == "" ? "(未找到)" : info.PasswordEncrypt)
                      + "，RSA 公钥=" + (info.HasRsaKey ? "有" : "无"), tag);
            return info;
        }

        /// <summary>下载验证码图片（登录触发验证码时使用）。</summary>
        public async Task<byte[]> GetCaptchaImageAsync(string validCodeUrl, string tag)
        {
            if (string.IsNullOrWhiteSpace(validCodeUrl)) return null;
            var url = HttpTransport.CombineUrl(BaseUrl + "/", validCodeUrl);
            var response = await _http.SendAsync(url, "GET", null, null, BaseUrl + "/eportal/", 10000, false).ConfigureAwait(false);
            if (!response.Ok || response.Body == null || response.Body.Length == 0)
            {
                _log.Warn("验证码下载失败：" + (response.Error ?? ("HTTP " + response.StatusCode)), tag);
                return null;
            }
            return response.Body;
        }

        // ---------------------------------------------------------------- 登录 / 下线

        public async Task<LoginResult> LoginAsync(
            AccountProfile account,
            string queryString,
            LoginPageInfo page,
            string validCode,
            string passwordEncryptMode,
            string rsaSeparator,
            string macOverride,
            string tag)
        {
            var result = new LoginResult();
            var password = account.Password ?? "";
            var macSource = string.IsNullOrWhiteSpace(account.Mac) ? macOverride : account.Mac;
            var mac = RuijieRsa.NormalizeMac(macSource ?? "");

            var encrypt = ResolvePasswordEncrypt(page, passwordEncryptMode, out var encryptReason);

            string passwordValue = password;
            if (encrypt)
            {
                if (page == null || !page.HasRsaKey)
                {
                    _log.Warn("需要 RSA 加密但登录页没有公钥，回退明文登录（" + encryptReason + "）", tag);
                    encrypt = false;
                }
                else
                {
                    try
                    {
                        passwordValue = RuijieRsa.EncryptPasswordWithMac(
                            page.PublicKeyExponent, page.PublicKeyModulus, password, mac, rsaSeparator);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("RSA 加密失败，回退明文登录", ex, tag);
                        encrypt = false;
                    }
                }
            }

            var body = "userId=" + DoubleEncode(account.UserId)
                       + "&password=" + DoubleEncode(passwordValue)
                       + "&service=" + DoubleEncode(account.Service ?? "")
                       + "&queryString=" + DoubleEncode(queryString)
                       + "&operatorPwd="
                       + "&operatorUserId="
                       + "&validcode=" + DoubleEncode(validCode ?? "")
                       + "&passwordEncrypt=" + (encrypt ? "true" : "false");

            _log.Info("登录请求：userId=" + account.UserId + "，passwordEncrypt=" + (encrypt ? "true" : "false")
                      + (encrypt ? "（" + encryptReason + "）" : ""), tag);

            var response = await PostInterfaceAsync("login", body, tag).ConfigureAwait(false);
            result.RawText = response.Text;

            if (!response.Ok)
            {
                result.Message = response.Error;
                return result;
            }

            var json = JsonHelper.ParseObject(response.Text);
            if (json != null)
            {
                result.Success = string.Equals(JsonHelper.GetString(json, "result", ""), "success", StringComparison.OrdinalIgnoreCase);
                result.UserIndex = JsonHelper.GetString(json, "userIndex", "");
                result.KeepaliveInterval = JsonHelper.GetInt(json, "keepaliveInterval", 120);
                result.Message = JsonHelper.GetString(json, "message", "");
                result.ValidCodeUrl = JsonHelper.GetString(json, "validCodeUrl", "");
            }
            else
            {
                // 兜底：非 JSON 响应
                var extracted = JsonHelper.ExtractJsonString(response.Text, "result");
                result.Success = string.Equals(extracted, "success", StringComparison.OrdinalIgnoreCase);
                result.UserIndex = JsonHelper.ExtractJsonString(response.Text, "userIndex");
                result.Message = JsonHelper.ExtractJsonString(response.Text, "message");
                if (!result.Success && string.IsNullOrEmpty(result.Message))
                {
                    result.Message = "无法解析登录响应：" + Truncate(response.Text, 200);
                }
            }

            if (result.KeepaliveInterval <= 0) result.KeepaliveInterval = 120;

            if (!result.Success)
            {
                result.Message = FriendlyLoginError(result.Message);
                _log.Warn("登录失败：" + result.Message, tag);
            }
            else
            {
                _log.Info("登录成功：userIndex=" + Truncate(result.UserIndex, 16)
                          + "，keepaliveInterval=" + result.KeepaliveInterval + "s", tag);
            }
            return result;
        }

        private static bool ResolvePasswordEncrypt(LoginPageInfo page, string mode, out string reason)
        {
            reason = "";
            if (string.Equals(mode, "plain", StringComparison.OrdinalIgnoreCase))
            {
                reason = "设置里强制明文";
                return false;
            }
            if (string.Equals(mode, "rsa", StringComparison.OrdinalIgnoreCase))
            {
                reason = "设置里强制 RSA";
                return true;
            }

            var flag = (page?.PasswordEncrypt ?? "").Trim();
            if (string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
            {
                reason = "登录页 passwordEncrypt=true";
                return true;
            }
            if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase))
            {
                reason = "登录页 passwordEncrypt=false";
                return false;
            }
            reason = "登录页未声明，按明文处理";
            return false;
        }

        private static string FriendlyLoginError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "登录失败（无返回信息）";
            if (message.IndexOf("设备未注册", StringComparison.Ordinal) >= 0)
            {
                return message + "（queryString 无效或过期，请重新探测）";
            }
            if (message.IndexOf("密码", StringComparison.Ordinal) >= 0 && message.IndexOf("错误", StringComparison.Ordinal) >= 0)
            {
                return message + "（也可能是加密模式不对，可在设置里切换明文/RSA）";
            }
            return message;
        }

        public async Task<ApiResult> KeepaliveAsync(string userIndex, string tag)
        {
            var response = await PostInterfaceAsync("keepalive",
                "userIndex=" + FormEncode(userIndex), tag).ConfigureAwait(false);
            return ToApiResult(response, "keepalive");
        }

        public async Task<ApiResult> LogoutAsync(string userIndex, string tag)
        {
            var response = await PostInterfaceAsync("logout",
                "userIndex=" + FormEncode(userIndex), tag).ConfigureAwait(false);
            return ToApiResult(response, "logout");
        }

        public async Task<ApiResult> LogoutAllAsync(string userId, string password, string tag)
        {
            var response = await PostInterfaceAsync("logoutByUserIdAndPass",
                "userId=" + FormEncode(userId) + "&pass=" + FormEncode(password), tag).ConfigureAwait(false);
            return ToApiResult(response, "logoutByUserIdAndPass");
        }

        public async Task<ApiResult> RegisterMacAsync(string mac, string userIndex, string tag)
        {
            var response = await PostInterfaceAsync("registerMac",
                "mac=" + FormEncode(RuijieRsa.NormalizeMac(mac)) + "&userIndex=" + FormEncode(userIndex), tag).ConfigureAwait(false);
            return ToApiResult(response, "registerMac");
        }

        public async Task<ApiResult> CancelMacAsync(string userId, string mac, string tag)
        {
            var response = await PostInterfaceAsync("cancelMacWithUserNameAndMac",
                "userId=" + FormEncode(userId) + "&usermac=" + FormEncode(RuijieRsa.NormalizeMac(mac)), tag).ConfigureAwait(false);
            return ToApiResult(response, "cancelMacWithUserNameAndMac");
        }

        public async Task<OnlineUserInfoResult> GetOnlineUserInfoAsync(string userIndex, string tag)
        {
            var response = await PostInterfaceAsync("getOnlineUserInfo",
                "userIndex=" + FormEncode(userIndex), tag).ConfigureAwait(false);

            var result = new OnlineUserInfoResult { Success = response.Ok, RawText = response.Text, Message = response.Error };
            var json = JsonHelper.ParseObject(response.Text);
            if (json == null)
            {
                if (response.Ok) result.Message = "响应不是 JSON：" + Truncate(response.Text, 200);
                return result;
            }

            result.Success = string.Equals(JsonHelper.GetString(json, "result", "success"), "success", StringComparison.OrdinalIgnoreCase);
            if (!result.Success) result.Message = JsonHelper.GetString(json, "message", "查询失败");
            result.UserId = JsonHelper.GetString(json, "userId", "");
            result.UserIp = JsonHelper.GetString(json, "userIp", "");
            result.UserMac = JsonHelper.GetString(json, "userMac", "");
            result.UserUrl = JsonHelper.GetString(json, "userUrl", "");
            return result;
        }

        // ---------------------------------------------------------------- 底层

        private Task<HttpResult> PostInterfaceAsync(string method, string body, string tag)
        {
            var url = BaseUrl + "/eportal/InterFace.do?method=" + method;
            _log.Debug("POST " + url + " | " + LogService.Redact(body), tag);
            return _http.SendAsync(url, "POST", body,
                "application/x-www-form-urlencoded; charset=UTF-8",
                BaseUrl + "/eportal/index.jsp", 15000, false);
        }

        private ApiResult ToApiResult(HttpResult response, string method)
        {
            var result = new ApiResult { Success = false, RawText = response.Text, Message = response.Error };
            if (!response.Ok) return result;

            var json = JsonHelper.ParseObject(response.Text);
            if (json == null)
            {
                var extracted = JsonHelper.ExtractJsonString(response.Text, "result");
                result.Success = string.Equals(extracted, "success", StringComparison.OrdinalIgnoreCase);
                if (!result.Success) result.Message = "响应不是 JSON：" + Truncate(response.Text, 160);
                return result;
            }

            result.Success = string.Equals(JsonHelper.GetString(json, "result", ""), "success", StringComparison.OrdinalIgnoreCase);
            result.Message = JsonHelper.GetString(json, "message", "");
            if (!result.Success && string.IsNullOrEmpty(result.Message))
            {
                result.Message = method + " 返回失败";
            }
            return result;
        }

        /// <summary>与 JS encodeURIComponent 完全一致（保留 A-Za-z0-9-_.!~*'()）。</summary>
        public static string EncodeUriComponent(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var builder = new StringBuilder(value.Length * 3);
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.' || c == '!' || c == '~' || c == '*' || c == '\'' || c == '(' || c == ')')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('%').Append(b.ToString("X2"));
                }
            }
            return builder.ToString();
        }

        /// <summary>门户 JS 里普遍是 encodeURIComponent(encodeURIComponent(x))。</summary>
        public static string DoubleEncode(string value)
        {
            return EncodeUriComponent(EncodeUriComponent(value ?? ""));
        }

        /// <summary>单次表单编码（用于按文档只编码一次的方法）。</summary>
        public static string FormEncode(string value)
        {
            return Uri.EscapeDataString(value ?? "");
        }

        public static string NormalizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "222.201.54.55";
            var value = host.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
            else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(8);
            value = value.TrimEnd('/');
            var slash = value.IndexOf('/');
            if (slash > 0) value = value.Substring(0, slash);
            return value;
        }

        public static string ToBaseUrl(string host)
        {
            var normalized = NormalizeHost(host);
            return "http://" + normalized;
        }

        private static string GetInputValue(string html, string name)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var tags = Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase);
            foreach (Match tag in tags)
            {
                var nameMatch = Regex.Match(tag.Value, "\\bname\\s*=\\s*([\"']?)([^\\s\"'>]+)\\1", RegexOptions.IgnoreCase);
                if (!nameMatch.Success) continue;
                if (!string.Equals(nameMatch.Groups[2].Value, name, StringComparison.OrdinalIgnoreCase)) continue;

                var valueMatch = Regex.Match(tag.Value, "\\bvalue\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase);
                if (valueMatch.Success) return valueMatch.Groups[2].Value;

                var plainValue = Regex.Match(tag.Value, "\\bvalue\\s*=\\s*([^\\s\"'>]+)", RegexOptions.IgnoreCase);
                if (plainValue.Success) return plainValue.Groups[1].Value;
                return "";
            }
            return "";
        }

        private static string Truncate(string text, int length)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= length ? text : text.Substring(0, length) + "...";
        }
    }
}
