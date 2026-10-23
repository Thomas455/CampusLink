using System;
using System.Collections.Generic;

namespace CampusLink.Models
{
    public class UpdateAsset
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public long Size { get; set; }
    }

    /// <summary>下载好的更新包：可能是完整目录（zip）或单个 exe。</summary>
    public class UpdatePackage
    {
        public string ExePath { get; set; } = "";

        /// <summary>待替换文件的来源目录（zip 解包目录；单 exe 时为 exe 所在目录）。</summary>
        public string SourceDirectory { get; set; } = "";

        /// <summary>true = zip 整包，需要整目录覆盖；false = 只有 exe。</summary>
        public bool IsFullPackage { get; set; }
    }

    /// <summary>一次更新检查的结果。</summary>
    public class UpdateInfo
    {
        public bool Checked { get; set; }
        public bool HasUpdate { get; set; }

        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string TagName { get; set; } = "";
        public string ReleaseName { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string SourceName { get; set; } = "";
        public UpdateAsset Asset { get; set; }

        /// <summary>其它更新源上同版本/更新版本的附件，主地址失败时依次尝试。</summary>
        public List<UpdateAsset> AlternateAssets { get; set; } = new List<UpdateAsset>();

        public string Message { get; set; } = "";

        public string VersionPair => "v" + (string.IsNullOrEmpty(CurrentVersion) ? "?" : CurrentVersion)
                                     + "  →  v" + (string.IsNullOrEmpty(LatestVersion) ? "?" : LatestVersion);
    }
}
