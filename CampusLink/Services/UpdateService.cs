using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CampusLink.Infrastructure;
using CampusLink.Models;
using Newtonsoft.Json.Linq;

namespace CampusLink.Services
{
    /// <summary>一个更新源（GitCode / GitHub），按配置顺序依次检查。</summary>
    public class UpdateSource
    {
        public string Name { get; set; } = "GitHub";

        /// <summary>owner/repo。</summary>
        public string Repository { get; set; } = "";

        /// <summary>网页基地址，例如 https://github.com。</summary>
        public string WebBase { get; set; } = "https://github.com";

        /// <summary>API 地址：GitCode 用 releases 列表，GitHub 用 releases/latest。</summary>
        public string ApiUrl { get; set; } = "";

        /// <summary>true = API 返回数组（GitCode / Gitee 风格）；false = 返回单个对象（GitHub latest）。</summary>
        public bool ApiReturnsArray { get; set; }

        /// <summary>是否支持 releases/latest 的 302 兜底（GitHub）。</summary>
        public bool SupportsLatestRedirect { get; set; }

        public string RepoUrl => WebBase.TrimEnd('/') + "/" + Repository;

        public string ReleasesUrl => RepoUrl + "/releases";

        public string TagsInfoUrl => RepoUrl + ".git/info/refs?service=git-upload-pack";
    }

    /// <summary>
    /// 多源自更新：默认先 GitCode、再 GitHub。
    /// 每个源：API →（GitHub）releases/latest 302 → git tags 兜底；
    /// 所有源里取版本最高的一个，同版本时优先排在前面的源（GitCode），并把其它源的附件作为下载备选。
    /// </summary>
    public class UpdateService
    {
        public const string DefaultRepository = "Thomas455/CampusLink";

        private const string UserAgent = "CampusLink-Updater";

        private readonly LogService _log;
        private readonly HttpTransport _http;
        private readonly List<UpdateSource> _sources;
        private readonly bool _useSystemProxy;

        public UpdateService(LogService log, IEnumerable<UpdateSource> sources, string currentVersion = null,
            bool useSystemProxy = false)
        {
            _log = log;
            _useSystemProxy = useSystemProxy;
            _http = new HttpTransport(null, UserAgent, useSystemProxy);
            _sources = (sources ?? Enumerable.Empty<UpdateSource>()).Where(s => s != null).ToList();
            if (_sources.Count == 0) _sources.AddRange(CreateDefaultSources());
            CurrentVersion = string.IsNullOrWhiteSpace(currentVersion) ? DetectCurrentVersion() : currentVersion.Trim();
        }

        /// <summary>兼容单源构造（测试/镜像用），按 GitHub 行为处理（支持 releases/latest 跳转）。</summary>
        public UpdateService(LogService log, string repository = null, string apiUrl = null, string webBaseUrl = null,
            string currentVersion = null, bool useSystemProxy = false)
            : this(log, new[]
            {
                CreateSource("GitHub", string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository,
                    string.IsNullOrWhiteSpace(webBaseUrl) ? "https://github.com" : webBaseUrl,
                    string.IsNullOrWhiteSpace(apiUrl) ? null : apiUrl)
            }, currentVersion, useSystemProxy)
        {
            _sources[0].SupportsLatestRedirect = true;
        }

        public static List<UpdateSource> CreateDefaultSources()
        {
            var list = new List<UpdateSource>
            {
                CreateSource("GitCode", DefaultRepository, "https://gitcode.com",
                    "https://api.gitcode.com/api/v5/repos/" + DefaultRepository + "/releases"),
                CreateSource("GitHub", DefaultRepository, "https://github.com",
                    "https://api.github.com/repos/" + DefaultRepository + "/releases/latest")
            };
            list[0].ApiReturnsArray = true;
            list[1].SupportsLatestRedirect = true;
            return list;
        }

        public static UpdateSource CreateSource(string name, string repository, string webBase, string apiUrl)
        {
            var repo = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim().Trim('/');
            return new UpdateSource
            {
                Name = name,
                Repository = repo,
                WebBase = string.IsNullOrWhiteSpace(webBase) ? "https://github.com" : webBase.TrimEnd('/'),
                ApiUrl = string.IsNullOrWhiteSpace(apiUrl)
                    ? "https://api.github.com/repos/" + repo + "/releases/latest"
                    : apiUrl
            };
        }

