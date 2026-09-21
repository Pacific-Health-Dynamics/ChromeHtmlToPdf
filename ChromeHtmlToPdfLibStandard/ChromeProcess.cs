using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ChromeHtmlToPdfLib.Enums;
using ChromeHtmlToPdfLib.Exceptions;
using ChromeHtmlToPdfLib.Helpers;
using Microsoft.Extensions.Logging;

namespace ChromeHtmlToPdfLib;

/// <summary>
///     Manages the lifecycle of a headless Chrome/Chromium process for PDF conversion.
///     Handles cross-platform binary discovery, CLI arguments, DevTools WebSocket endpoint extraction,
///     tab concurrency limiting via semaphore, and process tree termination on disposal.
/// </summary>
public sealed class ChromeProcess : IDisposable
{
    private const string UniqueEnvironmentKey = "ChromePdfConverter";

    private static readonly string[] LinuxChromePaths =
    {
        "/usr/local/sbin",
        "/usr/local/bin",
        "/usr/sbin",
        "/usr/bin",
        "/sbin",
        "/bin",
        "/opt/google/chrome"
    };

    private static readonly string[] LinuxChromeBinNames =
    {
        "google-chrome",
        "chrome",
        "chromium",
        "chromium-browser"
    };

    private readonly string? _chromeExeFileName;

    private readonly object _mutex = new();

    /// <summary>
    ///     Captures exceptions thrown during asynchronous startup events (stdout/stderr/exit) to rethrow on the calling
    ///     thread.
    /// </summary>
    private Exception? _chromeEventException;

    /// <summary>
    ///     Cached resolved path to the Chrome executable.
    /// </summary>
    private string? _chromeLocation;

    private Process? _chromeProcess;

