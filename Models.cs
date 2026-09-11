using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CodexQuotaWidget;

public sealed record QuotaWindow(
    string Id,
    string Title,
    double UsedPercent,
    int WindowDurationMins,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record QuotaSnapshot(
    IReadOnlyList<QuotaWindow> Windows,
    string? PlanType,
    int? ResetCredits,
    string? AccountId,
    string? AccountEmail,
    string? AccountType,
    DateTimeOffset FetchedAt);

public sealed class QuotaWindowViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush HealthyBrush = FrozenBrush(16, 163, 127);
    private static readonly SolidColorBrush WarningBrush = FrozenBrush(245, 158, 11);
    private static readonly SolidColorBrush CriticalBrush = FrozenBrush(239, 68, 68);
    private readonly QuotaWindow _model;

    public QuotaWindowViewModel(QuotaWindow model) => _model = model;

    public string Title => _model.Title;
    public double RemainingPercent => _model.RemainingPercent;
    public string RemainingPercentText => $"{RemainingPercent:0}%";
    public string UsedText => $"已用 {_model.UsedPercent:0.#}%";
    public string WindowText => FormatDuration(_model.WindowDurationMins);
    public System.Windows.GridLength RemainingWidth => new(RemainingPercent, System.Windows.GridUnitType.Star);
    public System.Windows.GridLength UsedWidth => new(Math.Max(0.01, 100 - RemainingPercent), System.Windows.GridUnitType.Star);
    public System.Windows.Media.Brush AccentBrush => RemainingPercent switch
    {
        <= 15 => CriticalBrush,
        <= 35 => WarningBrush,
        _ => HealthyBrush
    };

    public string ResetText
    {
        get
        {
            if (_model.ResetsAt is null)
            {
                return "重置时间暂不可用";
            }

            var remaining = _model.ResetsAt.Value - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
            {
                return "额度窗口正在重置";
            }

            var countdown = remaining.TotalDays >= 1
                ? $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时"
                : remaining.TotalHours >= 1
                    ? $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分"
                    : $"{Math.Max(0, remaining.Minutes)} 分钟";
            return $"{countdown}后重置 · {_model.ResetsAt.Value.LocalDateTime:MM-dd HH:mm}";
        }
    }

    public void Tick() => OnPropertyChanged(nameof(ResetText));

    private static string FormatDuration(int minutes)
    {
        if (minutes >= 1440 && minutes % 1440 == 0)
        {
            return $"{minutes / 1440} 天窗口";
        }

        if (minutes >= 60 && minutes % 60 == 0)
        {
            return $"{minutes / 60} 小时窗口";
        }

        return $"{minutes} 分钟窗口";
    }

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush OnlineBrush = FrozenBrush(16, 163, 127);
    private static readonly SolidColorBrush BusyBrush = FrozenBrush(245, 158, 11);
    private static readonly SolidColorBrush ErrorBrush = FrozenBrush(239, 68, 68);
    private string _statusText = "正在连接…";
    private System.Windows.Media.Brush _statusBrush = BusyBrush;
    private string _errorText = string.Empty;
    private string _footerText = "等待首次同步";
    private string _accountText = "正在识别当前账号…";
    private string _badgeText = "--";
    private string _badgeToolTip = "LiQuota 正在读取 Codex 额度…";
    private System.Windows.Media.Brush _badgeBrush = BusyBrush;
    private bool _hasError;

    public ObservableCollection<QuotaWindowViewModel> QuotaWindows { get; } = [];
    public bool IsRefreshing { get; private set; }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public System.Windows.Media.Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public string FooterText { get => _footerText; private set => Set(ref _footerText, value); }
    public string AccountText { get => _accountText; private set => Set(ref _accountText, value); }
    public string BadgeText { get => _badgeText; private set => Set(ref _badgeText, value); }
    public string BadgeToolTip { get => _badgeToolTip; private set => Set(ref _badgeToolTip, value); }
    public System.Windows.Media.Brush BadgeBrush { get => _badgeBrush; private set => Set(ref _badgeBrush, value); }
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }

    public void BeginRefresh()
    {
        IsRefreshing = true;
        StatusText = "正在刷新…";
        StatusBrush = BusyBrush;
        if (QuotaWindows.Count == 0)
        {
            BadgeText = "--";
            BadgeBrush = BusyBrush;
        }
        HasError = false;
    }

    public void Apply(QuotaSnapshot snapshot)
    {
        QuotaWindows.Clear();
        foreach (var window in snapshot.Windows)
        {
            QuotaWindows.Add(new QuotaWindowViewModel(window));
        }

        IsRefreshing = false;
        StatusText = "已同步";
        StatusBrush = OnlineBrush;
        HasError = false;

        var minimumWindow = snapshot.Windows.MinBy(x => x.RemainingPercent)!;
        BadgeText = $"{minimumWindow.RemainingPercent:0}%";
        BadgeBrush = minimumWindow.RemainingPercent switch
        {
            <= 15 => ErrorBrush,
            <= 35 => BusyBrush,
            _ => OnlineBrush
        };
        var resetText = minimumWindow.ResetsAt is null
            ? "重置时间暂不可用"
            : $"{minimumWindow.ResetsAt.Value.LocalDateTime:MM-dd HH:mm} 重置";
        BadgeToolTip = $"LiQuota · Codex 剩余 {minimumWindow.RemainingPercent:0}% · {resetText}";

        var plan = FormatPlan(snapshot.PlanType);
        var credits = snapshot.ResetCredits is > 0 ? $" · {snapshot.ResetCredits} 次免费重置" : string.Empty;
        AccountText = FormatAccount(snapshot.AccountEmail, snapshot.AccountType);
        FooterText = $"{plan}{credits} · 更新于 {snapshot.FetchedAt.LocalDateTime:HH:mm:ss}";
    }

    public void ApplyError(string message)
    {
        IsRefreshing = false;
        StatusText = "连接异常";
        StatusBrush = ErrorBrush;
        BadgeText = "!";
        BadgeBrush = ErrorBrush;
        BadgeToolTip = message;
        ErrorText = message;
        HasError = true;
        FooterText = $"上次尝试 {DateTime.Now:HH:mm:ss}";
    }

    public void Tick()
    {
        foreach (var window in QuotaWindows)
        {
            window.Tick();
        }
    }

    private static string FormatPlan(string? planType) => planType?.ToLowerInvariant() switch
    {
        "plus" => "ChatGPT Plus",
        "pro" => "ChatGPT Pro",
        "team" => "ChatGPT Team",
        "business" => "ChatGPT Business",
        "enterprise" => "ChatGPT Enterprise",
        "self_serve_business_prolite" => "ChatGPT Business",
        null or "" => "Codex",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(planType.Replace('_', ' '))
    };

    internal static string FormatAccount(string? email, string? accountType)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            var parts = email.Split('@', 2);
            if (parts.Length == 2)
            {
                var local = parts[0];
                var visible = local.Length switch
                {
                    0 => string.Empty,
                    1 => local,
                    _ => local[..Math.Min(2, local.Length)]
                };
                return $"当前账号  {visible}***@{parts[1]}";
            }

            return "当前账号  " + email;
        }

        return accountType?.Equals("apiKey", StringComparison.OrdinalIgnoreCase) == true
            ? "当前身份  API Key"
            : "当前账号  Codex Desktop";
    }

    public string BuildStatusSummary(QuotaSnapshot snapshot)
    {
        var lines = new List<string>
        {
            "LiQuota · Codex 额度",
            FormatAccount(snapshot.AccountEmail, snapshot.AccountType),
            $"套餐  {FormatPlan(snapshot.PlanType)}"
        };

        lines.AddRange(snapshot.Windows.Select(window =>
            $"{window.Title}：剩余 {window.RemainingPercent:0.#}% · " +
            (window.ResetsAt is null ? "重置时间未知" : $"{window.ResetsAt.Value.LocalDateTime:yyyy-MM-dd HH:mm} 重置")));
        lines.Add($"更新于 {snapshot.FetchedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        return string.Join(Environment.NewLine, lines);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
