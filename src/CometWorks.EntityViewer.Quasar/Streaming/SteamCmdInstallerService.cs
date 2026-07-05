using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class SteamCmdInstallerService(
    EntityViewerStreamingPaths paths,
    IEntityViewerStreamingSettingsStore settingsStore,
    IHostApplicationLifetime lifetime,
    ILogger<SteamCmdInstallerService> logger)
{
    private const int SpaceEngineersClientAppId = 244850;
    private const int MaxLogEntries = 500;
    private const string WindowsSteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const string LinuxSteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
    private static readonly HttpClient SteamCmdDownloadClient = new();

    private readonly object _sync = new();
    private readonly List<SteamCmdInstallerLogEntry> _log = [];

    private Process? _process;
    private CancellationTokenSource? _runCancellation;
    private string _state = "Idle";
    private string _message = "Installer idle.";
    private string _steamCmdPath = string.Empty;
    private string _loginName = string.Empty;
    private bool _validate = true;
    private int? _exitCode;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private int _sequence;

    public SteamCmdInstallerStatusResponse GetStatus()
    {
        lock (_sync)
        {
            return BuildStatusLocked();
        }
    }

    public async Task<SteamCmdInstallerStatusResponse> StartAsync(
        SteamCmdInstallRequest request,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var loginName = NormalizeLoginName(request.LoginName);
        var validate = request.Validate;
        Process process;
        CancellationTokenSource runCancellation;
        string steamCmdPath;
        Task stdoutPump;
        Task stderrPump;

        lock (_sync)
        {
            if (_process is { HasExited: false })
                return BuildStatusLocked();
        }

        if (!IsSteamAccountLoginName(loginName))
        {
            const string message = "Space Engineers client asset install requires a Steam account name. Anonymous SteamCMD cannot download paid app 244850; use an account that owns Space Engineers and any DLC assets this server needs.";
            await ApplyCompletedSettingsAsync(
                string.Empty,
                "Failed",
                message,
                exitCode: null,
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _state = "Failed";
                _message = message;
                _steamCmdPath = string.Empty;
                _loginName = string.Empty;
                _validate = validate;
                _exitCode = null;
                _startedAtUtc = null;
                _completedAtUtc = DateTimeOffset.UtcNow;
                AddLogLocked("error", message);
                return BuildStatusLocked();
            }
        }

        steamCmdPath = ResolveSteamCmdPath();
        if (string.IsNullOrWhiteSpace(steamCmdPath))
            steamCmdPath = await TryInstallManagedSteamCmdAsync(loginName, validate, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(steamCmdPath))
        {
            await ApplyCompletedSettingsAsync(
                loginName,
                "SteamCmdMissing",
                "SteamCMD executable was not found and could not be installed automatically.",
                exitCode: null,
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _state = "SteamCmdMissing";
                _message = "SteamCMD executable was not found and could not be installed automatically.";
                _steamCmdPath = string.Empty;
                _loginName = loginName;
                _validate = validate;
                _exitCode = null;
                _startedAtUtc = null;
                _completedAtUtc = DateTimeOffset.UtcNow;
                AddLogLocked("error", _message);
                return BuildStatusLocked();
            }
        }

        Directory.CreateDirectory(paths.ManagedGameClientDirectory);
        var arguments = BuildSteamCmdUpdateArguments(paths.ManagedGameClientDirectory, loginName, validate);
        var startInfo = CreateSteamCmdStartInfo(steamCmdPath, arguments);
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("SteamCMD did not start.");
            stdoutPump = Task.Run(() => PumpProcessOutputAsync(process.StandardOutput, "stdout"), CancellationToken.None);
            stderrPump = Task.Run(() => PumpProcessOutputAsync(process.StandardError, "stderr"), CancellationToken.None);
        }
        catch (Exception exception)
        {
            process.Dispose();
            runCancellation.Dispose();
            logger.LogWarning(exception, "Failed starting SteamCMD for Entity Viewer client asset install.");
            await ApplyCompletedSettingsAsync(
                loginName,
                "Failed",
                exception.Message,
                exitCode: null,
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _state = "Failed";
                _message = exception.Message;
                _steamCmdPath = steamCmdPath;
                _loginName = loginName;
                _validate = validate;
                _exitCode = null;
                _startedAtUtc = DateTimeOffset.UtcNow;
                _completedAtUtc = DateTimeOffset.UtcNow;
                AddLogLocked("error", exception.Message);
                return BuildStatusLocked();
            }
        }

        lock (_sync)
        {
            _process = process;
            _runCancellation = runCancellation;
            _state = automatic ? "AutoUpdating" : "Running";
            _message = automatic
                ? "SteamCMD hourly update running."
                : "SteamCMD installer running. Enter Steam Guard or password prompts below if SteamCMD asks for them.";
            _steamCmdPath = steamCmdPath;
            _loginName = loginName;
            _validate = validate;
            _exitCode = null;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _completedAtUtc = null;
            AddLogLocked("info", _message);
            AddLogLocked("info", $"Install directory: {paths.ManagedGameClientDirectory}");
        }

        await settingsStore.UpdateAsync(current =>
        {
            current.SteamCmdLoginName = loginName;
            current.LastInstallStatus = automatic ? "AutoUpdating" : "Running";
            current.LastInstallStartedAtUtc = DateTimeOffset.UtcNow;
            current.LastInstallCompletedAtUtc = null;
            current.LastInstallExitCode = null;
            current.LastInstallMessage = automatic ? "Hourly update running." : "SteamCMD installer running.";
            return current;
        }, cancellationToken).ConfigureAwait(false);

        _ = Task.Run(() => ObserveProcessAsync(process, runCancellation, loginName, automatic, stdoutPump, stderrPump), CancellationToken.None);

        lock (_sync)
        {
            return BuildStatusLocked();
        }
    }

    public Task<SteamCmdInstallerStatusResponse> SendInputAsync(SteamCmdInputRequest request)
    {
        var input = request.Input ?? string.Empty;
        lock (_sync)
        {
            if (_process is null || _process.HasExited)
            {
                _state = _state == "Idle" ? "Idle" : _state;
                _message = "SteamCMD is not running.";
                AddLogLocked("error", _message);
                return Task.FromResult(BuildStatusLocked());
            }

            try
            {
                _process.StandardInput.WriteLine(input);
                _process.StandardInput.Flush();
                AddLogLocked("stdin", "Input sent to SteamCMD.");
                return Task.FromResult(BuildStatusLocked());
            }
            catch (Exception exception)
            {
                _message = exception.Message;
                AddLogLocked("error", exception.Message);
                return Task.FromResult(BuildStatusLocked());
            }
        }
    }

    public Task<SteamCmdInstallerStatusResponse> CancelAsync()
    {
        lock (_sync)
        {
            if (_process is null || _process.HasExited)
            {
                _message = "SteamCMD is not running.";
                return Task.FromResult(BuildStatusLocked());
            }

            AddLogLocked("info", "Cancel requested. Stopping SteamCMD.");
            _runCancellation?.Cancel();
            TryKillProcessTree(_process);
            return Task.FromResult(BuildStatusLocked());
        }
    }

    private async Task ObserveProcessAsync(
        Process process,
        CancellationTokenSource runCancellation,
        string loginName,
        bool automatic,
        Task stdoutPump,
        Task stderrPump)
    {
        try
        {
            await process.WaitForExitAsync(runCancellation.Token).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            var contentProbe = EntityViewerContentRoots.Probe(paths.ManagedGameContentDirectory);
            var contentReady = contentProbe.IsUsable;
            var state = exitCode == 0 && contentReady ? "Succeeded" : "Failed";
            var message = state == "Succeeded"
                ? "Space Engineers client asset install/update complete."
                : exitCode == 0
                    ? BuildSuccessfulExitMissingContentMessage(loginName, contentProbe)
                    : $"SteamCMD exited with code {exitCode}.";

            await ApplyCompletedSettingsAsync(loginName, state, message, exitCode, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                _state = state;
                _message = message;
                _exitCode = exitCode;
                _completedAtUtc = DateTimeOffset.UtcNow;
                _process = null;
                _runCancellation = null;
                AddLogLocked(state == "Succeeded" ? "info" : "error", message);
            }
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            const string message = "SteamCMD install/update canceled.";
            await ApplyCompletedSettingsAsync(loginName, "Canceled", message, null, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                _state = "Canceled";
                _message = message;
                _completedAtUtc = DateTimeOffset.UtcNow;
                _process = null;
                _runCancellation = null;
                AddLogLocked("info", message);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SteamCMD process observation failed.");
            await ApplyCompletedSettingsAsync(loginName, "Failed", exception.Message, null, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                _state = "Failed";
                _message = exception.Message;
                _completedAtUtc = DateTimeOffset.UtcNow;
                _process = null;
                _runCancellation = null;
                AddLogLocked("error", exception.Message);
            }
        }
        finally
        {
            await WaitForOutputPumpsAsync(stdoutPump, stderrPump).ConfigureAwait(false);
            process.Dispose();
            runCancellation.Dispose();
        }
    }

    private async Task ApplyCompletedSettingsAsync(
        string loginName,
        string status,
        string message,
        int? exitCode,
        CancellationToken cancellationToken)
    {
        await settingsStore.UpdateAsync(current =>
        {
            current.SteamCmdLoginName = loginName;
            current.LastInstallStatus = status;
            current.LastInstallCompletedAtUtc = DateTimeOffset.UtcNow;
            current.LastInstallExitCode = exitCode;
            current.LastInstallMessage = message;
            return current;
        }, cancellationToken).ConfigureAwait(false);
    }

    private SteamCmdInstallerStatusResponse BuildStatusLocked()
    {
        return new SteamCmdInstallerStatusResponse
        {
            State = _state,
            IsRunning = _process is { HasExited: false },
            Message = _message,
            SteamCmdPath = _steamCmdPath,
            InstallDirectory = paths.ManagedGameClientDirectory,
            ContentDirectory = paths.ManagedGameContentDirectory,
            ContentReady = EntityViewerContentRoots.Probe(paths.ManagedGameContentDirectory).IsUsable,
            ContentDiagnostics = EntityViewerContentRoots.Probe(paths.ManagedGameContentDirectory).Message,
            LoginName = _loginName,
            Validate = _validate,
            ExitCode = _exitCode,
            StartedAtUtc = _startedAtUtc,
            CompletedAtUtc = _completedAtUtc,
            LastSequence = _sequence,
            Log = _log.ToList(),
        };
    }

    private void AddProcessLog(string stream, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        lock (_sync)
        {
            AddLogLocked(stream, message);
        }
    }

    private async Task PumpProcessOutputAsync(StreamReader reader, string stream)
    {
        var buffer = new char[512];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                    return;

                AddProcessLog(stream, new string(buffer, 0, read));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task WaitForOutputPumpsAsync(Task stdoutPump, Task stderrPump)
    {
        try
        {
            await Task.WhenAll(stdoutPump, stderrPump).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AddProcessLog("stderr", "Timed out while draining SteamCMD output.");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "SteamCMD output pump failed.");
        }
    }

    private void AddLogLocked(string stream, string message)
    {
        _sequence++;
        _log.Add(new SteamCmdInstallerLogEntry
        {
            Sequence = _sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            Stream = stream,
            Message = message,
        });

        if (_log.Count > MaxLogEntries)
            _log.RemoveRange(0, _log.Count - MaxLogEntries);
    }

    private async Task<string> TryInstallManagedSteamCmdAsync(
        string loginName,
        bool validate,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _state = "PreparingSteamCmd";
            _message = "SteamCMD was not found. Downloading managed SteamCMD...";
            _steamCmdPath = string.Empty;
            _loginName = loginName;
            _validate = validate;
            _exitCode = null;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _completedAtUtc = null;
            AddLogLocked("info", _message);
        }

        try
        {
            var managedSteamCmdDirectory = Path.Combine(paths.ManagedToolsDirectory, "SteamCMD");
            Directory.CreateDirectory(paths.ManagedToolsDirectory);

            var tempDirectory = Path.Combine(paths.ManagedToolsDirectory, $"SteamCMD-download-{Guid.NewGuid():N}");
            var extractDirectory = Path.Combine(tempDirectory, "extract");
            Directory.CreateDirectory(extractDirectory);

            try
            {
                var archiveName = OperatingSystem.IsWindows() ? "steamcmd.zip" : "steamcmd_linux.tar.gz";
                var archivePath = Path.Combine(tempDirectory, archiveName);
                var url = OperatingSystem.IsWindows() ? WindowsSteamCmdUrl : LinuxSteamCmdUrl;

                AddProcessLog("info", $"Downloading SteamCMD from {url}.");
                await DownloadFileAsync(url, archivePath, cancellationToken).ConfigureAwait(false);
                ExtractSteamCmdArchive(archivePath, extractDirectory);

                var extractedExecutable = FindSteamCmdExecutable(extractDirectory);
                if (string.IsNullOrWhiteSpace(extractedExecutable))
                    throw new FileNotFoundException("Downloaded SteamCMD package did not contain a SteamCMD executable.");

                Directory.CreateDirectory(managedSteamCmdDirectory);
                CopyDirectory(extractDirectory, managedSteamCmdDirectory);

                var managedExecutable = FindSteamCmdExecutable(managedSteamCmdDirectory);
                if (string.IsNullOrWhiteSpace(managedExecutable))
                    throw new FileNotFoundException("Managed SteamCMD executable was not found after extraction.");

                EnsureExecutablePermission(managedExecutable);
                AddProcessLog("info", $"Managed SteamCMD ready: {managedExecutable}");
                return managedExecutable;
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            logger.LogWarning(exception, "Managed SteamCMD bootstrap failed.");
            lock (_sync)
            {
                _state = "SteamCmdMissing";
                _message = $"Managed SteamCMD download failed: {exception.Message}";
                _completedAtUtc = DateTimeOffset.UtcNow;
                AddLogLocked("error", _message);
            }
            return string.Empty;
        }
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var response = await SteamCmdDownloadClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(path);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static void ExtractSteamCmdArchive(string archivePath, string extractDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, extractDirectory, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, extractDirectory, overwriteFiles: true);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);

            if (Directory.Exists(sourcePath))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static void EnsureExecutablePermission(string steamCmdPath)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                steamCmdPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private string ResolveSteamCmdPath()
    {
        var configured = Environment.GetEnvironmentVariable("QUASAR_STEAMCMD_PATH") ??
                         Environment.GetEnvironmentVariable("STEAMCMD_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var managedSteamCmdDirectory = Path.Combine(paths.ManagedToolsDirectory, "SteamCMD");
        var managed = FindSteamCmdExecutable(managedSteamCmdDirectory);
        if (!string.IsNullOrWhiteSpace(managed))
            return managed;

        return FindExecutableOnPath(OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd");
    }

    private static string FindSteamCmdExecutable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return string.Empty;

        var preferred = OperatingSystem.IsWindows()
            ? new[] { "steamcmd.exe", "steamcmd.bat" }
            : new[] { "steamcmd.sh", "steamcmd" };

        foreach (var fileName in preferred)
        {
            var match = Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(match))
                return match;
        }

        return string.Empty;
    }

    private static string FindExecutableOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return string.Empty;

        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string BuildSteamCmdUpdateArguments(string installDirectory, string loginName, bool validate)
    {
        var forcePlatform = OperatingSystem.IsWindows()
            ? string.Empty
            : "+@sSteamCmdForcePlatformType windows ";
        var validateArg = validate ? " validate" : string.Empty;

        return $"+force_install_dir {QuoteArgument(installDirectory)} {forcePlatform}+login {QuoteArgument(loginName)} +app_update {SpaceEngineersClientAppId}{validateArg} +quit";
    }

    private static ProcessStartInfo CreateSteamCmdStartInfo(string steamCmdPath, string arguments)
    {
        var workingDirectory = Path.GetDirectoryName(steamCmdPath) ?? AppContext.BaseDirectory;
        var fileName = steamCmdPath;
        var processArguments = arguments;
        var extension = Path.GetExtension(steamCmdPath);

        if (OperatingSystem.IsWindows() &&
            (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            processArguments = $"/d /s /c \"\"{steamCmdPath}\" {arguments}\"";
        }
        else if (!OperatingSystem.IsWindows() &&
                 string.Equals(extension, ".sh", StringComparison.OrdinalIgnoreCase))
        {
            fileName = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            processArguments = $"{QuoteArgument(steamCmdPath)} {arguments}";
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = processArguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string NormalizeLoginName(string loginName)
        => (loginName ?? string.Empty).Trim();

    private static bool IsSteamAccountLoginName(string loginName) =>
        !string.IsNullOrWhiteSpace(loginName) &&
        !loginName.Equals("anonymous", StringComparison.OrdinalIgnoreCase);

    private string BuildSuccessfulExitMissingContentMessage(string loginName, EntityViewerContentRootProbe contentProbe)
    {
        var manifestDiagnostics = InspectSteamAppManifest(paths.ManagedGameClientDirectory);
        return $"SteamCMD exited successfully, but no usable Space Engineers client Content folder was installed. {manifestDiagnostics} {contentProbe.Message}";
    }

    private static string InspectSteamAppManifest(string installDirectory)
    {
        var manifestPath = Path.Combine(installDirectory, "steamapps", $"appmanifest_{SpaceEngineersClientAppId}.acf");
        if (!File.Exists(manifestPath))
            return $"Steam app manifest was not found at {manifestPath}.";

        try
        {
            var text = File.ReadAllText(manifestPath);
            var sizeOnDisk = ReadAcfValue(text, "SizeOnDisk");
            var hasInstalledDepot = System.Text.RegularExpressions.Regex.IsMatch(
                text,
                "\"InstalledDepots\"\\s*\\{\\s*\"\\d+\"",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            if (sizeOnDisk == "0" && !hasInstalledDepot)
                return $"Steam app manifest reports SizeOnDisk=0 and no InstalledDepots, so app {SpaceEngineersClientAppId} was not actually downloaded.";

            return $"Steam app manifest exists at {manifestPath}, but client Content is still missing.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Steam app manifest could not be inspected: {exception.Message}";
        }
    }

    private static string ReadAcfValue(string text, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            $"\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s*\"([^\"]*)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