        public IReadOnlyList<UpdateSource> Sources => _sources;

        public string CurrentVersion { get; }

        public string RepoUrl => _sources[0].RepoUrl;

        public string ReleasesUrl => _sources[0].ReleasesUrl;

        public string ApiUrl => _sources[0].ApiUrl;

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
            var infos = new List<UpdateInfo>();
            foreach (var source in _sources)
            {
                infos.Add(await CheckSourceAsync(source).ConfigureAwait(false));
            }

            var candidates = infos
                .Where(i => i.HasUpdate)
                .OrderByDescending(i => ParseVersionTag(i.LatestVersion) ?? new Version(0, 0, 0))
                .ThenBy(i => IndexOf(i.SourceName))
                .ToList();

            if (candidates.Count > 0)
            {
                var best = candidates[0];
                // 其它源同版本（或更新）的附件作为下载备选
                foreach (var other in candidates.Skip(1))
                {
                    if (other.Asset == null) continue;
                    if (ParseVersionTag(other.LatestVersion) >= ParseVersionTag(best.LatestVersion))
                    {
                        best.AlternateAssets.Add(other.Asset);
                    }
                }

                if (best.Asset == null && best.AlternateAssets.Count > 0) best.Asset = best.AlternateAssets[0];
                if (best.Asset == null)
                {
                    best.Message = "检测到新版本 " + best.LatestVersion + "，但没有找到可下载的安装包附件";
                }

                _log?.Info("检查更新：选中 " + best.SourceName + " 的 " + best.TagName + "（当前 v" + CurrentVersion + "）"
                           + (best.Asset == null ? "，无附件" : "，附件 " + best.Asset.Name));
                return best;
            }

            var summary = new UpdateInfo
            {
                CurrentVersion = CurrentVersion,
                HtmlUrl = ReleasesUrl,
                SourceName = _sources[0].Name,
                Checked = infos.Any(i => i.Checked)
            };
            summary.Message = string.Join("；", infos.Select(i =>
                i.SourceName + "：" + (string.IsNullOrWhiteSpace(i.Message) ? (i.Checked ? "已是最新版本" : "检查失败") : i.Message)));
            if (string.IsNullOrWhiteSpace(summary.Message)) summary.Message = "检查失败";
            _log?.Info("检查更新：没有新版本（" + summary.Message + "）");
            return summary;
        }

