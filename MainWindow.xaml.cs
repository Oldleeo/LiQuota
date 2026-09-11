using Microsoft.Win32;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexQuotaWidget;

public partial class MainWindow : Window
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LiQuota";
    private const string LegacyRunValueName = "CodexQuotaWidget";
    private readonly MainViewModel _viewModel = new();
    private readonly CodexAppServerClient _client = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _dockTimer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly string? _qaOutputPath;
    private QuotaSnapshot? _lastSnapshot;
    private string? _lastNotificationKey;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.MinValue;
    private nint _windowHandle;
    private nint _hostHandle;
    private bool _isDocked;
    private bool _isExiting;
    private bool _qaSaved;

    internal MainWindow(AppSettings settings, string? qaOutputPath = null)
    {
        _settings = settings;
        InitializeComponent();
        _qaOutputPath = qaOutputPath;
        DataContext = _viewModel;
        QuotaPopup.DataContext = _viewModel;
        Opacity = 0;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => _viewModel.Tick();
        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _dockTimer.Tick += (_, _) => UpdateDockPosition();

        _client.AccountChanged += Client_AccountChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        _trayIcon = CreateTrayIcon();
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        CodexWindowTracker.MakeNoActivateToolWindow(_windowHandle);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();
        _countdownTimer.Start();
        _dockTimer.Start();
        UpdateDockPosition();
        await RefreshAsync();
    }

    private void Client_AccountChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (DateTimeOffset.Now - _lastRefreshAt > TimeSpan.FromSeconds(3))
            {
                await RefreshAsync();
            }
        });
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(() => ThemeService.Apply(_settings.Theme));
        }
    }

    private async Task RefreshAsync()
    {
        if (_viewModel.IsRefreshing)
        {
            return;
        }

        _viewModel.BeginRefresh();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var snapshot = await _client.ReadRateLimitsAsync(timeout.Token);
            _lastSnapshot = snapshot;
            _lastRefreshAt = DateTimeOffset.Now;
            _viewModel.Apply(snapshot);
            UpdateTrayTooltip(snapshot);
            ShowLowQuotaNotificationIfNeeded(snapshot);

            if (_qaOutputPath is not null && !_qaSaved)
            {
                await SaveQaArtifactsAndExitAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            _lastRefreshAt = DateTimeOffset.Now;
            _viewModel.ApplyError(ToFriendlyError(ex));
        }
    }

    private void UpdateDockPosition()
    {
        if (_windowHandle == nint.Zero || _isExiting)
        {
            return;
        }

        var host = CodexWindowTracker.ResolveHostWindow(_hostHandle);
        if (host == nint.Zero || CodexWindowTracker.IsMinimized(host))
        {
            _isDocked = false;
            _hostHandle = nint.Zero;
            Opacity = 0;
            _trayIcon.Visible = true;
            return;
        }

        if (_hostHandle != host)
        {
            _hostHandle = host;
            CodexWindowTracker.SetOwnedWindow(_windowHandle, host);
        }

        if (!CodexWindowTracker.DockBadge(
                _windowHandle, host, Width, Height,
                _settings.HorizontalOffset, _settings.VerticalOffset))
        {
            _isDocked = false;
            Opacity = 0;
            _trayIcon.Visible = true;
            return;
        }

        _isDocked = true;
        Opacity = 1;
        _trayIcon.Visible = false;
    }

    private async Task SaveQaArtifactsAndExitAsync(QuotaSnapshot snapshot)
    {
        var outputPath = _qaOutputPath ?? throw new InvalidOperationException("缺少 QA 输出路径。");
        for (var attempt = 0; attempt < 24 && !_isDocked; attempt++)
        {
            UpdateDockPosition();
            await Task.Delay(250);
        }

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var originalOpacity = Opacity;
        Opacity = 1;
        UpdateLayout();

        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        await SaveVisualAsync(this, outputPath);

        QuotaPopup.IsOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (QuotaPopup.Child is FrameworkElement popupContent &&
            popupContent.ActualWidth > 0 && popupContent.ActualHeight > 0)
        {
            var popupPath = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(outputPath)}-popup{Path.GetExtension(outputPath)}");
            await SaveVisualAsync(popupContent, popupPath);
        }
        QuotaPopup.IsOpen = false;
        Opacity = originalOpacity;

        var hostRect = CodexWindowTracker.TryGetRect(_hostHandle, out var hostBounds) ? hostBounds : null;
        var badgeRect = CodexWindowTracker.TryGetRect(_windowHandle, out var badgeBounds) ? badgeBounds : null;
        var qa = new
        {
            Docked = _isDocked,
            HostWindow = _hostHandle.ToInt64(),
            HostRect = hostRect,
            BadgeRect = badgeRect,
            Snapshot = new
            {
                snapshot.Windows,
                snapshot.PlanType,
                snapshot.ResetCredits,
                Account = MainViewModel.FormatAccount(snapshot.AccountEmail, snapshot.AccountType),
                snapshot.FetchedAt
            }
        };
        var jsonPath = Path.ChangeExtension(outputPath, ".json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(qa, new JsonSerializerOptions { WriteIndented = true }));

        _qaSaved = true;
        _isExiting = true;
        Close();
    }

    private static async Task SaveVisualAsync(FrameworkElement visual, string outputPath)
    {
        const double scale = 3d;
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var output = File.Create(outputPath);
        encoder.Save(output);
    }

    private static string ToFriendlyError(Exception ex)
    {
        var baseException = ex.GetBaseException();
        var message = baseException.Message;
        if (baseException is FileNotFoundException ||
            baseException is Win32Exception { NativeErrorCode: 2 or 3 })
        {
            return "无法定位 Codex App Server。请先打开 Codex Desktop，LiQuota 会自动重试。";
        }

        if (message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex 登录已过期，请先重新登录。";
        }

        return $"刷新失败：{message}";
    }

    private void Badge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        QuotaPopup.IsOpen = !QuotaPopup.IsOpen;
        if (QuotaPopup.IsOpen)
        {
            _ = RefreshAsync();
        }
        e.Handled = true;
    }

    private void Badge_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        BuildBadgeContextMenu().IsOpen = true;
        e.Handled = true;
    }

    private System.Windows.Controls.ContextMenu BuildBadgeContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var refresh = new System.Windows.Controls.MenuItem { Header = "立即刷新" };
        refresh.Click += async (_, _) => await RefreshAsync();
        menu.Items.Add(refresh);

        var copy = new System.Windows.Controls.MenuItem { Header = "复制额度摘要", IsEnabled = _lastSnapshot is not null };
        copy.Click += (_, _) => CopyStatusSummary();
        menu.Items.Add(copy);

        var alignment = new System.Windows.Controls.MenuItem { Header = "微调显示位置" };
        alignment.Items.Add(CreateActionMenuItem("向左 1 像素", () => AdjustDock(-1, 0)));
        alignment.Items.Add(CreateActionMenuItem("向右 1 像素", () => AdjustDock(1, 0)));
        alignment.Items.Add(CreateActionMenuItem("向上 1 像素", () => AdjustDock(0, -1)));
        alignment.Items.Add(CreateActionMenuItem("向下 1 像素", () => AdjustDock(0, 1)));
        alignment.Items.Add(new System.Windows.Controls.Separator());
        alignment.Items.Add(CreateActionMenuItem("恢复默认位置", ResetDock));
        menu.Items.Add(alignment);

        var theme = new System.Windows.Controls.MenuItem { Header = "外观" };
        foreach (var (label, value) in new[] { ("跟随系统", "System"), ("浅色", "Light"), ("深色", "Dark") })
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = _settings.Theme == value
            };
            item.Click += (_, _) => SetTheme(value);
            theme.Items.Add(item);
        }
        menu.Items.Add(theme);

        var notification = new System.Windows.Controls.MenuItem
        {
            Header = "低额度提醒（20%）",
            IsCheckable = true,
            IsChecked = _settings.LowQuotaNotifications
        };
        notification.Click += (_, _) =>
        {
            _settings.LowQuotaNotifications = notification.IsChecked;
            _lastNotificationKey = null;
            SaveSettings();
        };
        menu.Items.Add(notification);

        var startup = new System.Windows.Controls.MenuItem
        {
            Header = "开机自动启动",
            IsCheckable = true,
            IsChecked = IsStartupEnabled()
        };
        startup.Click += (_, _) => SetStartupEnabled(startup.IsChecked);
        menu.Items.Add(startup);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(CreateActionMenuItem("退出 LiQuota", ExitApplication));
        return menu;
    }

    private static System.Windows.Controls.MenuItem CreateActionMenuItem(string header, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void AdjustDock(int horizontal, int vertical)
    {
        _settings.HorizontalOffset = Math.Clamp(_settings.HorizontalOffset + horizontal, -30, 30);
        _settings.VerticalOffset = Math.Clamp(_settings.VerticalOffset + vertical, -20, 20);
        SaveSettings();
        UpdateDockPosition();
    }

    private void ResetDock()
    {
        _settings.HorizontalOffset = 0;
        _settings.VerticalOffset = 0;
        SaveSettings();
        UpdateDockPosition();
    }

    private void SetTheme(string theme)
    {
        _settings.Theme = theme;
        ThemeService.Apply(theme);
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法保存设置：{ex.Message}", "LiQuota",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyStatusSummary()
    {
        if (_lastSnapshot is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_viewModel.BuildStatusSummary(_lastSnapshot));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"复制失败：{ex.Message}", "LiQuota",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("立即刷新", null, (_, _) => Dispatcher.Invoke(async () => await RefreshAsync()));
        menu.Items.Add("复制额度摘要", null, (_, _) => Dispatcher.Invoke(CopyStatusSummary));
        var startupItem = new System.Windows.Forms.ToolStripMenuItem("开机自动启动")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) => SetStartupEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        return new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "LiQuota · 等待 Codex 窗口",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/LiQuota.ico", UriKind.Absolute));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var icon = new System.Drawing.Icon(stream);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch
        {
            // Fall back to a generated icon if the packaged resource is unavailable.
        }

        return CreateTrayDrawingIcon();
    }

    private static System.Drawing.Icon CreateTrayDrawingIcon()
    {
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);
        using var background = new SolidBrush(System.Drawing.Color.FromArgb(4, 39, 49));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var outer = new System.Drawing.Pen(System.Drawing.Color.FromArgb(25, 229, 166), 3.5f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        using var inner = new System.Drawing.Pen(System.Drawing.Color.FromArgb(169, 255, 232), 3f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        graphics.DrawArc(outer, 5, 5, 22, 22, 35, 280);
        graphics.DrawArc(inner, 9, 9, 14, 14, 35, 270);
        using var dot = new SolidBrush(System.Drawing.Color.FromArgb(208, 255, 244));
        graphics.FillEllipse(dot, 22, 7, 3, 3);
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private void UpdateTrayTooltip(QuotaSnapshot snapshot)
    {
        var minimum = snapshot.Windows.Min(x => x.RemainingPercent);
        _trayIcon.Text = $"LiQuota · Codex 剩余 {minimum:0}%";
    }

    private void ShowLowQuotaNotificationIfNeeded(QuotaSnapshot snapshot)
    {
        if (!_settings.LowQuotaNotifications)
        {
            return;
        }

        var minimum = snapshot.Windows.MinBy(x => x.RemainingPercent);
        if (minimum is null || minimum.RemainingPercent > 20)
        {
            return;
        }

        var key = $"{minimum.Id}:{minimum.ResetsAt?.ToUnixTimeSeconds()}:{Math.Floor(minimum.RemainingPercent / 5)}";
        if (_lastNotificationKey == key)
        {
            return;
        }

        _lastNotificationKey = key;
        var wasVisible = _trayIcon.Visible;
        _trayIcon.Visible = true;
        _trayIcon.BalloonTipTitle = "LiQuota 低额度提醒";
        _trayIcon.BalloonTipText = $"{minimum.Title}剩余 {minimum.RemainingPercent:0}%";
        _trayIcon.ShowBalloonTip(5000);
        if (!wasVisible && _isDocked)
        {
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            hideTimer.Tick += (_, _) =>
            {
                hideTimer.Stop();
                if (_isDocked)
                {
                    _trayIcon.Visible = false;
                }
            };
            hideTimer.Start();
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            return;
        }

        QuotaPopup.IsOpen = false;
        _refreshTimer.Stop();
        _countdownTimer.Stop();
        _dockTimer.Stop();
        _client.AccountChanged -= Client_AccountChanged;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _client.Dispose();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false);
        return key?.GetValue(RunValueName) is string || key?.GetValue(LegacyRunValueName) is string;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, true);
            key.DeleteValue(LegacyRunValueName, false);
            if (enabled)
            {
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法修改开机启动设置：{ex.Message}", "LiQuota",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
