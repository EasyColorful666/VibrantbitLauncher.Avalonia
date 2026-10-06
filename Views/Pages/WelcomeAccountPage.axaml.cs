using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 开机向导第 3 步：登录账户。
/// 复用账户页的视图模型和登录命令，保证两种入口行为完全一致。
/// </summary>
public partial class WelcomeAccountPage : UserControl
{
    private readonly AccountPageViewModel _viewModel;

    public WelcomeAccountPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(AccountPageViewModel)) as AccountPageViewModel
                     ?? new AccountPageViewModel();

        DataContext = _viewModel;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (!_viewModel.IsLoaded)
                _viewModel.LoadCommand.Execute(null);

            UpdateEmptyState();
        };

        _viewModel.Users.CollectionChanged += OnUsersChanged;
    }

    private void OnUsersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateEmptyState();

    /// <summary>没有账户时显示引导文案。</summary>
    private void UpdateEmptyState()
        => emptyState.IsVisible = _viewModel.Users.Count == 0;

    private void OnBack(object? sender, RoutedEventArgs e)
        => AppNavigation.Navigate(typeof(WelcomeSetupPage));

    private void OnSkip(object? sender, RoutedEventArgs e)
        => AppNavigation.Navigate(typeof(WelcomeCompletePage));

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        SettingsService.Save();
        AppNavigation.Navigate(typeof(WelcomeCompletePage));
    }
}
