using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MceIndex.Mcp.Configuration;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Crawling;

public sealed partial class CamofoxClient : IAsyncDisposable
{
    private const int MaximumLogCharacters = 16_384;
    private readonly MceIndexOptions options;
    private readonly ILogger<CamofoxClient> logger;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly StringBuilder processLog = new();
    private readonly object processLogGate = new();
    private readonly string userId = $"mceindex-mcp-{Environment.ProcessId}";
    private readonly string sessionKey = $"crawl-{Guid.NewGuid():N}";
    private Process? ownedProcess;
    private string? adminKey;
    private bool disposed;

    public CamofoxClient(MceIndexOptions options, ILogger<CamofoxClient> logger)
    {
        this.options = options;
        this.logger = logger;
        httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            BaseAddress = options.CamofoxUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (options.CamofoxAccessKey is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.CamofoxAccessKey);
        }
    }

    public async Task<CamofoxTab> OpenTabAsync(Uri target, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var body = $$"""{"userId":{{JsonSerializer.Serialize(userId)}},"sessionKey":{{JsonSerializer.Serialize(sessionKey)}},"url":{{JsonSerializer.Serialize(target.AbsoluteUri)}}}""";
        var result = await SendJsonAsync(HttpMethod.Post, "tabs", body, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("tabId", out var tabIdProperty) ||
            string.IsNullOrWhiteSpace(tabIdProperty.GetString()))
        {
            throw ProtocolError("Camofox did not return a tabId.");
        }
        return new CamofoxTab(tabIdProperty.GetString()!, userId);
    }

    public async Task<JsonElement> EvaluateAsync(
        CamofoxTab tab,
        string expression,
        CancellationToken cancellationToken)
    {
        var body = $$"""{"userId":{{JsonSerializer.Serialize(tab.UserId)}},"expression":{{JsonSerializer.Serialize(expression)}}}""";
        var root = await SendJsonAsync(
            HttpMethod.Post,
            $"tabs/{Uri.EscapeDataString(tab.Id)}/evaluate",
            body,
            cancellationToken).ConfigureAwait(false);
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean() ||
            !root.TryGetProperty("result", out var result))
        {
            throw ProtocolError("Camofox returned an invalid evaluation response.");
        }
        return result.Clone();
    }

    public async Task CloseTabAsync(CamofoxTab tab)
    {
        try
        {
            await SendJsonAsync(
                HttpMethod.Delete,
                $"tabs/{Uri.EscapeDataString(tab.Id)}?userId={Uri.EscapeDataString(tab.UserId)}",
                null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or MceIndexException)
        {
            LogTabCloseFailure(logger, error, tab.Id);
        }
    }

    public async Task CloseBrowserAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed && ownedProcess is null)
            {
                return;
            }

            try
            {
                await SendJsonAsync(
                    HttpMethod.Delete,
                    $"sessions/{Uri.EscapeDataString(userId)}",
                    null,
                    CancellationToken.None,
                    ensureSuccess: false).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or MceIndexException)
            {
            }

            var process = ownedProcess;
            if (process is null)
            {
                return;
            }

            if (!process.HasExited && adminKey is not null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "stop");
                    request.Headers.Add("X-Admin-Key", adminKey);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                {
                    LogShutdownFailure(logger, error);
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            process.Dispose();
            ownedProcess = null;
            adminKey = null;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        await CloseBrowserAsync().ConfigureAwait(false);
        httpClient.Dispose();
        lifecycleGate.Dispose();
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            if (ownedProcess is { HasExited: false })
            {
                await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (options.CamofoxExecutable is null)
            {
                throw new MceIndexException(
                    MceIndexErrorCode.BrowserNotFound,
                    $"Camofox is unavailable at {options.CamofoxUri} and camofox-browser was not found. " +
                    "Install @askjo/camofox-browser or set MCEINDEX_CAMOFOX_URL.");
            }
            if (!CanStartLocalService(options.CamofoxUri))
            {
                throw new MceIndexException(
                    MceIndexErrorCode.BrowserNotFound,
                    $"Camofox is unavailable at {options.CamofoxUri}; automatic startup is supported only for a loopback root URL.");
            }

            StartProcess();
            await WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void StartProcess()
    {
        var executable = options.CamofoxExecutable!;
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{executable}\"\"";
        }
        else
        {
            startInfo.FileName = executable;
        }

        adminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        startInfo.Environment["CAMOFOX_PORT"] = options.CamofoxUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["CAMOFOX_BIND_HOST"] = "127.0.0.1";
        startInfo.Environment["CAMOFOX_ADMIN_KEY"] = adminKey;
        startInfo.Environment["CAMOFOX_CRASH_REPORT_ENABLED"] = "false";
        startInfo.Environment["CAMOFOX_LOG_LEVEL"] = "warn";
        if (options.CamofoxProfile is not null)
        {
            startInfo.Environment["CAMOFOX_PROFILE_DIR"] = options.CamofoxProfile;
        }
        if (options.CamofoxAccessKey is not null)
        {
            startInfo.Environment["CAMOFOX_ACCESS_KEY"] = options.CamofoxAccessKey;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => AppendProcessLog(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AppendProcessLog(eventArgs.Data);
        try
        {
            if (!process.Start())
            {
                throw new Win32Exception("Process.Start returned false.");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            ownedProcess = process;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new MceIndexException(
                MceIndexErrorCode.BrowserNotFound,
                $"Could not start Camofox from {executable}.",
                innerException: error);
        }
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < options.RequestTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownedProcess is { HasExited: true } process)
            {
                var exitCode = process.ExitCode;
                ownedProcess = null;
                process.Dispose();
                throw new MceIndexException(
                    MceIndexErrorCode.BrowserNotFound,
                    $"Camofox exited with code {exitCode} during startup.{FormatProcessLog()}");
            }
            if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new MceIndexException(
            MceIndexErrorCode.LoadTimeout,
            $"Camofox did not become ready at {options.CamofoxUri} within {options.RequestTimeout.TotalMilliseconds:F0}ms.{FormatProcessLog()}");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "health");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }
            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            return false;
        }
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string path,
        string? body,
        CancellationToken cancellationToken,
        bool ensureSuccess = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout + TimeSpan.FromSeconds(15));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MceIndexException(
                MceIndexErrorCode.LoadTimeout,
                $"Camofox request {method} {path} timed out.",
                innerException: error);
        }
        catch (HttpRequestException error)
        {
            throw new MceIndexException(
                MceIndexErrorCode.BrowserNotFound,
                $"Could not reach Camofox at {options.CamofoxUri}.",
                innerException: error);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (ensureSuccess && !response.IsSuccessStatusCode)
            {
                throw MapHttpError(response.StatusCode, method, path, content);
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                return default;
            }
            try
            {
                using var document = JsonDocument.Parse(content);
                return document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                throw ProtocolError($"Camofox returned invalid JSON for {method} {path}.", error);
            }
        }
    }

    private static MceIndexException MapHttpError(HttpStatusCode status, HttpMethod method, string path, string content)
    {
        var message = ErrorMessage(content);
        var code = status switch
        {
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => MceIndexErrorCode.LoadTimeout,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MceIndexErrorCode.InvalidConfiguration,
            HttpStatusCode.ServiceUnavailable when message.Contains("launch", StringComparison.OrdinalIgnoreCase) => MceIndexErrorCode.BrowserNotFound,
            _ => MceIndexErrorCode.ExtractionFailed,
        };
        return new MceIndexException(code, $"Camofox {method} {path} failed with HTTP {(int)status}: {message}");
    }

    private static string ErrorMessage(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? content;
            }
        }
        catch (JsonException)
        {
        }
        return content.Length <= 1_000 ? content : content[..1_000];
    }

    private static MceIndexException ProtocolError(string message, Exception? error = null) =>
        new(MceIndexErrorCode.ExtractionFailed, message, innerException: error);

    private static bool CanStartLocalService(Uri uri) =>
        uri.IsLoopback && uri.AbsolutePath == "/";

    private void AppendProcessLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lock (processLogGate)
        {
            processLog.AppendLine(line);
            if (processLog.Length > MaximumLogCharacters)
            {
                processLog.Remove(0, processLog.Length - MaximumLogCharacters);
            }
        }
    }

    private string FormatProcessLog()
    {
        lock (processLogGate)
        {
            return processLog.Length == 0 ? string.Empty : $" Camofox log tail: {processLog}";
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not close Camofox tab {TabId}")]
    private static partial void LogTabCloseFailure(ILogger logger, Exception error, string tabId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Camofox did not acknowledge browser shutdown")]
    private static partial void LogShutdownFailure(ILogger logger, Exception error);
}

public sealed record CamofoxTab(string Id, string UserId);
