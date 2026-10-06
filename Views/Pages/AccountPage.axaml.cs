using Avalonia.Controls;
using System.Collections.Specialized;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class AccountPage : UserControl
{
    private readonly AccountPageViewModel? _viewModel;

    public AccountPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(AccountPageViewModel)) as AccountPageViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (_viewModel != null && !_viewModel.IsLoaded)
            {
                _viewModel.Load();
                _viewModel.IsLoaded = true;
            }

            UpdateEmptyState();

            // 账户增删后同步空状态
            if (_viewModel != null)
                _viewModel.Users.CollectionChanged += OnUsersChanged;
        };
    }

    private void OnUsersChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
        => emptyState.IsVisible = (_viewModel?.Users.Count ?? 0) == 0;
}
