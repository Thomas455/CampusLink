using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CampusLink.Infrastructure;
using CampusLink.Models;
using Newtonsoft.Json.Linq;

namespace CampusLink.Services
{
    /// <summary>
    /// GitHub Releases 自更新：
    /// 1) 先查 GitHub API（能拿到发布说明和附件）；
    /// 2) API 被限流/失败时退回 releases/latest 的 302 跳转（不受 API 限流影响），
    ///    并按约定文件名 CampusLink.exe / CampusLink.zip 推断下载地址。
    /// </summary>
    public class UpdateService
    {
        public const string DefaultRepository = "Thomas455/CampusLink";

        private const string UserAgent = "CampusLink-Updater";

        private readonly LogService _log;
        private readonly HttpTransport _http = new HttpTransport(null, UserAgent);

        public UpdateService(LogService log, string repository = null, string apiUrl = null, string webBaseUrl = null,
            string currentVersion = null)
        {
            _log = log;
            Repository = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim().Trim('/');
            WebBaseUrl = string.IsNullOrWhiteSpace(webBaseUrl) ? "https://github.com" : webBaseUrl.TrimEnd('/');
            ApiUrl = string.IsNullOrWhiteSpace(apiUrl)
                ? "https://api.github.com/repos/" + Repository + "/releases/latest"
                : apiUrl;
            CurrentVersion = string.IsNullOrWhiteSpace(currentVersion) ? DetectCurrentVersion() : currentVersion.Trim();
        }

        public string Repository { get; }

        public string WebBaseUrl { get; }

        public string ApiUrl { get; }

        public string CurrentVersion { get; }

        public string RepoUrl => WebBaseUrl + "/" + Repository;

        public string ReleasesUrl => RepoUrl + "/releases";

        /// <summary>当前程序版本（三段式）。</summary>
        public static string DetectCurrentVersion()
        {
            try
            {
                var version = Assembly.GetEntryAssembly()?.GetName().Version;
                if (version == null) return "0.0.0";
                return version.Major + "." + version.Minor + "." + Math.Max(version.Build, 0);
            }
            catch
            {
                return "0.0.0";
            }
        }

        // ---------------------------------------------------------------- 检查

        public async Task<UpdateInfo> CheckAsync()
        {
            var info = new UpdateInfo
            {
                CurrentVersion = CurrentVersion,
                HtmlUrl = ReleasesUrl
            };
            try
            {
                var response = await _http.SendAsync(ApiUrl, "GET", null, null, null, 12000, false).ConfigureAwait(false);
                if (response.StatusCode == 200)
                {
                    ParseApi(info, response.Text);
                    info.Checked = true;
                    return info;
                }

                if (response.StatusCode == 404)
                {
                    info.Checked = true;
                    info.Message = "仓库还没有发布版本";
                    return info;
                }

                info.Message = "GitHub API 返回 " + response.StatusCode + "，改用发布页检测";
                _log?.Warn(info.Message);
            }
            catch (Exception ex)
            {
                info.Message = "GitHub API 不可用：" + ex.Message;
                _log?.Warn(info.Message);
            }

            try
            {
                await CheckViaRedirectAsync(info).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                info.Checked = true;
                info.Message = "检查更新失败：" + ex.Message;
                _log?.Warn(info.Message);
            }

            return info;
        }

        private void ParseApi(UpdateInfo info, string json)
        {
            var root = JsonHelper.ParseObject(json);
            if (root == null)
            {
                info.Checked = true;
                info.Message = "更新信息解析失败";
                return;
            }

            info.TagName = JsonHelper.GetString(root, "tag_name", "");
            info.ReleaseName = JsonHelper.GetString(root, "name", "");
            info.ReleaseNotes = JsonHelper.GetString(root, "body", "");
            info.HtmlUrl = JsonHelper.GetString(root, "html_url", ReleasesUrl);

            var assets = root["assets"] as JArray;
            info.Asset = PickAsset(assets);

            var latest = ParseVersionTag(info.TagName);
            info.LatestVersion = latest == null ? info.TagName : Format(latest);
            info.HasUpdate = latest != null && latest > ParseVersionTag(CurrentVersion);
            info.Checked = true;
            if (!info.HasUpdate) info.Message = "已是最新版本";
            _log?.Info("检查更新：当前 v" + CurrentVersion + "，最新 " + info.TagName + "，有更新=" + info.HasUpdate);
        }

        private static UpdateAsset PickAsset(JArray assets)
        {
            if (assets == null) return null;
            UpdateAsset zip = null;
            foreach (var token in assets)
            {
                var name = JsonHelper.GetString(token as JObject, "name", "");
                var url = JsonHelper.GetString(token as JObject, "browser_download_url", "");
                if (string.IsNullOrWhiteSpace(url)) continue;
                var asset = new UpdateAsset
                {
                    Name = name,
                    Url = url,
                    Size = JsonHelper.GetInt(token as JObject, "size", 0)
                };
                var lower = name.ToLowerInvariant();
                if (lower.EndsWith(".exe")) return asset;
                if (lower.EndsWith(".zip") && zip == null) zip = asset;
            }
            return zip;
        }

