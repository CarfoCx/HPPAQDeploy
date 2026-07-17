using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HPPAQDeploy.App.ViewModels;

namespace HPPAQDeploy.App.Views;

public partial class DeploymentLogWindow : Window
{
    private DeploymentLogWindowViewModel? _viewModel;
    private readonly Dictionary<ListBox, INotifyCollectionChanged> _logSources = [];
    private readonly HashSet<ListBox> _pendingAutoScroll = [];

    public DeploymentLogWindow()
    {
        InitializeComponent();
        DataContextChanged += (s, e) => _viewModel = DataContext as DeploymentLogWindowViewModel;
    }

    private void LogList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list || list.ItemsSource is not INotifyCollectionChanged source)
            return;

        if (_logSources.TryGetValue(list, out var previousSource))
        {
            if (ReferenceEquals(previousSource, source))
                return;

            CollectionChangedEventManager.RemoveHandler(previousSource, OnLogCollectionChanged);
        }

        _logSources[list] = source;
        CollectionChangedEventManager.AddHandler(source, OnLogCollectionChanged);
    }

    private void LogList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list || !_logSources.Remove(list, out var source))
            return;

        _pendingAutoScroll.Remove(list);
        CollectionChangedEventManager.RemoveHandler(source, OnLogCollectionChanged);
    }

    private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll != true ||
            e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
            return;

        var affectedLists = _logSources
            .Where(pair => ReferenceEquals(pair.Value, sender))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var list in affectedLists)
        {
            if (!_pendingAutoScroll.Add(list))
                continue;

            list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _pendingAutoScroll.Remove(list);
                if (list.Items.Count > 0)
                    list.ScrollIntoView(list.Items[0]);
            }));
        }
    }
}
