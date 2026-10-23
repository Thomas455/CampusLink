using System;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json.Linq;

namespace CampusLink.Models
{
    public class ProbeResult
    {
        public bool Ok { get; set; }
        public bool NeedsAuth { get; set; }
        public bool Online { get; set; }
        public string QueryString { get; set; } = "";
        public string Mac { get; set; } = "";
        public string RedirectUrl { get; set; } = "";
        public string ProbeUrl { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public class LoginPageInfo
    {
        public string PasswordEncrypt { get; set; } = "";
        public string PublicKeyExponent { get; set; } = "";
        public string PublicKeyModulus { get; set; } = "";
        public bool HasCaptcha { get; set; }
        public string ValidCodeUrl { get; set; } = "";
        public string RawHtml { get; set; } = "";

        public bool HasRsaKey =>
            !string.IsNullOrWhiteSpace(PublicKeyExponent) && !string.IsNullOrWhiteSpace(PublicKeyModulus);
    }

    public class LoginResult
    {
        public bool Success { get; set; }
        public string UserIndex { get; set; } = "";
        public int KeepaliveInterval { get; set; } = 120;
        public string Message { get; set; } = "";
        public string ValidCodeUrl { get; set; } = "";
        public string RawText { get; set; } = "";
        public bool CaptchaRequired => !string.IsNullOrWhiteSpace(ValidCodeUrl);
    }

    public class ApiResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string RawText { get; set; } = "";

        public override string ToString()
        {
            return (Success ? "成功" : "失败") + (string.IsNullOrWhiteSpace(Message) ? "" : "：" + Message);
        }
    }

    public class OnlineUserInfoResult : ApiResult
    {
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string UserIp { get; set; } = "";
        public string UserMac { get; set; } = "";
        public string UserUrl { get; set; } = "";
        public string UserGroup { get; set; } = "";
        public string LoginType { get; set; } = "";
        public string Service { get; set; } = "";
        public string PortalIp { get; set; } = "";
        public string Announcement { get; set; } = "";
        public string WelcomeTip { get; set; } = "";
        public JObject Raw { get; set; }
        public List<AccountBallInfo> Balls { get; set; } = new List<AccountBallInfo>();
    }

    /// <summary>
    /// 已登录（MAC 免认证）状态下，从成功页重定向里找到的会话信息。
    /// userIndex 是十六进制串，解码后形如「会话_IP_学号」。
    /// </summary>
    public class OnlineSessionDiscovery : ApiResult
    {
        public string UserIndex { get; set; } = "";
        public string DecodedUserId { get; set; } = "";
        public string DecodedIp { get; set; } = "";

        public bool Found => !string.IsNullOrWhiteSpace(UserIndex);
    }

    public class ServiceOption
    {
        public string ServiceName { get; set; } = "";
        public string ShowName { get; set; } = "";

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(ShowName) ? ServiceName : ShowName + " (" + ServiceName + ")";
        }
    }

    public class ServicesResult : ApiResult
    {
        public string DefaultService { get; set; } = "";
        public List<ServiceOption> Services { get; set; } = new List<ServiceOption>();
    }

    public class PageInfoResult : ApiResult
    {
        public bool IsAutoLogin { get; set; }
        public string Announcement { get; set; } = "";
        public string SystemNotice { get; set; } = "";
        public string ValidCodeUrl { get; set; } = "";
        public JObject Raw { get; set; }
    }
}