    /// <summary>
    ///     Signal event that unblocks <see cref="EnsureRunning" /> once the DevTools WebSocket URI is parsed or startup fails.
    /// </summary>
    private ManualResetEvent? _chromeWaitEvent;

    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of <see cref="ChromeProcess" />.
    /// </summary>
    /// <param name="chromeExeFileName">Optional explicit path to the Chrome executable. If null, auto-discovery is performed.</param>
    /// <param name="userProfile">Optional path to a custom user data directory. If null, a unique temporary directory is used.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <exception cref="FileNotFoundException">Thrown when the specified Chrome executable does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified user profile directory does not exist.</exception>
    public ChromeProcess(string? chromeExeFileName = null, string? userProfile = null, ILogger? logger = null)
    {
        ResetArguments();

        if (logger != null)
            Logger = logger;

        if (string.IsNullOrWhiteSpace(chromeExeFileName))
            chromeExeFileName = ChromePath;

        if (!File.Exists(chromeExeFileName))
            throw new FileNotFoundException("Could not find chrome");

        _chromeExeFileName = chromeExeFileName;

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var userProfileDirectory = new DirectoryInfo(userProfile);
            if (!userProfileDirectory.Exists)
                throw new DirectoryNotFoundException(
                    $"The directory '{userProfileDirectory.FullName}' does not exists");

            SetDefaultArgument("--user-data-dir", $"\"{userProfileDirectory.FullName}\"");
        }
    }

    /// <summary>
    ///     Logger for diagnostics, trace events, and error reporting.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    ///     Maximum concurrent tabs allowed, read from the <c>CHROME_MAX_TABS</c> environment variable (defaults to 4).
    /// </summary>
    public static int GetMaxTabs =>
        int.TryParse(Environment.GetEnvironmentVariable("CHROME_MAX_TABS") ?? "4", out var maxTabs) ? maxTabs : 4;

    /// <summary>
    ///     Semaphore throttling concurrent page conversions across tabs.
    /// </summary>
    public SemaphoreSlim Sem { get; } = new(GetMaxTabs, GetMaxTabs);

    /// <summary>
    ///     Optional directory path used to search for the Chrome executable if not found in default locations.
    /// </summary>
    public string ChromeLocationDirectory { get; set; } = "";

    /// <summary>
    ///     DevTools Protocol WebSocket URI parsed from Chrome's startup output (e.g.
    ///     <c>ws://127.0.0.1:port/devtools/browser/guid</c>).
    /// </summary>
    public Uri? InstanceHandle { get; private set; }

    /// <summary>
    ///     OS Process ID of the running Chrome process, or <c>null</c> if not started.
    /// </summary>
    public int? ProcessId => _chromeProcess?.Id;

    /// <summary>
    ///     List of command-line arguments passed to Chrome upon process start.
    /// </summary>
    public List<string> DefaultArguments { get; private set; } = [];

    private static bool IsLinux
    {
        get
        {
            var p = (int)Environment.OSVersion.Platform;
            return p == 4 || p == 6 || p == 128;
        }
    }

    /// <summary>
    ///     Resolves and returns the full path to the Chrome/Chromium executable.
    ///     Searches default system locations on Linux/Windows or uses <see cref="ChromeLocationDirectory" />.
    /// </summary>
    /// <exception cref="ChromeException">Thrown when the Chrome executable cannot be located.</exception>
    public string ChromePath
    {
        get
        {
            if (_chromeLocation != null && !string.IsNullOrEmpty(_chromeLocation))
                return _chromeLocation;

            if (IsLinux)
            {
                foreach (var path in LinuxChromePaths)
                foreach (var bin in LinuxChromeBinNames)
                    if (File.Exists(Path.Combine(path, bin)))
                        return Path.Combine(path, bin);
                throw new ChromeException("Unable to locate chrome, try setting ChromeLocationDirectory");
            }

            // Else Windows
            var currentPath =
                new Uri(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                        throw new Exception("Failed to get assembly name")).LocalPath;

            var chrome = Path.Combine(currentPath, "chrome.exe");

            if (File.Exists(chrome))
            {
                _chromeLocation = chrome;
                return _chromeLocation;
            }

            chrome = @"c:\Program Files (x86)\Google\Chrome\Application\chrome.exe";

            if (File.Exists(chrome))
            {
                _chromeLocation = chrome;
                return _chromeLocation;
            }

            if (!string.IsNullOrEmpty(ChromeLocationDirectory)
                && File.Exists(Path.Combine(ChromeLocationDirectory, "chrome.exe")))
            {
                _chromeLocation = Path.Combine(ChromeLocationDirectory, "chrome.exe");
                return _chromeLocation;
            }

            throw new ChromeException("Unable to locate chrome, try setting ChromeLocationDirectory");
        }
    }


    private bool IsChromeRunning
    {
        get
        {
            lock (_mutex)
            {
                if (_chromeProcess == null)
                    return false;

                try
                {
                    _chromeProcess.Refresh();
                    return !_chromeProcess.HasExited;
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Failed to get chrome status {ex}");
                    return false;
                }
            }
        }
    }

    /// <summary>
    ///     Disposes the Chrome process and releases all allocated resources.
    ///     Kills the Chrome process and all spawned child processes before releasing handles.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            KillProcessAndChildren();
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Failed to kill Chrome process during Dispose");
        }
        finally
        {
            lock (_mutex)
            {
                if (_chromeProcess != null)
                {
                    try
                    {
                        _chromeProcess.OutputDataReceived -= _chromeProcess_OutputDataReceived;
                        _chromeProcess.ErrorDataReceived -= _chromeProcess_ErrorDataReceived;
                        _chromeProcess.Exited -= _chromeProcess_Exited;
                    }
                    catch
                    {
                        // Ignore event unhook failures during teardown
                    }

                    _chromeProcess.Dispose();
                    _chromeProcess = null;
                }
            }

            _chromeWaitEvent?.Dispose();
            _chromeWaitEvent = null;
            Sem.Dispose();
        }
    }

    ~ChromeProcess()
    {
        Dispose();
    }


    private void ResetArguments()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        DefaultArguments = [];

        SetDefaultArgument("--allow-pre-commit-input");
        SetDefaultArgument("--disable-background-networking");
        SetDefaultArgument("--disable-background-timer-throttling");
        SetDefaultArgument("--disable-backgrounding-occluded-windows");
        SetDefaultArgument("--disable-breakpad");
        SetDefaultArgument("--disable-client-side-phishing-detection");
        SetDefaultArgument("--disable-component-extensions-with-background-pages");
        SetDefaultArgument("--disable-crash-reporter");
        SetDefaultArgument("--disable-default-apps");
        SetDefaultArgument("--disable-dev-shm-usage");
        SetDefaultArgument("--disable-extensions");
        SetDefaultArgument("--disable-gpu");
        SetDefaultArgument("--disable-hang-monitor");
        SetDefaultArgument("--disable-infobars");
        SetDefaultArgument("--disable-ipc-flooding-protection");
        SetDefaultArgument("--disable-popup-blocking");
        SetDefaultArgument("--disable-prompt-on-repost");
        SetDefaultArgument("--disable-renderer-backgrounding");
        SetDefaultArgument("--disable-search-engine-choice-screen");
        SetDefaultArgument("--disable-sync");
        SetDefaultArgument("--disable-translate");
        SetDefaultArgument("--enable-automation");
        SetDefaultArgument("--export-tagged-pdf");
        SetDefaultArgument("--force-color-profile=srgb");
        SetDefaultArgument("--generate-pdf-document-outline");
        SetDefaultArgument("--headless", "new");
        SetDefaultArgument("--hide-scrollbars");
        SetDefaultArgument("--metrics-recording-only");
        SetDefaultArgument("--mute-audio");
        SetDefaultArgument("--no-first-run");
        SetDefaultArgument("--no-sandbox");
        SetDefaultArgument("--password-store=basic");
        SetDefaultArgument("--remote-debugging-port", "0");
        SetDefaultArgument("--safebrowsing-disable-auto-update");
        SetDefaultArgument("--use-mock-keychain");
        SetDefaultArgument("--user-data-dir", tempDirectory);
        SetWindowSize(WindowSize.HD_1366_768);
    }

    private void SetDefaultArgument(string argument)
    {
        if (!DefaultArguments.Contains(argument, StringComparison.CurrentCultureIgnoreCase))
            DefaultArguments.Add(argument);
    }

    private void SetDefaultArgument(string argument, string value)
    {
        if (IsChromeRunning)
            throw new ChromeException(
                $"Chrome is already running, you need to set the parameter '{argument}' before staring Chrome");

        for (var i = 0; i < DefaultArguments.Count; i++)
        {
            if (!DefaultArguments[i].StartsWith(argument + "=", StringComparison.InvariantCultureIgnoreCase)) continue;
            DefaultArguments[i] = argument + $"=\"{value}\"";
            return;
        }

        DefaultArguments.Add(argument + $"=\"{value}\"");
    }

    /// <summary>
    ///     Sets the initial browser viewport size in pixels via the <c>--window-size</c> argument.
    /// </summary>
    /// <param name="width">Viewport width in pixels (must be greater than 0).</param>
    /// <param name="height">Viewport height in pixels (must be greater than 0).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when width or height is less than or equal to zero.</exception>
    public void SetWindowSize(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        SetDefaultArgument("--window-size", width + "," + height);
    }

    /// <summary>
    ///     Sets the initial browser viewport size using a predefined <see cref="WindowSize" /> preset.
    /// </summary>
    /// <param name="size">The window size preset.</param>
    public void SetWindowSize(WindowSize size)
    {
        switch (size)
        {
            case WindowSize.SVGA:
                SetDefaultArgument("--window-size", 800 + "," + 600);
                break;
            case WindowSize.WSVGA:
                SetDefaultArgument("--window-size", 1024 + "," + 600);
                break;
            case WindowSize.XGA:
                SetDefaultArgument("--window-size", 1024 + "," + 768);
                break;
            case WindowSize.XGAPLUS:
                SetDefaultArgument("--window-size", 1152 + "," + 864);
                break;
            case WindowSize.WXGA_5_3:
                SetDefaultArgument("--window-size", 1280 + "," + 768);
                break;
            case WindowSize.WXGA_16_10:
                SetDefaultArgument("--window-size", 1280 + "," + 800);
                break;
            case WindowSize.SXGA:
                SetDefaultArgument("--window-size", 1280 + "," + 1024);
                break;
            case WindowSize.HD_1360_768:
                SetDefaultArgument("--window-size", 1360 + "," + 768);
                break;
            case WindowSize.HD_1366_768:
                SetDefaultArgument("--window-size", 1366 + "," + 768);
                break;
            case WindowSize.OTHER_1536_864:
                SetDefaultArgument("--window-size", 1536 + "," + 864);
                break;
            case WindowSize.HD_PLUS:
                SetDefaultArgument("--window-size", 1600 + "," + 900);
                break;
            case WindowSize.WSXGA_PLUS:
                SetDefaultArgument("--window-size", 1680 + "," + 1050);
                break;
            case WindowSize.FHD:
                SetDefaultArgument("--window-size", 1920 + "," + 1080);
                break;
            case WindowSize.WUXGA:
                SetDefaultArgument("--window-size", 1920 + "," + 1200);
                break;
            case WindowSize.OTHER_2560_1070:
                SetDefaultArgument("--window-size", 2560 + "," + 1070);
                break;
            case WindowSize.WQHD:
                SetDefaultArgument("--window-size", 2560 + "," + 1440);
                break;
            case WindowSize.OTHER_3440_1440:
                SetDefaultArgument("--window-size", 3440 + "," + 1440);
                break;
            case WindowSize._4K_UHD:
                SetDefaultArgument("--window-size", 3840 + "," + 2160);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(size), size, null);
        }
    }

    private void RemoveArgument(string argument)
    {
        if (DefaultArguments.Contains(argument))
            DefaultArguments.Remove(argument);
    }

    /// <summary>
    ///     Starts the Chrome process in headless mode if not already running.
    ///     Asynchronously waits until Chrome emits its DevTools WebSocket listening URI on stderr.
    /// </summary>
    /// <exception cref="ChromeException">Thrown if Chrome fails to start or exits prematurely during initialization.</exception>
    public void EnsureRunning()
    {
        lock (_mutex)
        {
            if (IsChromeRunning)
            {
                Logger?.LogTrace($"Chrome is already running on PID {_chromeProcess?.Id}... skipped");
                return;
            }

            _chromeEventException = null;
            var workingDirectory = Path.GetDirectoryName(_chromeExeFileName);

            Logger?.LogTrace(
                $"Starting Chrome from location '{_chromeExeFileName}' with working directory '{workingDirectory}'");
            Logger?.LogTrace($"\"{_chromeExeFileName}\" {string.Join(" ", DefaultArguments)}");

            _chromeProcess = new Process();
            var processStartInfo = new ProcessStartInfo
            {
                FileName = _chromeExeFileName,
                Arguments = string.Join(" ", DefaultArguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
#pragma warning disable CA1416
                processStartInfo.LoadUserProfile = false;
#pragma warning restore CA1416
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Failed to set user info {ex.Message}");
            }

            processStartInfo.Environment[UniqueEnvironmentKey] = UniqueEnvironmentKey;

            _chromeProcess.StartInfo = processStartInfo;

            _chromeWaitEvent = new ManualResetEvent(false);

            _chromeProcess.OutputDataReceived += _chromeProcess_OutputDataReceived;
            _chromeProcess.ErrorDataReceived += _chromeProcess_ErrorDataReceived;
            _chromeProcess.Exited += _chromeProcess_Exited;

            _chromeProcess.EnableRaisingEvents = true;

            try
            {
                _chromeProcess.Start();
            }
            catch (Exception exception)
            {
                Logger?.LogError("Could not start the Chrome process due to the following reason: " +
                                 ExceptionHelpers.GetInnerException(exception));
                throw;
            }

            Logger?.LogTrace("Chrome process started");

            _chromeProcess.BeginErrorReadLine();
            _chromeProcess.BeginOutputReadLine();

            // Block calling thread until DevTools URI is captured or process exits with error
            _chromeWaitEvent.WaitOne();

            if (_chromeEventException != null)
            {
                Logger?.LogError("Exception: " + ExceptionHelpers.GetInnerException(_chromeEventException));
                throw _chromeEventException;
            }

            Logger?.LogTrace("Chrome started");
        }
    }

    /// <summary>
    ///     Event handler invoked when the Chrome process unexpectedly exits.
    ///     Logs diagnostic exit information and unblocks <see cref="EnsureRunning" /> with an exception.
    /// </summary>
    private void _chromeProcess_Exited(object? sender, EventArgs e)
    {
        try
        {
            if (_disposed || _chromeProcess == null) return;
            Logger?.LogError("Chrome exited unexpectedly, arguments used: " + string.Join(" ", DefaultArguments));
            Logger?.LogError("Process id: " + _chromeProcess.Id);
            Logger?.LogError("Process exit time: " + _chromeProcess.ExitTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
            var exHr = Marshal.GetExceptionForHR(_chromeProcess.ExitCode);
            var exception = exHr == null ? null : ExceptionHelpers.GetInnerException(exHr);
            Logger?.LogError("Exception: " + exception);
            throw new ChromeException("Chrome exited unexpectedly, " + exception);
        }
        catch (Exception exception)
        {
            _chromeEventException = exception;
            if (_chromeProcess != null)
                _chromeProcess.Exited -= _chromeProcess_Exited;
            _chromeWaitEvent?.Set();
        }
    }

    /// <summary>
    ///     Event handler invoked when Chrome emits stderr output.
    ///     Intercepts the DevTools listening line to extract <see cref="InstanceHandle" /> and unblocks
    ///     <see cref="EnsureRunning" />.
    /// </summary>
    private void _chromeProcess_ErrorDataReceived(object sender, DataReceivedEventArgs args)
    {
        try
        {
            if (_disposed || args.Data == null) return;

            // Chrome prints: "DevTools listening on ws://127.0.0.1:port/devtools/browser/guid"
            if (args.Data.StartsWith("DevTools listening on"))
            {
                InstanceHandle = new Uri(args.Data.Replace("DevTools listening on ", string.Empty));
                Logger?.LogTrace($"Connected to dev protocol on uri '{InstanceHandle}'");
                _chromeWaitEvent?.Set();
            }
            else if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Logger?.LogWarning($"Error: {args.Data}");
            }
        }
        catch (Exception exception)
        {
            _chromeEventException = exception;
            if (_chromeProcess != null)
                _chromeProcess.ErrorDataReceived -= _chromeProcess_ErrorDataReceived;
            _chromeWaitEvent?.Set();
        }
    }

    /// <summary>
    ///     Event handler invoked when Chrome emits stdout output.
    /// </summary>
    private void _chromeProcess_OutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        try
        {
            if (_disposed || !string.IsNullOrWhiteSpace(args.Data))
                if (!_disposed && !string.IsNullOrWhiteSpace(args.Data))
                    Logger?.LogWarning($"Error: {args.Data}");
        }
        catch (Exception exception)
        {
            _chromeEventException = exception;
            if (_chromeProcess != null)
                _chromeProcess.OutputDataReceived -= _chromeProcess_OutputDataReceived;
            _chromeWaitEvent?.Set();
        }
    }

    /// <summary>
    ///     Terminates the Chrome root process and all child renderer/utility processes.
    ///     Waits up to 5 seconds for complete termination.
    /// </summary>
    private void KillProcessAndChildren()
    {
        lock (_mutex)
        {
            if (_chromeProcess == null)
                return;

            try
            {
                _chromeProcess.Refresh();
                if (_chromeProcess.HasExited)
                    return;

                Logger?.LogInformation("Stopping Chrome");

#if NETCOREAPP3_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
                    // Kills the entire process tree including renderers and GPU subprocesses
                    _chromeProcess.Kill(entireProcessTree: true);
#else
                _chromeProcess.Kill();
#endif
                _chromeProcess.WaitForExit(5000);
                Logger?.LogInformation("Chrome stopped");
            }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("is not running") && !exception.Message.Contains("has exited"))
                    Logger?.LogError(exception, exception.Message);
            }
        }
    }
}