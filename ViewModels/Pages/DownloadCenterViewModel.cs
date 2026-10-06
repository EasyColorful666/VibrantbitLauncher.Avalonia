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
                WeakReferenceMessenger.Default.Send<Type, string>(typeof(DownloadHubPage), "NavigateTo");
                WeakReferenceMessenger.Default.Send(new DownloadHubTabMessage(task.TaskType == DownloadTaskType.GameInstall ? 0 : 1));
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
