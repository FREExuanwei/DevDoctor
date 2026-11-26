using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;

namespace DevDoctor.App;

public partial class MainWindow : Window
{
    private bool _closingAfterCancel;
    private bool _restoreAppearanceFocus;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        // WorkArea is in WPF device-independent units, so startup also fits a high-DPI screen.
        var available = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, available.Width); MinHeight = Math.Min(MinHeight, available.Height);
        Width = Math.Min(Width, available.Width); Height = Math.Min(Height, available.Height);
        viewModel.PropertyChanged += (_, args) => {
            if (args.PropertyName != nameof(viewModel.CurrentPage)) return;
            MainScroll.ScrollToTop();
            if (viewModel.CurrentPage == "Search") _ = Dispatcher.InvokeAsync(CurrentSearchView.FocusQuery, DispatcherPriority.Input);
        };
        AppearancePopup.CustomPopupPlacementCallback = (size, _, _) => {
            var (anchor, measured) = MeasureAppearance();
            // WPF gives the callback screen pixels. Window and work-area geometry uses DIPs.
            var dpi = VisualTreeHelper.GetDpi(this);
            var popupSize = new Size(size.Width / dpi.DpiScaleX, size.Height / dpi.DpiScaleY);
            var offset = AppearancePopupLayout.Position(measured.Clip, anchor, popupSize);
            return [new(new Point(offset.X * dpi.DpiScaleX, offset.Y * dpi.DpiScaleY), PopupPrimaryAxis.None)];
        };
        viewModel.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(viewModel.IsAppearanceOpen) && viewModel.IsAppearanceOpen) UpdateAppearanceBounds();
        };
        viewModel.ThemePreviewsRefreshing += (_, _) => PreserveQuickThemeFocus(viewModel);
        SizeChanged += (_, _) => viewModel.TryDismissAppearance();
        LocationChanged += (_, _) => viewModel.TryDismissAppearance();
    }
    private (Rect Anchor, (double Width, double Height, Rect Clip) Measured) MeasureAppearance()
    {
        var anchor = AppearanceButton.TransformToAncestor(this).TransformBounds(new Rect(AppearanceButton.RenderSize));
        var measured = AppearancePopupLayout.Measure(new(0, 38, ActualWidth, Math.Max(0, ActualHeight - 68)),
            AppearancePopupLayout.WorkAreaFor(this), anchor, (DataContext as MainViewModel)?.UiScale ?? 1);
        return (anchor, measured);
    }
    private void UpdateAppearanceBounds()
    {
        var (_, measured) = MeasureAppearance();
        AppearancePanel.Width = measured.Width; AppearancePanel.MaxHeight = measured.Height;
    }
    private void PreserveQuickThemeFocus(MainViewModel viewModel)
    {
        if (!AppearancePopup.IsOpen || Keyboard.FocusedElement is not Button focused || !ReferenceEquals(focused.Command, viewModel.SelectThemePresetCommand) ||
            focused.CommandParameter is not string value || !AppearanceSettings.Presets.Contains(value)) return;
        _ = Dispatcher.InvokeAsync(() => {
            if (!AppearancePopup.IsOpen) return;
            if (Keyboard.FocusedElement is RadioButton) return;
            FindThemeButton(AppearancePanel, value)?.Focus();
        }, DispatcherPriority.Input);
    }
    private static Button? FindThemeButton(DependencyObject parent, object? value)
    {
        if (value is not string preset || !AppearanceSettings.Presets.Contains(preset)) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button button && Equals(button.CommandParameter, preset)) return button;
            if (FindThemeButton(child, preset) is { } found) return found;
        }
        return null;
    }
    private void AppearancePopupOpened(object sender, EventArgs e)
    {
        UpdateAppearanceBounds();
        _ = Dispatcher.BeginInvoke(new Action(() => { if (AppearancePopup.IsOpen) AppearancePanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); }));
    }
    private void AppearancePopupClosed(object sender, EventArgs e)
    {
        if (_restoreAppearanceFocus) { _restoreAppearanceFocus = false; AppearanceButton.Focus(); }
    }
    private void AppearancePanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel vm || !vm.IsAppearanceOpen) return;
        _restoreAppearanceFocus = true; vm.TryDismissAppearance(); e.Handled = true;
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && DataContext is MainViewModel searchVm)
        {
            searchVm.ShowSearchCommand.Execute(null);
            _ = Dispatcher.InvokeAsync(CurrentSearchView.FocusQuery, DispatcherPriority.Input);
            e.Handled = true;
        }
        if (e.Key == Key.Escape && DataContext is MainViewModel vm && vm.IsAppearanceOpen)
        { _restoreAppearanceFocus = true; vm.TryDismissAppearance(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void SearchClick(object sender, RoutedEventArgs e) => _ = Dispatcher.InvokeAsync(CurrentSearchView.FocusQuery, DispatcherPriority.Input);
    private void MaximizeClick(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_closingAfterCancel && DataContext is MainViewModel vm)
        {
            e.Cancel = true; base.OnClosing(e); IsEnabled = false; vm.Cancel();
            vm.TryDismissAppearance();
            // Let an active restore finish its commit or rollback and drain preference writes before disposing services.
            try { await vm.WaitForIdleAsync(); } catch (OperationCanceledException) { }
            _closingAfterCancel = true; _ = Dispatcher.BeginInvoke(new Action(Close)); return;
        }
        base.OnClosing(e);
    }
}
