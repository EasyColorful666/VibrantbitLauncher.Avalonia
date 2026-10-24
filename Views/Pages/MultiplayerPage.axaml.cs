using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
            UpdateLanModDot();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // StatusLevel / LanModStatusLevel 变化即代表对应圆点该换色（替代 WPF 的 DataTrigger）
        if (e.PropertyName == nameof(MultiplayerPageViewModel.StatusLevel))
            UpdateStatusDot();
        else if (e.PropertyName == nameof(MultiplayerPageViewModel.LanModStatusLevel))
            UpdateLanModDot();
    }

    /// <summary>联机服务状态：绿 = 可用，黄 = 进行中，红 = 未就绪。</summary>
    private void UpdateStatusDot()
    {
        var brushKey = (_viewModel?.StatusLevel ?? "Idle") switch
        {
            "Ready" => "SystemFillColorSuccessBrush",
            "Busy" => "SystemFillColorCautionBrush",
            _ => "SystemFillColorCriticalBrush"
        };

        ApplyDotColor(statusDot, brushKey);
    }

    /// <summary>联机模组状态：绿 = 已装好，黄 = 安装中，红 = 装不了，灰 = 待安装。</summary>
    private void UpdateLanModDot()
    {
        var brushKey = (_viewModel?.LanModStatusLevel ?? "Idle") switch
        {
            "Ready" => "SystemFillColorSuccessBrush",
            "Busy" => "SystemFillColorCautionBrush",
            "Warn" => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorNeutralBrush"
        };

        ApplyDotColor(lanModDot, brushKey);
    }

    private void ApplyDotColor(Ellipse dot, string brushKey)
    {
        if (this.TryFindResource(brushKey, out var brush) && brush is IBrush b)
        {
            dot.Fill = b;
            return;
        }

        // 主题里没有这个键时退回中性前景色，别让圆点停在上一个状态的颜色上
        if (this.TryFindResource("TextFillColorSecondaryBrush", out var fallback) && fallback is IBrush f)
            dot.Fill = f;
    }
}
