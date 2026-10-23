using System.Collections.Generic;
using Newtonsoft.Json;

namespace CampusLink.Models
{
    public class AppSettings
    {
        /// <summary>新建账号时默认填写的门户地址。</summary>
        public string DefaultPortalHost { get; set; } = "222.201.54.55";

        /// <summary>探测地址（每行一个），用于判断当前是否被 AC 拦截以及抓取 queryString。</summary>
        public string ProbeUrls { get; set; } =
            "http://222.201.54.55/\r\nhttp://1.1.1.1/\r\nhttp://www.msftconnecttest.com/redirect";

        /// <summary>密码加密模式：auto（按登录页字段）/ plain（明文）/ rsa（强制加密）。</summary>
        public string PasswordEncryptMode { get; set; } = "auto";

        /// <summary>RSA 加密前的拼接分隔符，资料里出现过 ">" 与 ">>" 两种写法。</summary>
        public string RsaSeparator { get; set; } = ">";

        public bool AutoReconnectEnabled { get; set; } = true;

        public int ReconnectCheckSeconds { get; set; } = 30;

        public bool KeepaliveEnabled { get; set; } = true;

        public bool AutoLoginOnStartup { get; set; } = true;

        public bool StartWithWindows { get; set; }

        public bool CloseToTray { get; set; } = true;

        public bool MinimizeToTray { get; set; } = true;

        public bool ShowBalloonNotifications { get; set; } = true;

        /// <summary>自助服务系统地址。</summary>
        public string SelfServiceBaseUrl { get; set; } = "http://222.201.54.23:8080/selfservice";

        /// <summary>默认密码模板，{uid} 会被替换为学号。</summary>
        public string DefaultPasswordTemplate { get; set; } = "{uid}@Gzhu";

        public List<string> ProbeUrlList()
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(ProbeUrls)) return list;
            foreach (var line in ProbeUrls.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public AppSettings Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            return JsonConvert.DeserializeObject<AppSettings>(json);
        }
    }
}
