using System;

namespace CampusLink.Models
{
    public enum LinkState
    {
        Unknown,
        Offline,
        Probing,
        NeedAuth,
        LoggingIn,
        Online,
        Error
    }

    /// <summary>
    /// 单个账号的运行时会话状态（不持久化）。
    /// Verified 区分「真的用账号登录过」和「网络本来就通（MAC 免认证）」。
    /// </summary>
    public class SessionState
    {
        public LinkState State { get; set; } = LinkState.Unknown;

        public string UserIndex { get; set; } = "";

        public string QueryString { get; set; } = "";

        public string DetectedMac { get; set; } = "";

        public int KeepaliveInterval { get; set; } = 120;

        /// <summary>true = 通过 login 接口拿到 userIndex；false = 只是探测到网络已通。</summary>
        public bool Verified { get; set; }

        public string LastMessage { get; set; } = "";

        public DateTime? LastCheckTime { get; set; }

        public DateTime? LastLoginTime { get; set; }

        public bool IsOnline => State == LinkState.Online;

        public bool IsBusy => State == LinkState.Probing || State == LinkState.LoggingIn;

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case LinkState.Online: return Verified ? "在线" : "在线·免认证";
                    case LinkState.Offline: return "离线";
                    case LinkState.Probing: return "探测中";
                    case LinkState.NeedAuth: return "待认证";
                    case LinkState.LoggingIn: return "登录中";
                    case LinkState.Error: return "异常";
                    default: return "未知";
                }
            }
        }
    }
}
