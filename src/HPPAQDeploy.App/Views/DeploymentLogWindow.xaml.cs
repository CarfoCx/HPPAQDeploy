using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HPPAQDeploy.App.ViewModels;

namespace HPPAQDeploy.App.Views;

public partial class DeploymentLogWindow : Window
{
    private DeploymentLogWindowViewModel? _viewModel;
    private ScrollViewer? _scrollViewer;

    public DeploymentLogWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as DeploymentLogWindowViewModel;
        _viewModel?.Subscribe();

        // Find the ScrollViewer inside the ItemsControl template after it renders
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _scrollViewer = FindScrollViewer(LogItemsControl);

            if (_viewModel != null)
            {
                _viewModel.FilteredDeploymentLog.CollectionChanged += OnLogCollectionChanged;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.FilteredDeploymentLog.CollectionChanged -= OnLogCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Unsubscribe();
        }
    }

    private void OnViewModelPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeploymentLogWindowViewModel.FilteredDeploymentLog) && _viewModel != null)
        {
            // Re-subscribe to the new collection instance
            _viewModel.FilteredDeploymentLog.CollectionChanged -= OnLogCollectionChanged;
            _viewModel.FilteredDeploymentLog.CollectionChanged += OnLogCollectionChanged;
        }
    }

    private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll == true && _scrollViewer != null)
        {
            // Entries are inserted at position 0 (newest first), so scroll to top
            _scrollViewer.ScrollToTop();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv)
                return sv;
            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
    }
}
