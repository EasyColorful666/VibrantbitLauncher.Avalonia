using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MineJavaEntry = MinecraftLaunch.Base.Models.Game.JavaEntry;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 开机向导第 2 步：游戏目录 / Java / 主题。
/// 直接复用 SettingsPageViewModel，保证与「设置」页的行为完全一致。
/// </summary>
public partial class WelcomeSetupPage : UserControl
{
    private readonly SettingsPageViewModel _viewModel;

    public WelcomeSetupPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(SettingsPageViewModel)) as SettingsPageViewModel
                     ?? new SettingsPageViewModel();

        DataContext = _viewModel;
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (_viewModel is INavigationAware aware)
                await aware.OnNavigatedToAsync();
        };
    }

    private async void OnSelectFolder(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Minecraft 文件夹",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            _viewModel.MinecraftFolder = path;
    }

    private async void OnBrowseJava(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 java.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java 可执行文件") { Patterns = new[] { "java.exe", "javaw.exe" } },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        var entry = new MineJavaEntry
        {
            JavaPath = path,
            Is64bit = true
        };

        _viewModel.JavaEntries.Add(entry);
        _viewModel.SelectedJava = entry;
    }

    private void OnBack(object? sender, RoutedEventArgs e)
        => AppNavigation.Navigate(typeof(WelcomePage));

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        SettingsService.Save();
        AppNavigation.Navigate(typeof(WelcomeAccountPage));
    }
}
