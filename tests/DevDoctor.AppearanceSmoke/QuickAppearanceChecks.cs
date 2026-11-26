using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;

namespace DevDoctor.AppearanceSmoke;

internal static partial class Program
{
    private static async Task VerifyQuickAppearanceAsync(Window window, MainViewModel vm, string output, List<string> checks,
        bool windowLayoutOnly = false)
    {
        vm.ScalePercent = 100; window.Width = Math.Min(1340, SystemParameters.WorkArea.Width); window.Height = Math.Min(900, SystemParameters.WorkArea.Height);
        window.Activate(); vm.NavigateCommand.Execute("Overview");
        var toggle = (Button)window.FindName("AppearanceButton");
        var popup = (Popup)window.FindName("AppearancePopup");
        var panel = (FrameworkElement)window.FindName("AppearancePanel");
        var report = vm.LatestReport; var projectReport = vm.Project?.Report; var projectFilter = vm.Project?.KindFilter;
        var savedPriority = vm.IssuePriorityFilter; var savedModule = vm.IssueModuleFilter; var savedStatus = vm.IssueStatusFilter;
        var matrix = new List<object>();
        void ObservePopup(string stage)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            var ownerHandle = new WindowInteropHelper(window).Handle; GetWindowThreadProcessId(ownerHandle, out var ownerPid);
            File.AppendAllText(Path.Combine(output, "popup-events.jsonl"), JsonSerializer.Serialize(new {
                stage, utc = DateTimeOffset.UtcNow, foregroundPid = pid, ownedPid = Environment.ProcessId,
                ownerPid, ownerHandle = ownerHandle.ToInt64(), popupHandle = (PresentationSource.FromVisual(panel) as HwndSource)?.Handle.ToInt64(),
                active = window.IsActive, vm.IsAppearanceOpen, nativePopupOpen = popup.IsOpen, panel.IsVisible,
                vm.CurrentPage, vm.ThemePreset, vm.AppearanceMode, vm.InterfaceStyle,
                window.Left, window.Top, window.ActualWidth, window.ActualHeight,
                attached = PresentationSource.FromVisual(panel) is not null,
                stack = stage == "popup-Closed" ? Environment.StackTrace : null
            }) + Environment.NewLine);
        }
        popup.Opened += (_, _) => ObservePopup("popup-Opened"); popup.Closed += (_, _) => ObservePopup("popup-Closed");
        window.Activated += (_, _) => ObservePopup("main-Activated"); window.Deactivated += (_, _) => ObservePopup("main-Deactivated");
        window.SizeChanged += (_, _) => ObservePopup("main-SizeChanged"); window.LocationChanged += (_, _) => ObservePopup("main-LocationChanged");
        var observeKeyboard = false;
        void ObserveFocus(string stage)
        {
            if (!observeKeyboard) return;
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            var focused = Keyboard.FocusedElement;
            var record = new { stage, utc = DateTimeOffset.UtcNow, vm.CurrentPage, active = window.IsActive, popup.IsOpen,
                panel.IsKeyboardFocusWithin, foregroundPid = pid, ownedPid = Environment.ProcessId,
                focusedType = focused?.GetType().Name,
                automationName = focused is DependencyObject d ? System.Windows.Automation.AutomationProperties.GetName(d) : "",
                commandParameter = (focused as Button)?.CommandParameter?.ToString(),
                isThemeCommand = ReferenceEquals((focused as Button)?.Command, vm.SelectThemePresetCommand),
                content = (focused as Button)?.Content?.ToString(), dataContext = (focused as FrameworkElement)?.DataContext?.GetType().Name,
                vm.ThemePreset, vm.AppearanceMode, vm.InterfaceStyle };
            File.AppendAllText(Path.Combine(output, "keyboard-focus-events.jsonl"), JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        vm.ThemePreviewsRefreshing += (_, _) => ObserveFocus("ThemePreviewsRefreshing-after-MainWindow-handler");
        vm.PropertyChanged += (_, args) => {
            if (!observeKeyboard || args.PropertyName != nameof(vm.ThemePreviews)) return;
            ObserveFocus("ThemePreviews-PropertyChanged");
            foreach (var priority in new[] { System.Windows.Threading.DispatcherPriority.Loaded, System.Windows.Threading.DispatcherPriority.Input, System.Windows.Threading.DispatcherPriority.ContextIdle })
                _ = window.Dispatcher.InvokeAsync(() => ObserveFocus("after-ThemePreviews-" + priority), priority);
        };
        async Task Open()
        {
            if (!vm.IsAppearanceOpen) InvokeButton(toggle);
            await SettleAsync(window);
            if (!popup.IsOpen || !panel.IsVisible) { ObservePopup("Open-assertion-failed"); throw new InvalidOperationException("The actual top appearance popup did not open; see popup-events.jsonl for owned/foreground PID and native/bound state."); }
        }
        if (windowLayoutOnly)
        {
            vm.AppearanceMode = "Light";
            vm.InterfaceStyle = "SoftCards";
            vm.NavigateCommand.Execute("Settings");
            await VerifyQuickWindowLayoutsAsync(window, vm, panel, output, checks, Open);
            return;
        }

        await Open(); await CaptureQuickOverlayAsync(window, panel, output, "quick-initial");
        foreach (var preset in AppearanceSettings.Presets)
        foreach (var mode in AppearanceSettings.Modes)
        foreach (var style in AppearanceSettings.Styles)
        {
            await Open();
            var watch = Stopwatch.StartNew();
            var card = Descendants<Button>(panel).Single(b => ReferenceEquals(b.Command, vm.SelectThemePresetCommand) && Equals(b.CommandParameter, preset));
            InvokeButton(card); await SettleAsync(window);
            SelectQuickRadio(panel, mode, vm.SelectAppearanceModeCommand); await SettleAsync(window);
            if (vm.AppearanceMode != mode) throw new InvalidOperationException("Radio accessibility selection did not synchronize display mode to the shared view model.");
            SelectQuickRadio(panel, style, vm.SelectInterfaceStyleCommand); await SettleAsync(window);
            if (vm.ThemePreset != preset || vm.InterfaceStyle != style) throw new InvalidOperationException("The quick appearance selection did not update the shared state.");
            if (!ReferenceEquals(report, vm.LatestReport) || !ReferenceEquals(projectReport, vm.Project?.Report) || vm.Project?.KindFilter != projectFilter
                || vm.IssuePriorityFilter != savedPriority || vm.IssueModuleFilter != savedModule || vm.IssueStatusFilter != savedStatus)
                throw new InvalidOperationException("Quick appearance discarded evidence or filters.");
            AssertQuickBounds(window, panel); AssertWindowColors(window);
            AssertChineseAppearance(window, panel, vm);
            matrix.Add(new { preset, mode, style, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, pixelsPerDip = VisualTreeHelper.GetDpi(window).PixelsPerDip });
            await File.WriteAllTextAsync(Path.Combine(output, "quick-matrix-progress.json"), JsonSerializer.Serialize(matrix));
            if (mode == "System") await CaptureQuickOverlayAsync(window, panel, output, $"quick-{preset}-{mode}-{style}");
        }
        if (matrix.Count != 54) throw new InvalidOperationException("The top quick appearance matrix is incomplete.");
        await File.WriteAllTextAsync(Path.Combine(output, "quick-matrix.json"), JsonSerializer.Serialize(matrix, new JsonSerializerOptions { WriteIndented = true }));
        checks.Add("PASS: actual top preview buttons and native radio selection providers applied all 54 theme/mode/style choices in the same production window without losing evidence or filters.");
        checks.Add("PASS: all 54 quick choices assert exact Chinese names for six theme cards, three style radios, live Chinese summary, wrapping labels and Chinese font fallback.");

        // Window messages target this owned production popup, never another application's input queue.
        observeKeyboard = true; ObserveFocus("before-keyboard-existing-test");
        await Open(); toggle.Focus(); panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        await SettleAsync(window);
        var popupHandle = ((HwndSource)PresentationSource.FromVisual(panel)).Handle;
        PostKey(popupHandle, 0x09); await SettleAsync(window);
        if (!panel.IsKeyboardFocusWithin) throw new InvalidOperationException("Tab left the quick panel unexpectedly.");
        var keyboardCard = Descendants<Button>(panel).Single(b => ReferenceEquals(b.Command, vm.SelectThemePresetCommand) && Equals(b.CommandParameter, "FluentBlue"));
        keyboardCard.Focus(); ObserveFocus("after-existing-card-Focus-before-Enter"); PostKey(popupHandle, 0x0D); await SettleAsync(window); ObserveFocus("after-Enter-settle");
        GetWindowThreadProcessId(GetForegroundWindow(), out var keyboardForegroundPid);
        if (vm.ThemePreset != "FluentBlue" || !panel.IsKeyboardFocusWithin || Keyboard.FocusedElement is not Button focusedTheme || !Equals(focusedTheme.CommandParameter, "FluentBlue"))
            throw new InvalidOperationException($"Keyboard theme selection lost focus when preview cards refreshed. preset={vm.ThemePreset}; panelFocus={panel.IsKeyboardFocusWithin}; focused={Keyboard.FocusedElement?.GetType().Name}; parameter={(Keyboard.FocusedElement as Button)?.CommandParameter}; name={(Keyboard.FocusedElement is DependencyObject focusedObject ? System.Windows.Automation.AutomationProperties.GetName(focusedObject) : "")}; content={(Keyboard.FocusedElement as Button)?.Content}; dataContext={(Keyboard.FocusedElement as FrameworkElement)?.DataContext?.GetType().Name}; popup={popup.IsOpen}; page={vm.CurrentPage}; active={window.IsActive}; foreground={GetForegroundWindow()}; foregroundPid={keyboardForegroundPid}; ownedPid={Environment.ProcessId}; ownedMain={new WindowInteropHelper(window).Handle}.");
        PostKey(popupHandle, 0x1B); await SettleAsync(window);
        if (vm.IsAppearanceOpen || !toggle.IsKeyboardFocusWithin) throw new InvalidOperationException("Escape did not close the popup and return focus to its trigger.");
        checks.Add("PASS: controlled native Tab/Enter/Escape messages selected a theme, retained choice focus after preview regeneration, dismissed the actual popup and restored trigger focus.");
        observeKeyboard = false;

        await Open(); await ClickOutsideOwnedPopupAsync(window); await SettleAsync(window);
        if (vm.IsAppearanceOpen) throw new InvalidOperationException("A real outside mouse click did not close the popup.");
        checks.Add("PASS: a guarded real mouse click inside the owned main window but outside its popup dismissed the panel.");

        await Open();
        InvokeButton(Descendants<Button>(panel).Single(b => ReferenceEquals(b.Command, vm.OpenAppearanceSettingsCommand)));
        await SettleAsync(window);
        if (vm.CurrentPage != "Settings" || vm.IsAppearanceOpen) throw new InvalidOperationException("The full appearance settings link did not navigate and dismiss the popup.");
        var modeBox = Descendants<ComboBox>(window).Single(c => c.IsVisible && ReferenceEquals(c.ItemsSource, vm.AppearanceModeOptions));
        var styleBox = Descendants<ComboBox>(window).Single(c => c.IsVisible && ReferenceEquals(c.ItemsSource, vm.InterfaceStyleOptions));
        foreach (var style in ChineseStyles)
        {
            styleBox.SetCurrentValue(Selector.SelectedValueProperty, style.Key); await SettleAsync(window);
            if (!Descendants<TextBlock>(styleBox).Any(t => t.IsVisible && t.Text == style.Value))
                throw new InvalidOperationException("Actual Settings ComboBox selected content is not the exact Chinese style: " + style.Value);
        }
        modeBox.SetCurrentValue(Selector.SelectedValueProperty, "Light"); styleBox.SetCurrentValue(Selector.SelectedValueProperty, "SoftCards");
        await SettleAsync(window); await Open();
        if (vm.AppearanceMode != "Light" || vm.InterfaceStyle != "SoftCards"
            || !Descendants<RadioButton>(panel).Single(r => Equals(r.CommandParameter, "Light")).IsChecked.GetValueOrDefault()
            || !Descendants<RadioButton>(panel).Single(r => Equals(r.CommandParameter, "SoftCards")).IsChecked.GetValueOrDefault())
            throw new InvalidOperationException("Settings-to-quick-panel selection did not synchronize.");
        await vm.FlushAppearanceAsync();
        if (vm.Reports is null) throw new InvalidOperationException("Production reports services missing.");
        checks.Add("PASS: full settings controls and top panel share live mode/style selection and queued persistence.");

        await VerifyQuickWindowLayoutsAsync(window, vm, panel, output, checks, Open);
    }

    private static async Task VerifyQuickWindowLayoutsAsync(Window window, MainViewModel vm, FrameworkElement panel,
        string output, List<string> checks, Func<Task> open)
    {
        var expected = vm.CaptureCurrentSettings();
        foreach (var scale in new[] { 100, 125, 150 })
        {
            vm.TryDismissAppearance(); window.Width = Math.Min(900, SystemParameters.WorkArea.Width); window.Height = Math.Min(640, SystemParameters.WorkArea.Height);
            vm.ScalePercent = scale; await SettleAsync(window); await open(); AssertQuickBounds(window, panel);
            await CaptureQuickOverlayAsync(window, panel, output, $"quick-small-window-{scale}");
            var scroll = Descendants<ScrollViewer>(panel).Single();
            scroll.ScrollToBottom(); await SettleAsync(window);
            var bottomStyle = Descendants<RadioButton>(panel).Single(r => Equals(r.CommandParameter, "CompactPro"));
            var bottomBounds = bottomStyle.TransformToAncestor(scroll).TransformBounds(new Rect(bottomStyle.RenderSize));
            if (bottomBounds.Top < -2 || bottomBounds.Bottom > scroll.ViewportHeight + 2 || bottomBounds.Right > scroll.ViewportWidth + 2)
                throw new InvalidOperationException($"The bottom style is clipped after scrolling at app scale {scale}: {bottomBounds}.");
            if (scale == 150) await CaptureQuickOverlayAsync(window, panel, output, "quick-small-window-150-bottom");
        }
        vm.TryDismissAppearance(); vm.ScalePercent = 100; window.WindowState = WindowState.Maximized;
        await SettleAsync(window); await open(); AssertQuickBounds(window, panel);
        await CaptureQuickOverlayAsync(window, panel, output, "quick-maximized");
        vm.TryDismissAppearance(); window.WindowState = WindowState.Normal;
        checks.Add("PASS: quick popup remains inside actual main-window/monitor bounds in small logical windows, app scales100/125/150 and actual maximized state; OS DPI was not modified.");

        // Use a real environment scan and perform appearance work while its task is active.
        window.Width = Math.Min(1340, SystemParameters.WorkArea.Width); window.Height = Math.Min(900, SystemParameters.WorkArea.Height);
        vm.NavigateCommand.Execute("Overview");
        await SettleAsync(window);
        var running = vm.RunAllCommand.ExecuteAsync(null);
        if (!vm.IsRunning) throw new InvalidOperationException("Actual environment scan did not enter running state.");
        await open();
        var duringScan = vm.IsRunning;
        InvokeButton(Descendants<Button>(panel).Single(b => ReferenceEquals(b.Command, vm.SelectThemePresetCommand) && Equals(b.CommandParameter, "Aurora")));
        await SettleAsync(window);
        if (!duringScan) throw new InvalidOperationException("Scan finished too soon to verify theme switching during actual work.");
        PostKey(((HwndSource)PresentationSource.FromVisual(panel)).Handle, 0x1B); await SettleAsync(window);
        await running;
        if (vm.IsAppearanceOpen || vm.LatestReport?.Results.Count != 10 || vm.LatestReport.WasCancelled || vm.ThemePreset != "Aurora")
            throw new InvalidOperationException("Appearance/Escape did not close only the popup while retaining the completed scan and theme selection.");
        checks.Add("PASS: actual ten-module local scan continued while top theme changed and Escape closed only its popup.");
        await vm.ApplySettingsAsync(expected); await vm.FlushAppearanceAsync();
        await VerifyV12LogicalLayoutsAsync(window, vm, output, checks);
    }

    private static void InvokeButton(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static void SelectQuickRadio(FrameworkElement panel, string value, object command)
    {
        var radio = Descendants<RadioButton>(panel).Single(r => ReferenceEquals(r.Command, command) && Equals(r.CommandParameter, value));
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
    }
    private static Rect GetQuickPopupBounds(Window window, FrameworkElement panel)
    {
        var origin = window.PointToScreen(new Point());
        var topLeft = panel.PointToScreen(new Point());
        var bottomRight = panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(new Point((topLeft.X - origin.X) / dpi.DpiScaleX, (topLeft.Y - origin.Y) / dpi.DpiScaleY),
            new Point((bottomRight.X - origin.X) / dpi.DpiScaleX, (bottomRight.Y - origin.Y) / dpi.DpiScaleY));
    }
    private static void AssertQuickBounds(Window window, FrameworkElement panel)
    {
        var rect = GetQuickPopupBounds(window, panel);
        if (rect.Left < -2 || rect.Top < 36 || rect.Right > window.ActualWidth + 2 || rect.Bottom > window.ActualHeight - 25 + 2)
            throw new InvalidOperationException($"Quick popup exceeds the actual window: {rect}, main={window.ActualWidth}×{window.ActualHeight}.");
        var workArea = AppearancePopupLayout.WorkAreaFor(window);
        workArea.Inflate(2, 2);
        if (!workArea.Contains(rect)) throw new InvalidOperationException($"Quick popup exceeds the actual monitor work area: {rect} vs {workArea}.");
    }
    private static async Task CaptureQuickOverlayAsync(Window window, FrameworkElement panel, string output, string name)
    {
        await SettleAsync(window);
        if (PresentationSource.FromVisual(panel) is null)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
            throw new InvalidOperationException($"Quick popup detached before capture {name}: active={window.IsActive}; foregroundPid={foregroundPid}; ownedPid={Environment.ProcessId}; popupOpen={(window.FindName("AppearancePopup") as Popup)?.IsOpen}; page={(window.DataContext as MainViewModel)?.CurrentPage}.");
        }
        var dpi = VisualTreeHelper.GetDpi(window);
        var popupBounds = GetQuickPopupBounds(window, panel);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) {
            drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(panel), null, popupBounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        if (window.DataContext is MainViewModel vm) Captures.Add(new(name + ".png", vm.ThemePreset, vm.AppearanceMode, vm.Theme, vm.InterfaceStyle, vm.ScalePercent, vm.CurrentPage, window.ActualWidth, window.ActualHeight, dpi.PixelsPerDip));
    }

    private static void PostKey(nint ownedHandle, int key)
    { PostMessage(ownedHandle, 0x100, (nint)key, (nint)1); PostMessage(ownedHandle, 0x101, (nint)key, unchecked((nint)0xC0000001u)); }
    private static async Task ClickOutsideOwnedPopupAsync(Window window)
    {
        window.Activate();
        await SettleAsync(window);
        var point = window.PointToScreen(new Point(25, 65)); var handle = new WindowInteropHelper(window).Handle;
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
        if (foregroundProcess != Environment.ProcessId) throw new InvalidOperationException("Cannot safely send a mouse click: the owned application is not foreground.");
        if (GetAncestor(WindowFromPoint(new NativePoint((int)point.X, (int)point.Y)), 2) != handle)
            throw new InvalidOperationException("Cannot safely click outside popup: target is not the owned main window.");
        GetCursorPos(out var previous);
        try {
            if (!SetCursorPos((int)point.X, (int)point.Y)) throw new InvalidOperationException("Controlled cursor move failed.");
            var inputs = new[] { new NativeInput { Type = 0, Data = new() { Mouse = new() { Flags = 2 } } }, new NativeInput { Type = 0, Data = new() { Mouse = new() { Flags = 4 } } } };
            if (SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) != 2) throw new InvalidOperationException("Controlled mouse click was not delivered.");
        } finally { SetCursorPos(previous.X, previous.Y); }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputUnion { [FieldOffset(0)] public NativeMouse Mouse; [FieldOffset(0)] public NativeKeyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int process);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
