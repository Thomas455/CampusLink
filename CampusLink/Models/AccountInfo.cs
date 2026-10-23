using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CampusLink.Models
{
    /// <summary>门户返回的套餐/余额等条目（ballInfo 解析出来）。</summary>
    public class AccountBallInfo
    {
        public string DisplayName { get; set; } = "";
        public string Value { get; set; } = "";
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>信息页里的一行（标签 + 值）。</summary>
    public class AccountInfoField
    {
        public AccountInfoField(string label, string value)
        {
            Label = label;
            Value = value ?? "";
        }

        public string Label { get; }

        public string Value { get; }
    }

    /// <summary>信息页里的一个分组。</summary>
    public class AccountInfoSection
    {
        public AccountInfoSection(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public ObservableCollection<AccountInfoField> Fields { get; } = new ObservableCollection<AccountInfoField>();
    }

    /// <summary>getOnlineUserInfo 的完整快照，供信息页结构化展示。</summary>
    public class AccountInfoSnapshot
    {
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string UserIp { get; set; } = "";
        public string UserMac { get; set; } = "";
        public string UserGroup { get; set; } = "";
        public string LoginType { get; set; } = "";
        public string Service { get; set; } = "";
        public string UserUrl { get; set; } = "";
        public string PortalIp { get; set; } = "";
        public string Announcement { get; set; } = "";
        public string WelcomeTip { get; set; } = "";
        public string RawJson { get; set; } = "";
        public bool Fetched { get; set; }
        public string Message { get; set; } = "";
        public List<AccountBallInfo> Balls { get; set; } = new List<AccountBallInfo>();
    }
}
