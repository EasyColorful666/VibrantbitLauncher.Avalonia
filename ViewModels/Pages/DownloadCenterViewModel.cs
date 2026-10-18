using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Views.Pages;

namespace VibrantbitLauncher.ViewModels.Pages
{
    public class DownloadCenterViewModel : ObservableObject
    {
        private readonly DownloadTaskService _taskService;

        public ObservableCollection<DownloadTaskItem> Tasks => _taskService.Tasks;

        public RelayCommand<DownloadTaskItem> RemoveCommand { get; }
        public RelayCommand<DownloadTaskItem> ViewDetailCommand { get; }
        public RelayCommand ClearCompletedCommand { get; }
        public RelayCommand GoToVersionDownloadCommand { get; }
        public RelayCommand GoToModDownloadCommand { get; }

        public DownloadCenterViewModel(DownloadTaskService taskService)
        {
            _taskService = taskService;
            RemoveCommand = new RelayCommand<DownloadTaskItem>(task =>
            {
                if (task != null && !task.IsActive)
                    _taskService.RemoveTask(task);
            });
            ViewDetailCommand = new RelayCommand<DownloadTaskItem>(task =>
            {
                if (task == null) return;

                // 整合包安装任务：直接打开进度详情窗口（已在开的话会被调到最前）。
                if (task.IsModpack)
                {
                    ModpackInstallCoordinator.ShowCurrentProgress();
                    return;
                }

                // 游戏安装任务：跳到**对应版本的「安装版本」详情进度页**（InstallPage），
                // 把版本号一并带过去。这里绝不能退回下载中心的通用分类列表 ——
                // 那只是「下载资源」的导航，看不到这一次安装到底装到哪了。
                if (task.TaskType == DownloadTaskType.GameInstall)
                {
                    if (!string.IsNullOrEmpty(task.TargetMcVersion))
                    {
                        AppNavigation.Navigate(typeof(InstallPage));
                        WeakReferenceMessenger.Default.Send<string, string>(task.TargetMcVersion!, "McVersion");
                    }
                    else
                    {
                        // 升级前创建的旧任务没记版本号：退化为回下载中心，
                        // 至少别让按钮点了毫无反应。
                        WeakReferenceMessenger.Default.Send<Type, string>(typeof(DownloadHubPage), "NavigateTo");
                        WeakReferenceMessenger.Default.Send(new DownloadHubTabMessage(0));
                    }
                    return;
                }

                // 模组文件下载：没有独立的进度详情页，对应的仍是下载中心的模组分类。
                WeakReferenceMessenger.Default.Send<Type, string>(typeof(DownloadHubPage), "NavigateTo");
                WeakReferenceMessenger.Default.Send(new DownloadHubTabMessage(1));
            });
            ClearCompletedCommand = new RelayCommand(() => _taskService.ClearCompleted());
            GoToVersionDownloadCommand = new RelayCommand(() =>
            {
                WeakReferenceMessenger.Default.Send<Type, string>(typeof(DownloadHubPage), "NavigateTo");
                WeakReferenceMessenger.Default.Send(new DownloadHubTabMessage(0));
            });
            GoToModDownloadCommand = new RelayCommand(() =>
            {
                WeakReferenceMessenger.Default.Send<Type, string>(typeof(DownloadHubPage), "NavigateTo");
                WeakReferenceMessenger.Default.Send(new DownloadHubTabMessage(1));
            });
        }
    }
}
