using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexQuotaWidget;

public sealed class CodexAppServerClient : IDisposable
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private StreamWriter? _writer;
    private long _nextId;
    private bool _disposed;

    public event EventHandler? AccountChanged;

    public async Task<QuotaSnapshot> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        JsonElement result;
        JsonElement? account = null;
        try
        {
            account = await TryReadAccountAsync(cancellationToken).ConfigureAwait(false);
            result = await SendRequestCoreAsync("account/rateLimits/read", null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            StopProcess();
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            account = await TryReadAccountAsync(cancellationToken).ConfigureAwait(false);
            result = await SendRequestCoreAsync("account/rateLimits/read", null, cancellationToken)
                .ConfigureAwait(false);
        }

        return ParseSnapshot(result, account);
    }

    private async Task<JsonElement?> TryReadAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SendRequestCoreAsync("account/read", new { refreshToken = false }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Older App Server versions may not expose account/read. Quota reading
            // remains useful, so identity is treated as optional for compatibility.
            return null;
        }
    }

    internal static QuotaSnapshot ParseSnapshot(JsonElement result, JsonElement? accountResult = null)
    {
        var windows = new List<QuotaWindow>();
        string? planType = null;
        string? accountEmail = null;
        string? accountType = null;
        var accountId = GetString(result, "accountId");

        if (accountResult is { } accountRoot &&
            accountRoot.TryGetProperty("account", out var account) &&
            account.ValueKind == JsonValueKind.Object)
        {
            accountEmail = GetString(account, "email");
            accountType = GetString(account, "type");
            planType = GetString(account, "planType");
            accountId ??= GetString(account, "id");
        }

        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byId.EnumerateObject())
            {
                ParseBucket(property.Name, property.Value, windows, ref planType);
            }
        }
        else if (result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            ParseBucket("codex", legacy, windows, ref planType);
        }

        int? resetCredits = null;
        if (result.TryGetProperty("rateLimitResetCredits", out var reset) &&
            reset.ValueKind == JsonValueKind.Object &&
            reset.TryGetProperty("availableCount", out var count) &&
            count.TryGetInt32(out var countValue))
        {
            resetCredits = countValue;
        }

        if (windows.Count == 0)
        {
            throw new InvalidDataException("Codex 没有返回可显示的额度窗口。请确认当前使用 ChatGPT 账号登录。");
        }

        windows.Sort((left, right) => left.WindowDurationMins.CompareTo(right.WindowDurationMins));
        return new QuotaSnapshot(
            windows,
            planType,
            resetCredits,
            accountId,
            accountEmail,
            accountType,
            DateTimeOffset.Now);
    }

    private static void ParseBucket(
        string bucketId,
        JsonElement bucket,
        ICollection<QuotaWindow> windows,
        ref string? planType)
    {
        var limitName = GetString(bucket, "limitName");
        planType ??= GetString(bucket, "planType");
        ParseWindow(bucketId, limitName, "primary", bucket, windows);
        ParseWindow(bucketId, limitName, "secondary", bucket, windows);
    }

    private static void ParseWindow(
        string bucketId,
        string? limitName,
        string windowName,
        JsonElement bucket,
        ICollection<QuotaWindow> windows)
    {
        if (!bucket.TryGetProperty(windowName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var used = window.TryGetProperty("usedPercent", out var usedElement) && usedElement.TryGetDouble(out var usedValue)
            ? usedValue
            : 0;
        var duration = window.TryGetProperty("windowDurationMins", out var durationElement) && durationElement.TryGetInt32(out var durationValue)
            ? durationValue
            : 0;
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var unixSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        var durationLabel = FormatDurationTitle(duration);
        var bucketLabel = !string.IsNullOrWhiteSpace(limitName) && !limitName.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? limitName
            : bucketId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? null : bucketId;
        var title = bucketLabel is null ? $"{durationLabel}额度" : $"{bucketLabel} · {durationLabel}";

        windows.Add(new QuotaWindow(
            $"{bucketId}:{windowName}",
            title,
            Math.Clamp(used, 0, 100),
            duration,
            resetsAt));
    }

    private static string FormatDurationTitle(int minutes)
    {
        if (minutes >= 1440 && minutes % 1440 == 0)
        {
            return $"{minutes / 1440} 天";
        }

        if (minutes >= 60 && minutes % 60 == 0)
        {
            return $"{minutes / 60} 小时";
        }

        return minutes > 0 ? $"{minutes} 分钟" : "当前";
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _writer is not null)
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _writer is not null)
            {
                return;
            }

            StopProcess();
            var executable = CodexExecutableLocator.Find();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server --stdio",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Codex App Server。");
            _writer = _process.StandardInput;
            _writer.AutoFlush = true;
            _ = Task.Run(() => ReadOutputLoopAsync(_process.StandardOutput));
            _ = Task.Run(() => DrainErrorLoopAsync(_process.StandardError));

            var initialize = new
            {
                clientInfo = new
                {
                    name = "codex_quota_widget",
                    title = "Codex Quota Widget",
                    version = "3.0.0"
                }
            };
            await SendRequestCoreAsync("initialize", initialize, cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<JsonElement> SendRequestCoreAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("Codex App Server 尚未连接。");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await WriteJsonLineAsync(writer, new { method, id, @params = parameters ?? new { } }, cancellationToken)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(18), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("Codex App Server 尚未连接。");
        await WriteJsonLineAsync(writer, new { method, @params = parameters }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteJsonLineAsync(StreamWriter writer, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadOutputLoopAsync(StreamReader reader)
    {
        try
        {
            while (!_disposed && await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("method", out var methodElement) &&
                    methodElement.ValueKind == JsonValueKind.String &&
                    methodElement.GetString() is "account/updated" or "account/rateLimits/updated")
                {
                    AccountChanged?.Invoke(this, EventArgs.Empty);
                    continue;
                }

                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) ||
                    !_pending.TryRemove(id, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = GetString(error, "message") ?? error.GetRawText();
                    completion.TrySetException(new InvalidOperationException(message));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(new InvalidDataException("Codex 返回了无法识别的响应。"));
                }
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            FailPending(ex);
        }
    }

    private static async Task DrainErrorLoopAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // The desktop executable can emit harmless PATH alias warnings here.
            }
        }
        catch
        {
            // Standard error is diagnostic only.
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void StopProcess()
    {
        try
        {
            _writer?.Dispose();
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
            }
            _process?.Dispose();
        }
        catch
        {
            // A failed process is replaced on the next request.
        }
        finally
        {
            _writer = null;
            _process = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopProcess();
        FailPending(new ObjectDisposedException(nameof(CodexAppServerClient)));
        _startGate.Dispose();
        _writeGate.Dispose();
    }
}

internal static class CodexExecutableLocator
{
    public static string Find()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "codex.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        foreach (var processName in new[] { "Codex", "ChatGPT" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(processPath))
                    {
                        continue;
                    }

                    // Newer desktop builds use ChatGPT.exe as the Electron host and run
                    // resources\codex.exe as a separate backend process.  When this widget
                    // is launched from Explorer, that resources folder is not on PATH, so
                    // use the running executable paths as the source of truth.
                    var root = Path.GetDirectoryName(processPath)!;
                    var candidates = new[]
                    {
                        processPath,
                        Path.Combine(root, "codex.exe"),
                        Path.Combine(root, "resources", "codex.exe"),
                        Path.Combine(root, "app", "resources", "codex.exe")
                    };
                    var found = candidates.FirstOrDefault(candidate =>
                        Path.GetFileName(candidate).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(candidate));
                    if (found is not null)
                    {
                        return found;
                    }
                }
                catch
                {
                    // Some processes do not permit reading MainModule.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return "codex.exe";
    }
}
