using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class InstallPage : UserControl
{
    private InstallPageViewModel? _viewModel;

    public InstallPage()
    {
        InitializeComponent();

        // Avalonia 的 ToggleButton 没有 XAML 的 Checked/Unchecked 事件属性，
        // 用 IsCheckedChanged 统一转发到各自的处理方法（与 WPF 行为等价）。
        checkBox1.IsCheckedChanged += (_, _) => DispatchToggle(checkBox1, checkBox1_Checked, checkBox1_Unchecked);
        checkBox2.IsCheckedChanged += (_, _) => DispatchToggle(checkBox2, checkBox2_Checked, checkBox2_Unchecked);
        checkBox3.IsCheckedChanged += (_, _) => DispatchToggle(checkBox3, checkBox3_Checked, checkBox3_Unchecked);
        checkBox4.IsCheckedChanged += (_, _) => DispatchToggle(checkBox4, checkBox4_Checked, checkBox4_Unchecked);

        WeakReferenceMessenger.Default.Register<string, string>(this, "McVersion", (_, m) => SetMcVersion(m));
    }

    /// <summary>把 IsCheckedChanged 转发成「勾选 / 取消勾选」两个处理器之一。</summary>
    private static void DispatchToggle(
        CheckBox box,
        EventHandler<RoutedEventArgs> onChecked,
        EventHandler<RoutedEventArgs> onUnchecked)
    {
        var args = new RoutedEventArgs();
        if (box.IsChecked == true)
            onChecked(box, args);
        else
            onUnchecked(box, args);
    }

    private async void SetMcVersion(string msg)
    {
        var vm = new InstallPageViewModel(msg);
        _viewModel = vm;
        DataContext = vm;

        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        AppServices_Notify($"正在安装版本 {msg}");

        // 加载前重置 CheckBox 状态
        checkBox1.IsEnabled = checkBox2.IsEnabled = checkBox3.IsEnabled = checkBox4.IsEnabled = true;
        checkBox1.IsChecked = checkBox2.IsChecked = checkBox3.IsChecked = checkBox4.IsChecked = false;

        await vm.Load();
        ApplyLoaderAvailability();
        UpdateCardStates();
        UpdateEmptyHints();
    }

    private static void AppServices_Notify(string message)
        => Services.AppServices.Notifications?.Show(message, "提示", Services.NotificationSeverity.Informational);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstallPageViewModel.ForgeVersions)
            or nameof(InstallPageViewModel.FabricVersions)
            or nameof(InstallPageViewModel.NeoforgeVersions)
            or nameof(InstallPageViewModel.QuiltVersions))
        {
            UpdateEmptyHints();
        }
    }

    /// <summary>根据 ViewModel 中的 Available 状态禁用 404 的加载器选项。</summary>
    private void ApplyLoaderAvailability()
    {
        if (_viewModel is not InstallPageViewModel vm)
            return;

        if (!vm.ForgeAvailable) checkBox1.IsEnabled = false;
        if (!vm.FabricAvailable) checkBox2.IsEnabled = false;
        if (!vm.NeoforgeAvailable) checkBox3.IsEnabled = false;
        if (!vm.QuiltAvailable) checkBox4.IsEnabled = false;
    }

    private void UpdateEmptyHints()
    {
        if (_viewModel is null)
            return;

        forgeEmpty.IsVisible = _viewModel.ForgeVersions.Count == 0;
        fabricEmpty.IsVisible = _viewModel.FabricVersions.Count == 0;
        neoforgeEmpty.IsVisible = _viewModel.NeoforgeVersions.Count == 0;
        quiltEmpty.IsVisible = _viewModel.QuiltVersions.Count == 0;
    }

    /// <summary>被勾选的加载器卡片边框点亮（替代 WPF 的 DataTrigger 描边）。</summary>
    private void UpdateCardStates()
    {
        SetSelected(forgeCard, checkBox1.IsChecked == true);
        SetSelected(fabricCard, checkBox2.IsChecked == true);
        SetSelected(neoforgeCard, checkBox3.IsChecked == true);
        SetSelected(quiltCard, checkBox4.IsChecked == true);
    }

    private static void SetSelected(Border card, bool selected)
    {
        if (selected)
        {
            if (!card.Classes.Contains("selected"))
                card.Classes.Add("selected");
        }
        else
        {
            card.Classes.Remove("selected");
        }
    }

    // checkBox1 = Forge：勾选 Forge → 禁用 Fabric、Quilt（NeoForge 可与 Forge 共存）
    private void checkBox1_Checked(object? sender, RoutedEventArgs e)
    {
        checkBox2.IsEnabled = false;
        checkBox4.IsEnabled = false;
        UpdateCardStates();
    }

    private void checkBox1_Unchecked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not InstallPageViewModel vm)
            return;

        checkBox2.IsEnabled = vm.FabricAvailable;
        checkBox4.IsEnabled = vm.QuiltAvailable;
        vm.SelectForgeVersion = string.Empty;
        UpdateCardStates();
    }

    // checkBox2 = Fabric：勾选 Fabric → 禁用 Forge、Quilt、NeoForge
    private void checkBox2_Checked(object? sender, RoutedEventArgs e)
    {
        checkBox1.IsEnabled = false;
        checkBox3.IsEnabled = false;
        checkBox4.IsEnabled = false;
        UpdateCardStates();
    }

    private void checkBox2_Unchecked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not InstallPageViewModel vm)
            return;

        checkBox1.IsEnabled = vm.ForgeAvailable;
        checkBox3.IsEnabled = vm.NeoforgeAvailable;
        checkBox4.IsEnabled = vm.QuiltAvailable;
        vm.SelectFabricVersion = string.Empty;
        UpdateCardStates();
    }

    // checkBox3 = NeoForge：勾选 NeoForge → 禁用 Fabric、Quilt
    private void checkBox3_Checked(object? sender, RoutedEventArgs e)
    {
        checkBox2.IsEnabled = false;
        checkBox4.IsEnabled = false;
        UpdateCardStates();
    }

    private void checkBox3_Unchecked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not InstallPageViewModel vm)
            return;

        checkBox2.IsEnabled = vm.FabricAvailable;
        checkBox4.IsEnabled = vm.QuiltAvailable;
        vm.SelectNeoforgeVersion = string.Empty;
        UpdateCardStates();
    }

    // checkBox4 = Quilt：勾选 Quilt → 禁用所有其他（Forge、Fabric、NeoForge）
    private void checkBox4_Checked(object? sender, RoutedEventArgs e)
    {
        checkBox1.IsEnabled = false;
        checkBox2.IsEnabled = false;
        checkBox3.IsEnabled = false;
        UpdateCardStates();
    }

    private void checkBox4_Unchecked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not InstallPageViewModel vm)
            return;

        checkBox1.IsEnabled = vm.ForgeAvailable;
        checkBox2.IsEnabled = vm.FabricAvailable;
        checkBox3.IsEnabled = vm.NeoforgeAvailable;
        vm.SelectQuiltVersion = string.Empty;
        UpdateCardStates();
    }
}
