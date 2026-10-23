using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Models;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    /// <summary>检查更新 / 下载（带进度）/ 替换重启。</summary>
    public class UpdateViewModel : ObservableObject
    {
        private readonly UpdateService _service;
        private readonly LogService _log;
        private readonly ToastService _toast;
        private readonly Action _shutdown;

        private UpdateInfo _info = new UpdateInfo();
        private bool _isChecking;
        private bool _isDownloading;
        private double _progress;
        private string _progressText = "";
        private string _statusText = "尚未检查更新";

        public UpdateViewModel(UpdateService service, LogService log, Action shutdown, ToastService toast)
        {
            _service = service;
            _log = log;
            _shutdown = shutdown;
            _toast = toast;

            CheckCommand = new AsyncRelayCommand(() => CheckAsync(true), () => !IsChecking && !IsDownloading, OnError);
            DownloadCommand = new AsyncRelayCommand(DownloadAndInstallAsync,
                () => HasUpdate && !IsDownloading && !IsChecking, OnError);
            OpenReleaseCommand = new RelayCommand(
                () => ShellHelper.OpenUrl(string.IsNullOrWhiteSpace(Info.HtmlUrl) ? _service.ReleasesUrl : Info.HtmlUrl, _log));
            OpenRepoCommand = new RelayCommand(() => ShellHelper.OpenUrl(_service.RepoUrl, _log));
        }

        /// <summary>manual=true 表示用户主动点检查（界面会弹更新窗口）。</summary>
        public event EventHandler<bool> CheckFinished;

        public ICommand CheckCommand { get; }
        public ICommand DownloadCommand { get; }
        public ICommand OpenReleaseCommand { get; }
        public ICommand OpenRepoCommand { get; }

        public UpdateInfo Info
        {
            get => _info;
            private set
            {
                _info = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUpdate));
                OnPropertyChanged(nameof(BannerText));
                OnPropertyChanged(nameof(VersionPair));
                OnPropertyChanged(nameof(Notes));
                OnPropertyChanged(nameof(LatestVersionText));
                OnPropertyChanged(nameof(AssetName));
                OnPropertyChanged(nameof(SourceText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasUpdate => _info != null && _info.HasUpdate;

        public string BannerText => HasUpdate
            ? "发现新版本 " + _info.VersionPair
              + (string.IsNullOrWhiteSpace(_info.SourceName) ? "" : "（" + _info.SourceName + "）")
              + " · 更新会自动下载并重启，账号配置保留"
            : "";

        public string VersionPair => _info == null ? "" : _info.VersionPair;

        public string Notes => CleanNotes(_info?.ReleaseNotes);

        /// <summary>发布说明是 Markdown，这里做最轻量的清理（去标题井号/加粗），避免界面里全是符号。</summary>
        private static string CleanNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes)) return "（该版本没有填写发布说明）";
            var lines = notes.Replace("\r\n", "\n").Split('\n');
            var builder = new System.Text.StringBuilder();
            var lastBlank = false;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("### ")) line = trimmed.Substring(4);
                else if (trimmed.StartsWith("## ")) line = trimmed.Substring(3);
                else if (trimmed.StartsWith("# ")) line = trimmed.Substring(2);
                line = line.Replace("**", "").Replace("`", "");

                var isBlank = string.IsNullOrWhiteSpace(line);
                if (isBlank && lastBlank) continue;
                builder.AppendLine(line);
                lastBlank = isBlank;
            }
            return builder.ToString().Trim();
        }

        public string LatestVersionText => _info == null || string.IsNullOrWhiteSpace(_info.LatestVersion)
            ? "—"
            : "v" + _info.LatestVersion;

        public string CurrentVersionText => "v" + _service.CurrentVersion;

        public string AssetName => _info?.Asset == null ? "" : _info.Asset.Name;

        public string SourceText => _info == null || string.IsNullOrWhiteSpace(_info.SourceName)
            ? ""
            : "来源：" + _info.SourceName;

        public bool IsChecking
        {
            get => _isChecking;
            private set
            {
                if (!SetProperty(ref _isChecking, value)) return;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            private set
            {
                if (!SetProperty(ref _isDownloading, value)) return;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public double Progress
        {
            get => _progress;
            private set => SetProperty(ref _progress, value);
        }

        public string ProgressText
        {
            get => _progressText;
            private set => SetProperty(ref _progressText, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>关闭顶部提示条（本次运行不再提示，下次检查仍会提示）。</summary>
        public void Dismiss()
        {
            if (_info == null) return;
            _info.HasUpdate = false;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(BannerText));
        }

        public async Task<UpdateInfo> CheckAsync(bool manual)
        {
            if (IsChecking) return Info;
            IsChecking = true;
            StatusText = "正在检查更新…";
            try
            {
                var info = await _service.CheckAsync().ConfigureAwait(true);
                Info = info;
                StatusText = info.HasUpdate
                    ? "发现新版本 " + info.VersionPair
                      + (string.IsNullOrWhiteSpace(info.SourceName) ? "" : "（来源 " + info.SourceName + "）")
                    : (string.IsNullOrWhiteSpace(info.Message) ? "已是最新版本" : info.Message);
            }
            finally
            {
                IsChecking = false;
            }

            // 事件必须在 IsChecking 复位后触发：手动检查时界面会在事件里同步打开弹窗，
            // 若此时 IsChecking 仍为 true，「立即更新」按钮会一直处于禁用状态
            if (Info != null && Info.HasUpdate)
            {
                _toast?.Info("发现新版本 " + Info.VersionPair
                             + (string.IsNullOrWhiteSpace(Info.SourceName) ? "" : "（" + Info.SourceName + "）"), "检查更新");
            }
            else if (manual)
            {
                _toast?.Success(StatusText, "检查更新");
            }

            CheckFinished?.Invoke(this, manual);
            return Info;
        }

        private async Task DownloadAndInstallAsync()
        {
            if (!HasUpdate) return;
            IsDownloading = true;
            Progress = 0;
            ProgressText = "0%";
            StatusText = "正在下载 " + _info.Asset?.Name + " …";
            try
            {
                var progress = new Progress<double>(value =>
                {
                    Progress = value;
                    ProgressText = value.ToString("0") + "%";
                });

                var package = await _service.DownloadAsync(_info, progress, CancellationToken.None).ConfigureAwait(true);
                Progress = 100;
                ProgressText = "100%";
                StatusText = "下载完成（" + (package.IsFullPackage ? "整包" : "单 exe") + "），正在退出并替换程序…（会短暂关闭窗口）";
                _log.Info("更新包已就绪：" + (package.IsFullPackage ? package.SourceDirectory : package.ExePath));

                _toast?.Success("更新包已就绪，正在重启安装", "更新");
                _service.ApplyAndRestart(package, _log);
                _shutdown?.Invoke();
            }
            catch (Exception ex)
            {
                StatusText = "更新失败：" + ex.Message;
                _toast?.Error(ex.Message, "更新失败");
                _log.Error("更新失败", ex);
            }
            finally
            {
                IsDownloading = false;
            }
        }

        private void OnError(Exception ex)
        {
            IsChecking = false;
            IsDownloading = false;
            StatusText = "操作异常：" + ex.Message;
            _log.Error("更新操作异常", ex);
        }
    }
}
