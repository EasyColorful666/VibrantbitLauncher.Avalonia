using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class MultiplayerPage : UserControl
{
    private readonly MultiplayerPageViewModel? _viewModel;

    public MultiplayerPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(MultiplayerPageViewModel)) as MultiplayerPageViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += (_, _) =>
        {
            _viewModel?.InitializeCommand.Execute(null);
            UpdateStatusDot();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // StatusLevel 变化即代表状态圆点该换色（替代 WPF 的 DataTrigger）
        if (e.PropertyName == nameof(MultiplayerPageViewModel.StatusLevel))
            UpdateStatusDot();
    }

    /// <summary>绿 = 可用，黄 = 进行中，红 = 未就绪。</summary>
    private void UpdateStatusDot()
    {
        var level = _viewModel?.StatusLevel ?? "Idle";

        var brushKey = level switch
        {
            "Ready" => "SystemFillColorSuccessBrush",
            "Busy" => "SystemFillColorCautionBrush",
            _ => "SystemFillColorCriticalBrush"
        };

        if (this.TryFindResource(brushKey, out var brush) && brush is IBrush b)
            statusDot.Fill = b;
    }
}