        private int IndexOf(string sourceName)
        {
            for (var i = 0; i < _sources.Count; i++)
            {
                if (string.Equals(_sources[i].Name, sourceName, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 99;
        }

        private async Task<UpdateInfo> CheckSourceAsync(UpdateSource source)
        {
            var info = new UpdateInfo
            {
                CurrentVersion = CurrentVersion,
                HtmlUrl = source.ReleasesUrl,
                SourceName = source.Name
            };

            try
            {
                var response = await _http.SendAsync(source.ApiUrl, "GET", null, null, null, 12000, false)
                    .ConfigureAwait(false);
                if (response.StatusCode == 200)
                {
                    if (source.ApiReturnsArray) ParseReleaseList(info, response.Text);
                    else ParseReleaseObject(info, response.Text);

                    // API 明确给出过版本时，不再用兜底覆盖结论
                    var apiGaveVersion = !string.IsNullOrWhiteSpace(info.LatestVersion);
                    if (!info.HasUpdate && !apiGaveVersion && source.SupportsLatestRedirect)
                    {
                        // API 返回空/无版本时，GitHub 还能用 releases/latest 跳转兜底
                        await TryRedirectAsync(source, info).ConfigureAwait(false);
                    }

                    if (!info.HasUpdate && string.IsNullOrWhiteSpace(info.LatestVersion))
                    {
                        await TryTagsAsync(source, info).ConfigureAwait(false);
                    }

                    info.Checked = true;
                    if (string.IsNullOrEmpty(info.Message) && !info.HasUpdate) info.Message = "已是最新版本";
                    return info;
                }

                info.Message = "API 返回 " + response.StatusCode;
            }
            catch (Exception ex)
            {
                info.Message = "API 不可用：" + ex.Message;
            }

            _log?.Warn("[" + source.Name + "] " + info.Message);

            if (source.SupportsLatestRedirect)
            {
                await TryRedirectAsync(source, info).ConfigureAwait(false);
            }

            if (!info.HasUpdate)
            {
                await TryTagsAsync(source, info).ConfigureAwait(false);
            }

            info.Checked = true;
            if (string.IsNullOrEmpty(info.Message) && !info.HasUpdate) info.Message = "已是最新版本";
            return info;
        }

        private void ParseReleaseObject(UpdateInfo info, string json)
        {
            var root = JsonHelper.ParseObject(json);
            if (root == null)
            {
                info.Checked = true;
                info.Message = "更新信息解析失败";
                return;
            }
            FillFromRelease(info, root);
        }

        private void ParseReleaseList(UpdateInfo info, string json)
        {
            JArray array = null;
            var trimmed = (json ?? "").Trim();
            if (trimmed.StartsWith("["))
            {
                try { array = JArray.Parse(trimmed); } catch { }
            }
            if (array == null)
            {
                info.Checked = true;
                info.Message = "更新信息解析失败";
                return;
            }

            if (array.Count == 0)
            {
                info.Checked = true;
                info.Message = "该源还没有发布版本";
                return;
            }

            JObject best = null;
            Version bestVersion = null;
            foreach (var token in array)
            {
                var obj = token as JObject;
                if (obj == null) continue;
                var version = ParseVersionTag(JsonHelper.GetString(obj, "tag_name", ""));
                if (version == null) continue;
                if (bestVersion == null || version > bestVersion)
                {
                    bestVersion = version;
                    best = obj;
                }
            }

            if (best == null)
            {
                info.Checked = true;
                info.Message = "该源没有可识别的版本";
                return;
            }

            FillFromRelease(info, best);
        }

        private void FillFromRelease(UpdateInfo info, JObject release)
        {
            info.TagName = JsonHelper.GetString(release, "tag_name", "");
            info.ReleaseName = JsonHelper.GetString(release, "name", "");
            info.ReleaseNotes = JsonHelper.GetString(release, "body", "");
            info.HtmlUrl = JsonHelper.GetString(release, "html_url", info.HtmlUrl);

            var assets = release["assets"] as JArray ?? release["attach_files"] as JArray;
            info.Asset = PickAsset(assets);

            var latest = ParseVersionTag(info.TagName);
            info.LatestVersion = latest == null ? info.TagName : Format(latest);
            info.HasUpdate = latest != null && latest > ParseVersionTag(CurrentVersion);
            info.Checked = true;
            if (!info.HasUpdate) info.Message = "已是最新版本";
        }

        private static UpdateAsset PickAsset(JArray assets)
        {
            if (assets == null) return null;
            UpdateAsset exe = null;
            foreach (var token in assets)
            {
                var obj = token as JObject;
                if (obj == null) continue;
                var name = JsonHelper.GetString(obj, "name", "");
                var url = JsonHelper.GetString(obj, "browser_download_url", "");
                if (string.IsNullOrWhiteSpace(url)) url = JsonHelper.GetString(obj, "download_url", "");
                if (string.IsNullOrWhiteSpace(url)) continue;
                var asset = new UpdateAsset
                {
                    Name = name,
                    Url = url,
                    Size = JsonHelper.GetInt(obj, "size", 0)
                };
                var lower = name.ToLowerInvariant();
                // 整包（zip）优先；单 exe 只接受 CampusLink.exe，安装器/Setup 不能拿来覆盖主程序
                if (lower.EndsWith(".zip")) return asset;
                if (lower.EndsWith(".exe") && exe == null
                    && !lower.Contains("setup") && !lower.Contains("install") && !lower.Contains("uninst"))
                {
                    exe = asset;
                }
            }
            return exe;
        }

        private async Task TryRedirectAsync(UpdateSource source, UpdateInfo info)
        {
            try
            {
                _log?.Debug("[" + source.Name + "] 尝试发布页兜底：" + source.ReleasesUrl + "/latest");
                var response = await _http.SendAsync(source.ReleasesUrl + "/latest", "GET", null, null, null, 12000, false)
                    .ConfigureAwait(false);
                _log?.Debug("[" + source.Name + "] 发布页返回 " + response.StatusCode + "，Location=" + (response.RawLocation ?? "(空)"));
                var location = response.RawLocation ?? "";
                if (string.IsNullOrWhiteSpace(location))
                {
                    info.Message = "该源还没有发布版本";
                    return;
                }

                var absolute = HttpTransport.CombineUrl(location, "");
                if (absolute.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    info.Message = "该源还没有发布版本";
                    return;
                }

                var tag = absolute.TrimEnd('/');
                var slash = tag.LastIndexOf('/');
                if (slash >= 0) tag = tag.Substring(slash + 1);
                tag = Uri.UnescapeDataString(tag);
                info.TagName = tag;
                info.HtmlUrl = absolute;
                var latest = ParseVersionTag(tag);
                info.LatestVersion = latest == null ? tag : Format(latest);
                info.HasUpdate = latest != null && latest > ParseVersionTag(CurrentVersion);
                if (!info.HasUpdate) info.Message = "已是最新版本";
                else if (info.Asset == null)
                {
                    info.Asset = new UpdateAsset
                    {
                        Name = "CampusLink.exe",
                        Url = source.RepoUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/CampusLink.exe"
                    };
                }
                _log?.Info("[" + source.Name + "] 发布页兜底：最新 " + tag + "，有更新=" + info.HasUpdate);
            }
            catch (Exception ex)
            {
                _log?.Warn("[" + source.Name + "] 发布页检测失败：" + ex.Message);
            }
        }

        private async Task TryTagsAsync(UpdateSource source, UpdateInfo info)
        {
            try
            {
                var response = await _http.SendAsync(source.TagsInfoUrl, "GET", null, null, null, 10000, false)
                    .ConfigureAwait(false);
                if (!response.Ok || string.IsNullOrEmpty(response.Text)) return;

                Version bestVersion = null;
                string bestTag = null;
                foreach (Match match in Regex.Matches(response.Text, "refs/tags/([^\\s\\^]+)"))
                {
                    var tag = match.Groups[1].Value;
                    var version = ParseVersionTag(tag);
                    if (version == null) continue;
                    if (bestVersion == null || version > bestVersion)
                    {
                        bestVersion = version;
                        bestTag = tag;
                    }
                }

                if (bestTag == null || !(bestVersion > ParseVersionTag(CurrentVersion))) return;

                info.TagName = bestTag;
                info.LatestVersion = Format(bestVersion);
                info.HasUpdate = true;
                info.Checked = true;
                info.Message = info.Asset == null ? "只在 tag 里发现新版本，没有找到安装包附件" : "";
                _log?.Info("[" + source.Name + "] tag 兜底：发现 " + bestTag);
            }
            catch (Exception ex)
            {
                _log?.Warn("[" + source.Name + "] tag 检测失败：" + ex.Message);
            }
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

            var match = Regex.Match(value, "^(\\d+)\\.(\\d+)(?:\\.(\\d+))?$");
            if (!match.Success) return null;
            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            return new Version(major, minor, build);
        }

        private static string Format(Version version) => version.Major + "." + version.Minor + "." + version.Build;

        // ---------------------------------------------------------------- 下载

        /// <summary>下载更新包并返回安装信息（zip 解出整目录，单 exe 则只替换 exe）。</summary>
        public async Task<UpdatePackage> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken token)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Asset == null || string.IsNullOrWhiteSpace(info.Asset.Url))
            {
                throw new InvalidOperationException("没有可下载的更新附件");
            }

            var urls = new List<string> { info.Asset.Url };
            // 同名不同后缀互备：有整包就试整包，没有就试单 exe
            var alternate = AlternateExtension(info.Asset.Url);
            if (alternate != null) urls.Add(alternate);
            foreach (var other in info.AlternateAssets)
            {
                if (other == null || string.IsNullOrWhiteSpace(other.Url)) continue;
                if (!urls.Contains(other.Url)) urls.Add(other.Url);
                var otherAlternate = AlternateExtension(other.Url);
                if (otherAlternate != null && !urls.Contains(otherAlternate)) urls.Add(otherAlternate);
            }

            Exception last = null;
            foreach (var url in urls)
            {
                try
                {
                    var extension = ExtensionOf(url);
                    var target = Path.Combine(Path.GetTempPath(),
                        "CampusLink-update-" + info.LatestVersion + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + extension);

                    _log?.Info("开始下载更新：" + url);
                    await DownloadFileAsync(url, target, progress, token, _useSystemProxy).ConfigureAwait(false);
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

        private static string AlternateExtension(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return url.Substring(0, url.Length - 4) + ".zip";
            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return url.Substring(0, url.Length - 4) + ".exe";
            return null;
        }

        private static string ExtensionOf(string url)
        {
            if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return ".exe";
            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ".zip";
            return ".bin";
        }

        private static async Task DownloadFileAsync(string url, string targetPath, IProgress<double> progress, CancellationToken token, bool useSystemProxy)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = true;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            if (!useSystemProxy) request.Proxy = null;

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

        private UpdatePackage PreparePackage(string downloadedPath)
        {
            if (!downloadedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdatePackage
                {
                    ExePath = downloadedPath,
                    SourceDirectory = Path.GetDirectoryName(downloadedPath) ?? "",
                    IsFullPackage = false
                };
            }

            var extractDir = Path.Combine(Path.GetTempPath(),
                "CampusLink-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(downloadedPath, extractDir);

            var exe = Directory.GetFiles(extractDir, "CampusLink.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null) throw new FileNotFoundException("更新包里没有找到 CampusLink.exe");

            return new UpdatePackage
            {
                ExePath = exe,
                SourceDirectory = Path.GetDirectoryName(exe) ?? extractDir,
                IsFullPackage = true
            };
        }

        // ---------------------------------------------------------------- 安装

        /// <summary>
        /// 写一个带重试的更新脚本：等本进程退出 →
        /// 整包用 robocopy 覆盖整个安装目录（单 exe 则只覆盖 exe）→ 重新启动。
        /// </summary>
        public void ApplyAndRestart(UpdatePackage package, LogService log)
        {
            if (package == null || string.IsNullOrWhiteSpace(package.ExePath)) return;

            var currentExe = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                throw new InvalidOperationException("无法定位当前程序的 exe 路径");
            }

            var targetDir = Path.GetDirectoryName(currentExe) ?? "";
            var script = Path.Combine(Path.GetTempPath(), "campuslink-update-" + Guid.NewGuid().ToString("N") + ".cmd");
            var content = new StringBuilder()
                .AppendLine("@echo off")
                .AppendLine("ping -n 3 127.0.0.1 >nul");

            if (package.IsFullPackage && !string.IsNullOrWhiteSpace(package.SourceDirectory))
            {
                content.AppendLine("for /l %%i in (1,1,60) do (");
                content.AppendLine("  robocopy \"" + package.SourceDirectory + "\" \"" + targetDir + "\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
                content.AppendLine("  if not errorlevel 8 goto done");
                content.AppendLine("  ping -n 2 127.0.0.1 >nul");
                content.AppendLine(")");
            }
            else
            {
                content.AppendLine("for /l %%i in (1,1,60) do (");
                content.AppendLine("  copy /y \"" + package.ExePath + "\" \"" + currentExe + "\" >nul 2>&1 && goto done");
                content.AppendLine("  ping -n 2 127.0.0.1 >nul");
                content.AppendLine(")");
            }

            content.AppendLine(":done")
                .AppendLine("start \"\" \"" + currentExe + "\"")
                .AppendLine("del \"%~f0\"");

            File.WriteAllText(script, content.ToString(), Encoding.Default);

            log?.Info("开始替换程序（" + (package.IsFullPackage ? "整包" : "单 exe") + "）："
                      + (package.IsFullPackage ? package.SourceDirectory : package.ExePath) + " -> " + targetDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
        }
    }
}
