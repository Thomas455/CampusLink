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
    /// </summary>
    public class SessionState
    {
        public LinkState State { get; set; } = LinkState.Unknown;

        public string UserIndex { get; set; } = "";

        public string QueryString { get; set; } = "";

        public string DetectedMac { get; set; } = "";

        public int KeepaliveInterval { get; set; } = 120;

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
                    case LinkState.Online: return "在线";
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
