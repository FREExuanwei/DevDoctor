using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DevDoctor.App.ViewModels;
namespace DevDoctor.App.Views;
public partial class IssuesView : UserControl
{
    private MainViewModel? _viewModel;
    private DispatcherOperation? _pending;
    private bool _needsFocus;
    public IssuesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { if (IsLoaded) Attach(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) { _needsFocus = true; QueueFocus(); } };
    }
    private void Attach()
    {
        Detach(); _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += Changed;
        IssueItems.ItemContainerGenerator.StatusChanged += ContainersChanged;
        _needsFocus = true; QueueFocus();
    }
    private void Detach()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= Changed;
        IssueItems.ItemContainerGenerator.StatusChanged -= ContainersChanged;
        _viewModel = null; _pending?.Abort(); _pending = null; _needsFocus = false;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.FocusedFinding) or nameof(MainViewModel.CurrentPage)) _needsFocus = true;
        if (e.PropertyName is nameof(MainViewModel.FocusedFinding) or nameof(MainViewModel.VisibleIssues) or nameof(MainViewModel.CurrentPage)) QueueFocus();
    }
    private void ContainersChanged(object? sender, EventArgs e)
    {
        if (IssueItems.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) QueueFocus();
    }
    private void QueueFocus()
    {
        if (!_needsFocus || !IsLoaded || !IsVisible || _viewModel?.CurrentPage != "Issues" || _viewModel.FocusedFinding is null) return;
        _pending?.Abort();
        _pending = Dispatcher.InvokeAsync(() => {
            _pending = null;
            if (!IsLoaded || !IsVisible || _viewModel?.CurrentPage != "Issues" || _viewModel.FocusedFinding is not { } finding) return;
            if (IssueItems.ItemContainerGenerator.ContainerFromItem(finding) is FrameworkElement item) {
                _needsFocus = false;
                item.BringIntoView(); item.Focus(); Keyboard.Focus(item);
            }
        }, DispatcherPriority.Loaded);
    }
}
