using System.Collections.Specialized;
using Avalonia.Controls;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class DownloadCenterPage : UserControl
{
    private readonly DownloadCenterViewModel? _viewModel;

    public DownloadCenterPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(DownloadCenterViewModel)) as DownloadCenterViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += (_, _) => UpdateEmptyState();

        if (_viewModel is not null)
            _viewModel.Tasks.CollectionChanged += OnTasksChanged;
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateEmptyState();

    private void UpdateEmptyState()
        => emptyText.IsVisible = _viewModel is null || _viewModel.Tasks.Count == 0;
}
