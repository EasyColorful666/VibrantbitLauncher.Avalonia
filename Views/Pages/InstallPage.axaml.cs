using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class InstallPage : UserControl
{
    /// <summary>列表为空时的占位文案。</summary>
    private const string EmptyHintText = "该版本暂无可用版本";

    /// <summary>列表正在拉取时的占位文案。</summary>
    private const string LoadingHintText = "正在加载中";

    private InstallPageViewModel? _viewModel;

    /// <summary>版本切换令牌：连续切版本时用来丢弃过期那次加载的结果。</summary>
    private int _loadToken;

    /// <summary>正在代码里改勾选状态，用来掐断 OnLoaderToggled 的重入。</summary>
    private bool _recalculating;

    public InstallPage()
    {
        InitializeComponent();

        // Avalonia 的 ToggleButton 没有 XAML 的 Checked/Unchecked 事件属性。
        // 四个框共用一个处理器：勾选状态一变就整体重算可用性，
        // 比原来「勾谁就手工关谁」的分散写法更好维护。
        checkBox1.IsCheckedChanged += (_, _) => OnLoaderToggled();
        checkBox2.IsCheckedChanged += (_, _) => OnLoaderToggled();
        checkBox3.IsCheckedChanged += (_, _) => OnLoaderToggled();
        checkBox4.IsCheckedChanged += (_, _) => OnLoaderToggled();

        WeakReferenceMessenger.Default.Register<string, string>(this, "McVersion", (_, m) => SetMcVersion(m));
    }

    /// <summary>
    /// 任一勾选框状态变化后的统一处理：清掉没勾选的加载器已选版本、
    /// 重算四个框的可用性、点亮卡片边框。
    /// </summary>
    private void OnLoaderToggled()
    {
        if (_recalculating)
            return;

        if (_viewModel is not InstallPageViewModel vm)
            return;

        // 取消勾选 → 同步清空已选版本，避免「框没勾却还带着版本号」被当成要装加载器
        if (checkBox1.IsChecked != true) vm.SelectForgeVersion = string.Empty;
        if (checkBox2.IsChecked != true) vm.SelectFabricVersion = string.Empty;
        if (checkBox3.IsChecked != true) vm.SelectNeoforgeVersion = string.Empty;
        if (checkBox4.IsChecked != true) vm.SelectQuiltVersion = string.Empty;

        UpdateLoaderAvailability();
        UpdateCardStates();
    }

    private async void SetMcVersion(string msg)
    {
        // 记下「这是第几次切换」。await 期间用户可能又点了别的版本，
        // 回来时若令牌已变，就不要再拿旧结果去改界面。
        var token = ++_loadToken;

        var vm = new InstallPageViewModel(msg);

        // 换 VM 时先退订旧的：否则被替换掉的 VM 仍会继续触发
        // PropertyChanged，用过期数据刷新占位文案。
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = vm;

        AppServices_Notify($"正在安装版本 {msg}");

        // 加载前重置勾选状态。四个框的可用性交给 UpdateLoaderAvailability 统一决定：
        // 此刻版本列表还没拉回来，它们会一起变成禁用态（加载完再放开有货的那几个）。
        checkBox1.IsChecked = checkBox2.IsChecked = checkBox3.IsChecked = checkBox4.IsChecked = false;
        UpdateLoaderAvailability();

        // 先把加载跑起来：Load() 会同步执行到内部第一个 await，
        // 此时 VM 的 IsLoadingVersions 已经置为 true，下面这次刷新就能让
        // 四张卡片当场显示「正在加载中」，而不是先闪一下上一个版本残留的文案。
        var loadTask = vm.Load();
        UpdateEmptyHints();

        await loadTask;

        if (token != _loadToken)
            return;

        UpdateLoaderAvailability();
        UpdateCardStates();
        UpdateEmptyHints();
    }

    private static void AppServices_Notify(string message)
        => Services.AppServices.Notifications?.Show(message, "提示", Services.NotificationSeverity.Informational);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 列表内容变化、或加载开关切换（开始拉取 / 拉取结束），都要重算占位文案与勾选框可用性。
        // 挂上 IsLoadingVersions 之后，点「刷新」也能立刻切到「正在加载中」并锁住勾选框。
        if (e.PropertyName is nameof(InstallPageViewModel.IsLoadingVersions))
        {
            UpdateLoaderAvailability();
            UpdateEmptyHints();
        }
        else if (e.PropertyName is nameof(InstallPageViewModel.ForgeVersions)
            or nameof(InstallPageViewModel.FabricVersions)
            or nameof(InstallPageViewModel.NeoforgeVersions)
            or nameof(InstallPageViewModel.QuiltVersions))
        {
            UpdateEmptyHints();
        }
    }

    /// <summary>
    /// 重算四个加载器勾选框的可用状态。优先级从高到低：
    /// <list type="number">
    /// <item>版本列表还没拉完 → 四个框全部禁用（加载好之前不给勾）；</item>
    /// <item>该加载器在这个版本下没有可选版本（404 / 列表为空）→ 禁用；</item>
    /// <item>共存互斥规则（沿用原 WPF 版语义）：
    /// Forge 挡住 Fabric、Quilt；Fabric 挡住其余全部；NeoForge 挡住 Fabric、Quilt；
    /// Quilt 挡住其余全部。已被勾中的框始终保持可用，否则没法取消。</item>
    /// </list>
    /// </summary>
    private void UpdateLoaderAvailability()
    {
        if (_viewModel is not InstallPageViewModel vm)
            return;

        if (vm.IsLoadingVersions)
        {
            checkBox1.IsEnabled = checkBox2.IsEnabled = checkBox3.IsEnabled = checkBox4.IsEnabled = false;
            return;
        }

        // 有版本可选才允许勾
        bool forgeUsable = vm.ForgeAvailable && vm.ForgeVersions.Count > 0;
        bool fabricUsable = vm.FabricAvailable && vm.FabricVersions.Count > 0;
        bool neoUsable = vm.NeoforgeAvailable && vm.NeoforgeVersions.Count > 0;
        bool quiltUsable = vm.QuiltAvailable && vm.QuiltVersions.Count > 0;

        // 已勾中但已变不可用（例如点刷新后列表空了）→ 顺手取消勾选。
        // 关掉 _recalculating 防重入，勾选状态在下面重新读一次。
        _recalculating = true;
        if (checkBox1.IsChecked == true && !forgeUsable) checkBox1.IsChecked = false;
        if (checkBox2.IsChecked == true && !fabricUsable) checkBox2.IsChecked = false;
        if (checkBox3.IsChecked == true && !neoUsable) checkBox3.IsChecked = false;
        if (checkBox4.IsChecked == true && !quiltUsable) checkBox4.IsChecked = false;
        _recalculating = false;

        bool forgeOn = checkBox1.IsChecked == true;
        bool fabricOn = checkBox2.IsChecked == true;
        bool neoOn = checkBox3.IsChecked == true;
        bool quiltOn = checkBox4.IsChecked == true;

        checkBox1.IsEnabled = forgeOn || (forgeUsable && !fabricOn && !quiltOn);
        checkBox2.IsEnabled = fabricOn || (fabricUsable && !forgeOn && !neoOn && !quiltOn);
        checkBox3.IsEnabled = neoOn || (neoUsable && !fabricOn && !quiltOn);
        checkBox4.IsEnabled = quiltOn || (quiltUsable && !forgeOn && !fabricOn && !neoOn);
    }

    /// <summary>
    /// 刷新四张卡片的空列表占位文案：
    /// 正在拉取 → 「正在加载中」；拉取结束仍然为空 → 「该版本暂无可用版本」。
    /// </summary>
    private void UpdateEmptyHints()
    {
        if (_viewModel is null)
            return;

        var loading = _viewModel.IsLoadingVersions;
        UpdateEmptyHint(forgeEmpty, _viewModel.ForgeVersions.Count, loading);
        UpdateEmptyHint(fabricEmpty, _viewModel.FabricVersions.Count, loading);
        UpdateEmptyHint(neoforgeEmpty, _viewModel.NeoforgeVersions.Count, loading);
        UpdateEmptyHint(quiltEmpty, _viewModel.QuiltVersions.Count, loading);
    }

    private static void UpdateEmptyHint(TextBlock hint, int count, bool loading)
    {
        if (count > 0)
        {
            hint.IsVisible = false;
            return;
        }

        hint.Text = loading ? LoadingHintText : EmptyHintText;
        hint.IsVisible = true;
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

}
