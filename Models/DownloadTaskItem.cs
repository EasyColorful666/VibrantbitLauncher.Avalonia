using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace VibrantbitLauncher.Models
{
    public enum DownloadTaskStatus
    {
        Pending,
        Downloading,
        Completed,
        Failed,
        Canceled
    }

    public enum DownloadTaskType
    {
        GameInstall,
        ModDownload,

        /// <summary>
        /// 按「配装码」从零装一个实例。走的是整合包的安装器，但对用户来说是两回事 ——
        /// 任务列表里必须显示「配装码」，不能显示成「游戏版本」或「整合包」。
        /// </summary>
        LoadoutInstall,

        Other
    }

    public class DownloadTaskItem : ObservableObject
    {
        private int _progress;
        private string _speed = string.Empty;
        private string _statusText = string.Empty;
        private DownloadTaskStatus _status = DownloadTaskStatus.Pending;

        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public DownloadTaskType TaskType { get; set; } = DownloadTaskType.Other;
        public DateTime CreatedTime { get; } = DateTime.Now;

        /// <summary>
        /// 这是不是一个整合包安装任务。用于让「查看详情」直接打开整合包进度详情页
        /// （而不是跳到下载中心的通用列表）。不加对 Models 之外的依赖，只放一个标记。
        /// </summary>
        public bool IsModpack { get; set; }

        /// <summary>
        /// 该任务关联的 Minecraft 版本号（如 "1.20.1"）。
        ///
        /// 「下载任务」页点「查看详情」时，安装型任务要靠它跳到**对应版本**的
        /// 「安装版本」页（InstallPage）看进度；模组文件下载等没有关联版本的任务留空。
        /// </summary>
        public string? TargetMcVersion { get; set; }

        public int Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        public string Speed
        {
            get => _speed;
            set => SetProperty(ref _speed, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public DownloadTaskStatus Status
        {
            get => _status;
            set
            {
                SetProperty(ref _status, value);
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(IsActive));
            }
        }

        public string StatusDisplay => Status switch
        {
            DownloadTaskStatus.Pending => "等待中",
            DownloadTaskStatus.Downloading => "下载中",
            DownloadTaskStatus.Completed => "已完成",
            DownloadTaskStatus.Failed => "失败",
            DownloadTaskStatus.Canceled => "已取消",
            _ => string.Empty
        };

        public string TypeDisplay => TaskType switch
        {
            DownloadTaskType.GameInstall => "游戏版本",
            DownloadTaskType.ModDownload => "模组文件",
            DownloadTaskType.LoadoutInstall => "配装码",
            _ => "其他"
        };

        public bool IsActive => Status == DownloadTaskStatus.Downloading || Status == DownloadTaskStatus.Pending;
    }
}
