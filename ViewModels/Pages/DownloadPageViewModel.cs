using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MinecraftLaunch.Base.Interfaces;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Windows;
using VibrantbitLauncher.Views.Pages;

namespace VibrantbitLauncher.ViewModels.Pages
{
    public class DownloadPageViewModel : ObservableObject
    {
        public List<IInstallEntry> installEntries = new();
        private string mcFolder;

        ObservableCollection<McVersion> mcVersions = new ObservableCollection<McVersion>();
        public ObservableCollection<McVersion> McVersions
        {
            get => mcVersions;
            set => SetProperty(ref mcVersions, value);
        }

        public RelayCommand<string> DownloadCommand { get; }
        public bool IsLoaded { get; set; }

        public DownloadPageViewModel()
        {
            DownloadCommand = new RelayCommand<string>(Download);
            IsLoaded = false;
        }

        public void Download(string McVersion)
        {
            // 通过导航服务切到安装页，并把所选版本号广播过去
            AppNavigation.Navigate(typeof(InstallPage));
            WeakReferenceMessenger.Default.Send<string, string>(McVersion, "McVersion");
        }
    }
}
