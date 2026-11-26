using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Threading;
using DevDoctor.App.ViewModels;

namespace DevDoctor.App.Views;
public partial class ProjectView : UserControl
{
    private MainViewModel? _main;
    private ProjectViewModel? _project;
    private DispatcherOperation? _pending;
    public ProjectView()
    {
        InitializeComponent(); Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { if (IsLoaded) Attach(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueDetails(); };
    }
    private void Attach()
    {
        Detach(); _main = DataContext as MainViewModel; _project = _main?.Project;
        if (_main is not null) _main.PropertyChanged += Changed;
        if (_project is not null) _project.PropertyChanged += Changed;
        QueueDetails();
    }
    private void Detach()
    {
        if (_main is not null) _main.PropertyChanged -= Changed;
        if (_project is not null) _project.PropertyChanged -= Changed;
        _main = null; _project = null; _pending?.Abort(); _pending = null;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectViewModel.SelectedMatch) or nameof(MainViewModel.CurrentPage)) QueueDetails();
    }
    private void MatchClick(object sender, RoutedEventArgs e) => QueueDetails();
    private void QueueDetails()
    {
        if (!IsLoaded || !IsVisible || _project?.SelectedMatch is null) return;
        _pending?.Abort(); _pending = Dispatcher.InvokeAsync(() => {
            _pending = null;
            if (IsLoaded && IsVisible && _project?.SelectedMatch is not null) Details.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
