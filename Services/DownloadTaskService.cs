using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using VibrantbitLauncher.Models;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 全局下载任务管理服务（单例），跟踪所有版本安装和模组下载任务。
    /// </summary>
    public class DownloadTaskService
    {
        public ObservableCollection<DownloadTaskItem> Tasks { get; } = new();

        public DownloadTaskItem CreateTask(string name, DownloadTaskType type)
        {
            var task = new DownloadTaskItem
            {
                Name = name,
                TaskType = type,
                Status = DownloadTaskStatus.Downloading,
                StatusText = "准备中..."
            };

            Dispatcher.UIThread.Invoke(() => Tasks.Insert(0, task));
            return task;
        }

        public void UpdateProgress(DownloadTaskItem task, int progress, string speed = "", string statusText = "")
        {
            if (task == null) return;
            Dispatcher.UIThread.Invoke(() =>
            {
                task.Progress = Math.Clamp(progress, 0, 100);
                if (!string.IsNullOrEmpty(speed))
                    task.Speed = speed;
                if (!string.IsNullOrEmpty(statusText))
                    task.StatusText = statusText;
                task.Status = DownloadTaskStatus.Downloading;
            });
        }

        public void CompleteTask(DownloadTaskItem task, string message = "")
        {
            if (task == null) return;
            Dispatcher.UIThread.Invoke(() =>
            {
                task.Progress = 100;
                task.Status = DownloadTaskStatus.Completed;
                task.StatusText = string.IsNullOrEmpty(message) ? "下载完成" : message;
                task.Speed = string.Empty;
            });
        }

        public void FailTask(DownloadTaskItem task, string error)
        {
            if (task == null) return;
            Dispatcher.UIThread.Invoke(() =>
            {
                task.Status = DownloadTaskStatus.Failed;
                task.StatusText = error;
                task.Speed = string.Empty;
            });
        }

        public void RemoveTask(DownloadTaskItem task)
        {
            if (task == null) return;
            Dispatcher.UIThread.Invoke(() => Tasks.Remove(task));
        }

        public void ClearCompleted()
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                var toRemove = Tasks
                    .Where(t => t.Status == DownloadTaskStatus.Completed
                             || t.Status == DownloadTaskStatus.Failed
                             || t.Status == DownloadTaskStatus.Canceled)
                    .ToList();
                foreach (var t in toRemove)
                    Tasks.Remove(t);
            });
        }
    }
}
