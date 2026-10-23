using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using CampusLink.Infrastructure;
using CampusLink.Services;

namespace CampusLink.ViewModels
{
    public class SocialLink
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Badge { get; set; }

        public string DisplayUrl => Url;
    }

    /// <summary>关于页：作者社交媒体、赞助码、免责声明。</summary>
    public class AboutViewModel : ObservableObject
    {
        private readonly LogService _log;
        private string _status = "点「打开」在浏览器里访问，或「复制链接」分享。";

        public AboutViewModel(LogService log, string dataDirectory, string logDirectory, UpdateViewModel update)
        {
            _log = log;
            DataDirectory = dataDirectory;
            LogDirectory = logDirectory;
            Update = update;

            Links = new ObservableCollection<SocialLink>
            {
                new SocialLink { Name = "项目主页（GitHub）", Badge = "P", Url = ProjectUrl },
                new SocialLink { Name = "GitHub", Badge = "G", Url = "https://github.com/Thomas455" },
                new SocialLink { Name = "哔哩哔哩", Badge = "B", Url = "https://space.bilibili.com/646916080" },
                new SocialLink
                {
                    Name = "抖音", Badge = "D",
                    Url = "https://www.douyin.com/user/MS4wLjABAAAAtC_E5FCBoZ3xtzK8U4P0kKPT8-_lSeOk6f3HQzlZ03g"
                },
                new SocialLink { Name = "个人主页", Badge = "W", Url = "https://thomas455.github.io/" }
            };

            OpenLinkCommand = new RelayCommand<SocialLink>(OpenLink);
            CopyLinkCommand = new RelayCommand<SocialLink>(CopyLink);
            OpenDataFolderCommand = new RelayCommand(() => OpenFolder(DataDirectory));
            OpenLogFolderCommand = new RelayCommand(() => OpenFolder(LogDirectory));
            OpenProjectCommand = new RelayCommand(() => ShellHelper.OpenUrl(ProjectUrl, _log));
        }

        public const string ProjectUrl = "https://github.com/Thomas455/CampusLink";

        public UpdateViewModel Update { get; }

        public ObservableCollection<SocialLink> Links { get; }

        public ICommand OpenLinkCommand { get; }
        public ICommand CopyLinkCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand OpenProjectCommand { get; }

        public string DataDirectory { get; }

        public string LogDirectory { get; }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string VersionText
        {
            get
            {
                try
                {
                    var version = Assembly.GetExecutingAssembly().GetName().Version;
                    return "版本 " + (version == null ? "1.0.0" : version.ToString(3));
                }
                catch
                {
                    return "版本 1.0.0";
                }
            }
        }

        public string ProductName => "CampusLink 校园网认证助手";

        private void OpenLink(SocialLink link)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.Url)) return;
            ShellHelper.OpenUrl(link.Url, _log);
            Status = "已在浏览器打开：" + link.Name;
        }

        private void CopyLink(SocialLink link)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.Url)) return;
            try
            {
                Clipboard.SetText(link.Url);
                Status = "已复制：" + link.Name + " 链接";
            }
            catch (Exception ex)
            {
                Status = "复制失败：" + ex.Message;
            }
        }

        private void OpenFolder(string path)
        {
            ShellHelper.OpenFolder(path, _log);
        }
    }
}
