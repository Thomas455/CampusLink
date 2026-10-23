using System;
using Newtonsoft.Json;

namespace CampusLink.Models
{
    /// <summary>
    /// 一个校园网账号的持久化配置。密码以 DPAPI（当前用户）加密存储，明文只保留在内存中。
    /// </summary>
    public class AccountProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>显示名称（备注名），留空时界面显示学号。</summary>
        public string DisplayName { get; set; } = "";

        public string UserId { get; set; } = "";

        /// <summary>DPAPI 加密后的 Base64 密码。</summary>
        public string PasswordCipher { get; set; } = "";

        /// <summary>明文密码，只在内存中使用，绝不序列化。</summary>
        [JsonIgnore]
        public string Password { get; set; } = "";

        /// <summary>绑定的网卡 Id（MAC 与源 IP 都来自这块网卡，不再让用户手填 MAC）。</summary>
        public string NicId { get; set; } = "";

        /// <summary>可选：服务名（getServices 返回的 serviceName）。</summary>
        public string Service { get; set; } = "";

        /// <summary>门户地址，如 222.201.54.55 或 http://222.201.54.55:8080。</summary>
        public string PortalHost { get; set; } = "222.201.54.55";

        /// <summary>程序启动且处于未认证状态时自动登录。</summary>
        public bool AutoLogin { get; set; } = true;

        /// <summary>检测到被拦截（掉线）时自动重新登录。</summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>登录成功后自动调用 registerMac 注册无感认证。</summary>
        public bool AutoRegisterMac { get; set; }

        public string Remark { get; set; } = "";

        [JsonIgnore]
        public string Title => string.IsNullOrWhiteSpace(DisplayName) ? UserId : DisplayName;

        public AccountProfile Clone()
        {
            return (AccountProfile)MemberwiseClone();
        }
    }
}
