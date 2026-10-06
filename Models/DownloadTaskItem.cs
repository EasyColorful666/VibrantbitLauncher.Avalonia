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
            _ => "其他"
        };

        public bool IsActive => Status == DownloadTaskStatus.Downloading || Status == DownloadTaskStatus.Pending;
    }
}
