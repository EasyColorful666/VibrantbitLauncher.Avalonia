using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.Models;
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

        // 别处装了新实例（整合包 / 新版本）后要立刻能在下拉框里看到。
        // 本页只在第一次显示时扫一次盘（IsLoaded 守卫），没有这个订阅的话，
        // 用户装完整合包回到启动页会发现列表里还是没有它，得重启程序才行。
        // 用弱引用订阅 → 页面被回收时会自动解绑，不会把旧实例钉在内存里。
        WeakReferenceMessenger.Default.Register<InstancesChangedMessage, string>(
            this, "InstancesChanged",
            static (recipient, _) =>
            {
                if (recipient is RunPage page && page._viewModel is not null)
                    page._viewModel.RefreshCommand.Execute(null);
            });
    }
}
