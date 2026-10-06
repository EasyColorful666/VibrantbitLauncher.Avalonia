using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 「hello」手写笔迹入场动画：两条 Path 依次用虚线偏移描出，画完再启用「下一步」。
/// </summary>
public partial class HelloEffectPage : UserControl
{
    public HelloEffectPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 用路径实际长度做虚线动画（Avalonia 的 Geometry.Bounds 是包围盒，这里沿用原项目近似算法）
        var hLen = Math.Max(PathH.Data?.Bounds.Width ?? 120, 120) * 4;
        var elloLen = Math.Max(PathEllo.Data?.Bounds.Width ?? 580, 200) * 4;

        PathH.StrokeDashArray = new AvaloniaList<double> { hLen, hLen };
        PathH.StrokeDashOffset = hLen;
        PathEllo.StrokeDashArray = new AvaloniaList<double> { elloLen, elloLen };
        PathEllo.StrokeDashOffset = elloLen;

        // h 笔画：0.4s 淡入 + 0.8s 描线
        var hFade = new Animation
        {
            Duration = TimeSpan.FromSeconds(0.4),
            FillMode = FillMode.Forward,
            Children = { new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } } }
        };

        var hDash = new Animation
        {
            Duration = TimeSpan.FromSeconds(0.8),
            Easing = new QuadraticEaseInOut(),
            FillMode = FillMode.Forward,
            Children = { new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Shape.StrokeDashOffsetProperty, 0d) } } }
        };

        var elloFade = new Animation
        {
            Duration = TimeSpan.FromSeconds(0.7),
            Delay = TimeSpan.FromSeconds(0.7),
            FillMode = FillMode.Forward,
            Children = { new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } } }
        };

        var elloDash = new Animation
        {
            Duration = TimeSpan.FromSeconds(2.8),
            Delay = TimeSpan.FromSeconds(0.7),
            Easing = new QuadraticEaseInOut(),
            FillMode = FillMode.Forward,
            Children = { new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Shape.StrokeDashOffsetProperty, 0d) } } }
        };

        _ = hFade.RunAsync(PathH);
        _ = elloDash.RunAsync(PathH);
        _ = elloFade.RunAsync(PathEllo);
        _ = elloDash.RunAsync(PathEllo);

        // 动画完成（0.7 + 2.8 = 3.5s）后启用「下一步」
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        NextButton.IsEnabled = true;
    }

    private void NextButton_Click(object? sender, RoutedEventArgs e)
        => AppNavigation.Navigate(typeof(WelcomeCompletePage));
}
