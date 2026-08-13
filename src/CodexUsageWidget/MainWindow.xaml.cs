using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexUsageWidget.Core;
using Forms = System.Windows.Forms;

namespace CodexUsageWidget;

public partial class MainWindow : Window
{
    private const double CompactWidth = 204d;
    private const double CompactHeight = 76d;
    private const double ExpandedWidth = 368d;
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(180));

    private enum WidgetVisualState { Compact, AnimatingToExpanded, Expanded, AnimatingToCompact }

    private readonly MainViewModel _viewModel = new();
    private readonly CodexUsageScanner _scanner = CodexUsageScanner.ForCurrentUser();
    private readonly CodexAppServerClient _liveSource;
    private readonly UsageRefreshCoordinator _refreshCoordinator;
    private readonly SettingsStore _settingsStore = new();
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _scanTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _refreshSignalTimer;
    private readonly HoverIntentTracker _hoverIntent = new();
    private readonly RefreshSignalDebouncer _refreshSignalDebouncer = new(TimeSpan.FromMilliseconds(500));
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Drawing.Icon _trayIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _visibilityItem;
    private readonly Forms.ToolStripMenuItem _topmostItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private AppSettings _settings;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private WidgetVisualState _visualState = WidgetVisualState.Compact;
    private WidgetRect _compactRect;
    private bool _anchorRight;
    private bool _anchorBottom;
    private bool _allowClose;
    private bool _exiting;
    private long _transitionVersion;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _liveSource = new CodexAppServerClient(TimeSpan.FromSeconds(10));
        _liveSource.RateLimitsUpdated += LiveSource_RateLimitsUpdated;
        _refreshCoordinator = new UsageRefreshCoordinator(
            _liveSource,
            cancellationToken => Task.Run(() => _scanner.FindLatest(cancellationToken), cancellationToken));
        _settings = _settingsStore.Load();
        _settings.StartWithWindows = StartupManager.IsEnabled();
        Topmost = _settings.Topmost;

        _visibilityItem = new Forms.ToolStripMenuItem("隐藏悬浮窗", null, (_, _) => ToggleVisibility());
        _topmostItem = new Forms.ToolStripMenuItem("始终置顶", null, (_, _) => ToggleTopmost()) { Checked = Topmost };
        _startupItem = new Forms.ToolStripMenuItem("开机启动", null, (_, _) => ToggleStartup()) { Checked = _settings.StartWithWindows };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([
            _visibilityItem,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("立即刷新", null, async (_, _) => await RefreshAsync(collapseWhenFinished: true)),
            _topmostItem,
            _startupItem,
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("退出", null, (_, _) => ExitApplication())
        ]);

        _trayIcon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Codex 用量",
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);

        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _viewModel.Tick(), Dispatcher);
        _scanTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), Dispatcher);
        _collapseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Normal, CollapseTimer_Tick, Dispatcher);
        _refreshSignalTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, RefreshSignalTimer_Tick, Dispatcher);
        ConfigureWatchers();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreCompactPosition();
        ApplyCompactVisuals();
        _tickTimer.Start();
        _scanTimer.Start();
        await RefreshAsync(collapseWhenFinished: true);
    }

    private void RestoreCompactPosition()
    {
        var area = GetCurrentWorkArea();
        var requested = new WidgetRect(
            _settings.Left ?? area.Right - CompactWidth - 18,
            _settings.Top ?? area.Y + 18,
            CompactWidth,
            CompactHeight);
        _compactRect = WidgetPlacement.Clamp(requested, area);
        SetWindowRect(_compactRect);
    }

    private void ConfigureWatchers()
    {
        foreach (var root in _scanner.Roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnLogChanged;
                watcher.Created += OnLogChanged;
                watcher.Renamed += OnLogChanged;
                watcher.Error += (_, _) => Dispatcher.BeginInvoke(QueueRefresh);
                _watchers.Add(watcher);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
    }

    private void OnLogChanged(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(QueueRefresh);
    private void LiveSource_RateLimitsUpdated(object? sender, EventArgs e) => Dispatcher.BeginInvoke(QueueRefresh);

    private void QueueRefresh()
    {
        _refreshSignalDebouncer.Signal(DateTimeOffset.Now);
        _refreshSignalTimer.Start();
    }

    private async void RefreshSignalTimer_Tick(object? sender, EventArgs e)
    {
        if (!_refreshSignalDebouncer.TryConsume(DateTimeOffset.Now)) return;
        _refreshSignalTimer.Stop();
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool collapseWhenFinished = false)
    {
        try
        {
            var result = await _refreshCoordinator.RefreshAsync(_lifetimeCancellation.Token);
            _viewModel.Apply(result.Snapshot);
            if (result.Snapshot is null) _viewModel.SetRefreshError();
            if (collapseWhenFinished) CollapseImmediately();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _viewModel.SetRefreshError(); }
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverIntent.PointerEntered();
        _collapseTimer.Stop();
        Expand();
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverIntent.PointerLeft();
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (_hoverIntent.ConsumeCollapse(IsMouseOver)) Collapse();
    }

    private void Expand()
    {
        if (_visualState is WidgetVisualState.Expanded or WidgetVisualState.AnimatingToExpanded) return;
        CaptureCompactRectIfNeeded();
        ExpandedCard.Measure(new System.Windows.Size(344, double.PositiveInfinity));
        var expandedHeight = Math.Max(210d, ExpandedCard.DesiredSize.Height);
        var placement = WidgetPlacement.CalculateExpandedTarget(_compactRect, ExpandedWidth, expandedHeight, GetCurrentWorkArea());
        _anchorRight = placement.AnchorRight;
        _anchorBottom = placement.AnchorBottom;
        CompactCard.IsHitTestVisible = false;
        ExpandedCard.IsHitTestVisible = true;
        AnimateTo(placement.Rect, expanded: true);
    }

    private void Collapse()
    {
        if (_visualState is WidgetVisualState.Compact or WidgetVisualState.AnimatingToCompact) return;
        if (_visualState == WidgetVisualState.Expanded)
        {
            _compactRect = WidgetPlacement.CompactFromExpanded(CurrentRect(), CompactWidth, CompactHeight, _anchorRight, _anchorBottom, GetCurrentWorkArea());
        }
        CompactCard.IsHitTestVisible = true;
        ExpandedCard.IsHitTestVisible = false;
        AnimateTo(_compactRect, expanded: false);
    }

    private void CollapseImmediately()
    {
        _collapseTimer.Stop();
        _hoverIntent.Reset();
        _transitionVersion++;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        ExpandedCard.BeginAnimation(OpacityProperty, null);
        CompactCard.BeginAnimation(OpacityProperty, null);
        if (_visualState == WidgetVisualState.Expanded)
        {
            _compactRect = WidgetPlacement.CompactFromExpanded(CurrentRect(), CompactWidth, CompactHeight, _anchorRight, _anchorBottom, GetCurrentWorkArea());
        }
        SetWindowRect(_compactRect);
        ApplyCompactVisuals();
    }

    private async void AnimateTo(WidgetRect target, bool expanded)
    {
        var version = ++_transitionVersion;
        _visualState = expanded ? WidgetVisualState.AnimatingToExpanded : WidgetVisualState.AnimatingToCompact;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(LeftProperty, AnimationTo(target.X, easing), HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty, AnimationTo(target.Y, easing), HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(WidthProperty, AnimationTo(target.Width, easing), HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(HeightProperty, AnimationTo(target.Height, easing), HandoffBehavior.SnapshotAndReplace);
        ExpandedCard.BeginAnimation(OpacityProperty, FadeTo(expanded ? 1d : 0d, easing), HandoffBehavior.SnapshotAndReplace);
        CompactCard.BeginAnimation(OpacityProperty, FadeTo(expanded ? 0d : 1d, easing), HandoffBehavior.SnapshotAndReplace);
        await Task.Delay(TransitionDuration.TimeSpan);
        if (version != _transitionVersion) return;
        CommitWindowAnimations(target);
        if (expanded)
        {
            _visualState = WidgetVisualState.Expanded;
            ExpandedCard.Opacity = 1;
            CompactCard.Opacity = 0;
        }
        else
        {
            _visualState = WidgetVisualState.Compact;
            ExpandedCard.Opacity = 0;
            CompactCard.Opacity = 1;
            _compactRect = target;
            SaveSettings();
        }
    }

    private static DoubleAnimation AnimationTo(double value, IEasingFunction easing) => new(value, TransitionDuration) { EasingFunction = easing };
    private static DoubleAnimation FadeTo(double value, IEasingFunction easing) => new(value, TransitionDuration) { EasingFunction = easing };

    private void CommitWindowAnimations(WidgetRect target)
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        ExpandedCard.BeginAnimation(OpacityProperty, null);
        CompactCard.BeginAnimation(OpacityProperty, null);
        SetWindowRect(target);
    }

    private void ApplyCompactVisuals()
    {
        _visualState = WidgetVisualState.Compact;
        CompactCard.Opacity = 1;
        CompactCard.IsHitTestVisible = true;
        ExpandedCard.Opacity = 0;
        ExpandedCard.IsHitTestVisible = false;
    }

    private void CaptureCompactRectIfNeeded()
    {
        if (_visualState == WidgetVisualState.Compact) _compactRect = CurrentRect() with { Width = CompactWidth, Height = CompactHeight };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _collapseTimer.Stop();
        _hoverIntent.BeginDrag();
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _hoverIntent.EndDrag(); }
        var current = CurrentRect();
        _compactRect = WidgetPlacement.CompactFromExpanded(current, CompactWidth, CompactHeight, _anchorRight, _anchorBottom, GetCurrentWorkArea());
        SaveSettings();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(collapseWhenFinished: true);
    private void Hide_Click(object sender, RoutedEventArgs e) => HideWindow();
    private void ToggleVisibility() { if (IsVisible) HideWindow(); else ShowWindow(); }

    private void HideWindow()
    {
        CollapseImmediately();
        Hide();
        _visibilityItem.Text = "显示悬浮窗";
    }

    private void ShowWindow()
    {
        CollapseImmediately();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _visibilityItem.Text = "隐藏悬浮窗";
        _ = RefreshAsync(collapseWhenFinished: true);
    }

    private void ToggleTopmost()
    {
        Topmost = !Topmost;
        _settings.Topmost = Topmost;
        _topmostItem.Checked = Topmost;
        SaveSettings();
    }

    private void ToggleStartup()
    {
        var requested = !_startupItem.Checked;
        if (!StartupManager.SetEnabled(requested))
        {
            _notifyIcon.ShowBalloonTip(3000, "Codex 用量", "无法修改开机启动设置。", Forms.ToolTipIcon.Warning);
            return;
        }
        _settings.StartWithWindows = requested;
        _startupItem.Checked = requested;
        SaveSettings();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideWindow();
    }

    private void SaveSettings()
    {
        if (IsLoaded)
        {
            _settings.Left = _compactRect.X;
            _settings.Top = _compactRect.Y;
        }
        _settings.Topmost = Topmost;
        _settingsStore.Save(_settings);
    }

    private WidgetRect GetCurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var screen = Forms.Screen.FromHandle(handle);
            var source = PresentationSource.FromVisual(this);
            var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var topLeft = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            var bottomRight = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
            return new WidgetRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        }
        var area = SystemParameters.WorkArea;
        return new WidgetRect(area.X, area.Y, area.Width, area.Height);
    }

    private WidgetRect CurrentRect() => new(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
    private void SetWindowRect(WidgetRect rect) { Left = rect.X; Top = rect.Y; Width = rect.Width; Height = rect.Height; }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null) return icon;
            }
        }
        catch (ArgumentException) { }
        catch (FileNotFoundException) { }

        return (System.Drawing.Icon)SystemIcons.Application.Clone();
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        SaveSettings();
        _allowClose = true;
        _transitionVersion++;
        _collapseTimer.Stop();
        _tickTimer.Stop();
        _scanTimer.Stop();
        _refreshSignalTimer.Stop();
        _refreshSignalDebouncer.Reset();
        _lifetimeCancellation.Cancel();
        foreach (var watcher in _watchers) watcher.Dispose();
        _liveSource.RateLimitsUpdated -= LiveSource_RateLimitsUpdated;
        await _liveSource.DisposeAsync();
        _lifetimeCancellation.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}
