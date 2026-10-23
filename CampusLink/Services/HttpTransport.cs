using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace CampusLink.Services
{
    public class HttpResult
    {
        public int StatusCode { get; set; }
        public string RawLocation { get; set; }
        public string ContentType { get; set; }
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string Url { get; set; } = "";
        public string Error { get; set; }
        public bool Ok => string.IsNullOrEmpty(Error);

        private string _text;

        /// <summary>响应体文本：优先严格 UTF-8，失败退回 GB18030（门户是 GBK 页面）。</summary>
        public string Text
        {
            get
            {
                if (_text != null) return _text;
                _text = Body != null && Body.Length > 0 ? Decode(Body) : "";
                return _text;
            }
        }

        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try
            {
                var strictUtf8 = new UTF8Encoding(false, true);
                return strictUtf8.GetString(bytes);
            }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(bytes); }
                catch { return Encoding.UTF8.GetString(bytes); }
            }
        }
    }

    /// <summary>
    /// 基于 HttpWebRequest 的极简 HTTP 通道：
    /// - 自带 CookieContainer（eportal 依赖 JSESSIONID）
    /// - 可关闭自动跳转，从而读到原始 302 Location（queryString 的来源）
    /// - 支持绑定源 IP（多网卡场景）
    /// - 可保留原始 Location 字符串（HttpClient 会把它归一化成 Uri）
    /// </summary>
    public class HttpTransport
    {
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly IPAddress _sourceIp;
        private readonly bool _useSystemProxy;

        public HttpTransport(string sourceIp = null, string userAgent = null, bool useSystemProxy = false)
        {
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? BrowserUserAgent : userAgent;
            _useSystemProxy = useSystemProxy;
            if (!string.IsNullOrWhiteSpace(sourceIp) && IPAddress.TryParse(sourceIp.Trim(), out var ip))
            {
                _sourceIp = ip;
            }
        }

        public string UserAgent { get; }

        public CookieContainer Cookies => _cookies;

        public async Task<HttpResult> SendAsync(
            string url,
            string method = "GET",
            string body = null,
            string contentType = null,
            string referer = null,
            int timeoutMs = 15000,
            bool allowRedirect = false,
            bool useCookies = true)
        {
            var result = new HttpResult { Url = url };
            HttpWebRequest request = null;

            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.AllowAutoRedirect = allowRedirect;
                request.UserAgent = UserAgent;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.KeepAlive = false;
                // 校园网门户是局域网，默认直连；访问 GitHub 时走系统代理（否则有代理的环境会失败）
                if (!_useSystemProxy) request.Proxy = null;
                request.ServicePoint.Expect100Continue = false;
                if (useCookies) request.CookieContainer = _cookies;
                if (!string.IsNullOrEmpty(referer)) request.Referer = referer;
                if (_sourceIp != null)
                {
                    request.ServicePoint.BindIPEndPointDelegate = (servicePoint, remoteEndPoint, retryCount) =>
                        new IPEndPoint(_sourceIp, 0);
                }

                if (body != null)
                {
                    request.ContentType = contentType ?? "application/x-www-form-urlencoded; charset=UTF-8";
                    var payload = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = payload.Length;
                    using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    {
                        await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                    }
                }

                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    FillFromResponse(result, response);
                    using (var memory = new MemoryStream())
                    {
                        using (var stream = response.GetResponseStream())
                        {
                            if (stream != null) await stream.CopyToAsync(memory).ConfigureAwait(false);
                        }
                        result.Body = memory.ToArray();
                    }
                }
            }
            catch (WebException wex)
            {
                if (wex.Response is HttpWebResponse errorResponse)
                {
                    FillFromResponse(result, errorResponse);
                    try
                    {
                        using (var memory = new MemoryStream())
                        {
                            using (var stream = errorResponse.GetResponseStream())
                            {
                                stream?.CopyTo(memory);
                            }
                            result.Body = memory.ToArray();
                        }
                    }
                    catch { }
                    errorResponse.Dispose();
                }
                else
                {
                    result.Error = wex.Status == WebExceptionStatus.Timeout
                        ? "请求超时（" + timeoutMs + "ms）"
                        : "网络错误：" + wex.Message;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private static void FillFromResponse(HttpResult result, HttpWebResponse response)
        {
            result.StatusCode = (int)response.StatusCode;
            // 直接读原始头，HttpClient 的 Headers.Location 会做 URL 归一化
            result.RawLocation = response.Headers["Location"] ?? "";
            result.ContentType = response.ContentType ?? "";
        }

        public static string CombineUrl(string baseUrl, string relativeOrAbsolute)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolute)) return baseUrl;
            if (relativeOrAbsolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                relativeOrAbsolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return relativeOrAbsolute;
            }
            if (!baseUrl.EndsWith("/", StringComparison.Ordinal)) baseUrl += "/";
            return new Uri(new Uri(baseUrl), relativeOrAbsolute).ToString();
        }
    }
}