        private async Task CheckViaRedirectAsync(UpdateInfo info)
        {
            var latestUrl = RepoUrl + "/releases/latest";
            var response = await _http.SendAsync(latestUrl, "GET", null, null, null, 12000, false).ConfigureAwait(false);
            var location = response.RawLocation ?? "";
            if (string.IsNullOrWhiteSpace(location))
            {
                info.Checked = true;
                info.Message = "发布页没有返回跳转，可能还没有发布版本";
                return;
            }

            info.HtmlUrl = HttpTransport.CombineUrl(location, "");
            var tag = info.HtmlUrl.TrimEnd('/');
            var slash = tag.LastIndexOf('/');
            if (slash >= 0) tag = tag.Substring(slash + 1);
            tag = Uri.UnescapeDataString(tag);
            info.TagName = tag;

            var latest = ParseVersionTag(tag);
            info.LatestVersion = latest == null ? tag : Format(latest);
            info.HasUpdate = latest != null && latest > ParseVersionTag(CurrentVersion);
            info.Checked = true;
            info.Message = info.HasUpdate ? "" : "已是最新版本";

            if (info.HasUpdate && info.Asset == null)
            {
                // 按发布约定推断附件名
                info.Asset = new UpdateAsset
                {
                    Name = "CampusLink.exe",
                    Url = RepoUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/CampusLink.exe",
                    Size = 0
                };
            }

            _log?.Info("检查更新（发布页）：最新 " + tag + "，有更新=" + info.HasUpdate);
        }

        public static Version ParseVersionTag(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var value = text.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            var dash = value.IndexOf('-');
            if (dash > 0) value = value.Substring(0, dash);
            var plus = value.IndexOf('+');
            if (plus > 0) value = value.Substring(0, plus);

            Version version;
            if (!Version.TryParse(value, out version)) return null;
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }

        private static string Format(Version version) => version.Major + "." + version.Minor + "." + version.Build;

        // ---------------------------------------------------------------- 下载

        /// <summary>下载更新包并返回本地路径（zip 会在这里解出 exe）。</summary>
        public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken token)
        {
            if (info?.Asset == null || string.IsNullOrWhiteSpace(info.Asset.Url))
            {
                throw new InvalidOperationException("没有可下载的更新附件");
            }

            var urls = new List<string> { info.Asset.Url };
            if (info.Asset.Url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(info.Asset.Url.Substring(0, info.Asset.Url.Length - 4) + ".zip");
            }

            Exception last = null;
            foreach (var url in urls)
            {
                try
                {
                    var extension = Path.GetExtension(url);
                    if (string.IsNullOrEmpty(extension)) extension = ".exe";
                    var target = Path.Combine(Path.GetTempPath(),
                        "CampusLink-update-" + info.LatestVersion + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + extension);

                    _log?.Info("开始下载更新：" + url);
                    await DownloadFileAsync(url, target, progress, token).ConfigureAwait(false);
                    _log?.Info("下载完成：" + target + "（" + new FileInfo(target).Length + " 字节）");
                    return PreparePackage(target);
                }
                catch (Exception ex)
                {
                    last = ex;
                    _log?.Warn("下载失败，尝试备用地址：" + url + " -> " + ex.Message);
                }
            }

            throw last ?? new InvalidOperationException("下载失败");
        }

        private static async Task DownloadFileAsync(string url, string targetPath, IProgress<double> progress, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = true;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.Proxy = null;

            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var stream = response.GetResponseStream())
            using (var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (stream == null) throw new IOException("响应流为空");
                var total = response.ContentLength;
                var buffer = new byte[64 * 1024];
                long received = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(100.0, received * 100.0 / total));
                    }
                }
                progress?.Report(100d);
                if (total > 0 && received < total)
                {
                    throw new IOException("下载不完整（" + received + "/" + total + "）");
                }
            }
        }

        private string PreparePackage(string downloadedPath)
        {
            if (!downloadedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return downloadedPath;

            var extractDir = Path.Combine(Path.GetTempPath(),
                "CampusLink-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(downloadedPath, extractDir);

            foreach (var file in Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories))
            {
                return file;
            }
            throw new FileNotFoundException("更新包里没有找到 CampusLink.exe");
        }

        // ---------------------------------------------------------------- 安装

        /// <summary>写一个带重试的更新脚本：等本进程退出 → 覆盖 exe → 重新启动。</summary>
        public void ApplyAndRestart(string newExePath, LogService log)
        {
            var currentExe = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                throw new InvalidOperationException("无法定位当前程序的 exe 路径");
            }

            var script = Path.Combine(Path.GetTempPath(), "campuslink-update-" + Guid.NewGuid().ToString("N") + ".cmd");
            var content = new StringBuilder()
                .AppendLine("@echo off")
                .AppendLine("ping -n 3 127.0.0.1 >nul")
                .AppendLine("for /l %%i in (1,1,30) do (")
                .AppendLine("  copy /y \"" + newExePath + "\" \"" + currentExe + "\" >nul 2>&1 && goto done")
                .AppendLine("  ping -n 2 127.0.0.1 >nul")
                .AppendLine(")")
                .AppendLine(":done")
                .AppendLine("start \"\" \"" + currentExe + "\"")
                .AppendLine("del \"%~f0\"")
                .ToString();
            File.WriteAllText(script, content, Encoding.Default);

            log?.Info("开始替换程序：" + newExePath + " -> " + currentExe);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
        }
    }
}
