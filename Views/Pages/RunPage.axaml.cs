using Avalonia.Controls;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class RunPage : UserControl
{
    private readonly RunPageViewModel? _viewModel;

    public RunPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(RunPageViewModel)) as RunPageViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (_viewModel is { IsLoaded: false })
                _viewModel.LoadCommand.Execute(null);
        };
    }
}
