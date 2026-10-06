using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.ViewModels.Pages
{
    public class DashboardPageViewModel : ObservableObject
    {
        public ObservableCollection<NewsItem> News { get; set; } = new ObservableCollection<NewsItem>();
        public bool IsLoaded { get; set; }

        public DashboardPageViewModel()
        {
            IsLoaded = false;
        }

        public async Task LoadNewsAsync()
        {
            try
            {
                var list = await MojangNewsHelper.GetNewsListAsync();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    News.Clear();
                    foreach (var item in list) News.Add(item);
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "加载新闻列表失败");
            }
            IsLoaded = true;
        }
    }
}
