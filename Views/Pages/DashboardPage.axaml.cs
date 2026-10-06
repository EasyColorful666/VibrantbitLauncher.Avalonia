using Avalonia.Controls;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class DashboardPage : UserControl
{
    private readonly DashboardPageViewModel? _viewModel;

    public DashboardPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(DashboardPageViewModel)) as DashboardPageViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (_viewModel is { IsLoaded: false })
                await _viewModel.LoadNewsAsync();
        };
    }
}
