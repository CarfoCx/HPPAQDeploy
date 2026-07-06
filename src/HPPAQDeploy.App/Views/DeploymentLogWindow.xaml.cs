using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HPPAQDeploy.App.ViewModels;

namespace HPPAQDeploy.App.Views;

public partial class DeploymentLogWindow : Window
{
    private DeploymentLogWindowViewModel? _viewModel;

    public DeploymentLogWindow()
    {
        InitializeComponent();
        DataContextChanged += (s, e) => _viewModel = DataContext as DeploymentLogWindowViewModel;
    }

    // Scrolling logic is now handled via XAML or can be added back if needed,
    // but with TabControl, we need to find the ScrollViewer in the active template.
    // For now, focusing on the multi-tab architecture.
}
