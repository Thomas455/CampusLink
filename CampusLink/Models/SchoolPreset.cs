using System;
using System.IO;
using Newtonsoft.Json;

namespace CampusLink.Models
{
    /// <summary>
    /// 学校预设：不同学校的门户地址/自助服务地址不同，一个预设就是配置文件里的一个 json。
    /// </summary>
    public class SchoolPreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>学校名称，例如「广州大学·大学城校区」。</summary>
        public string Name { get; set; } = "";

        /// <summary>预设作者。</summary>
        public string Author { get; set; } = "";

        /// <summary>门户/认证地址（例如 222.201.54.55）。</summary>
        public string PortalHost { get; set; } = "";

        /// <summary>自助服务地址。</summary>
        public string SelfServiceUrl { get; set; } = "";

        /// <summary>默认密码模板，{uid} 会替换成学号。</summary>
        public string DefaultPasswordTemplate { get; set; } = "{uid}@Gzhu";

        /// <summary>是否内置预设（可删除，下次启动会自动恢复）。</summary>
        public bool BuiltIn { get; set; }

        [JsonIgnore]
        public string FilePath { get; set; }

        [JsonIgnore]
        public string DisplayName => Name + (string.IsNullOrWhiteSpace(Author) ? "" : "  ·  " + Author);

        public SchoolPreset Clone()
        {
            return (SchoolPreset)MemberwiseClone();
        }

        public string BuildDefaultPassword(string userId)
        {
            var template = string.IsNullOrWhiteSpace(DefaultPasswordTemplate)
                ? "{uid}"
                : DefaultPasswordTemplate;
            return template.Replace("{uid}", userId ?? "");
        }
    }
}
