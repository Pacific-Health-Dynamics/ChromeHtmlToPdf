using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ChromeHtmlToPdfLib.Enums;
using ChromeHtmlToPdfLib.Exceptions;
using ChromeHtmlToPdfLib.Helpers;
using Microsoft.Extensions.Logging;

namespace ChromeHtmlToPdfLib
{
    public sealed class ChromeProcess : IDisposable
    {
        private const string UniqueEnviromentKey = "ChromePdfConverter";

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

        /// <summary>
        ///     Chrome with it's full path
        /// </summary>
        private readonly string? _chromeExeFileName;

        private readonly object mutex = new object();

        /// <summary>
        ///     Exceptions thrown from a Chrome startup event
        /// </summary>
        private Exception? _chromeEventException;

        /// <summary>
        ///     Returns the location of Chrome
        /// </summary>
        private string? _chromeLocation;

        /// <summary>
        ///     The process id under which Chrome is running
        /// </summary>
        private Process? _chromeProcess;


        /// <summary>
        ///     Flag to wait in code when starting Chrome
        /// </summary>
        private ManualResetEvent? _chromeWaitEvent;


        /// <summary>
        ///     Keeps track is we already disposed our resources
        /// </summary>
        private bool _disposed;

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
        ///     A web proxy
        /// </summary>
        //private WebProxy _webProxy;
        public ILogger? Logger { get; set; }

        public SemaphoreSlim Sem { get; } = new SemaphoreSlim(4, 4);

        /// <summary>
        ///     Optional path to the chrome executable;
        /// </summary>
        public string ChromeLocationDirectory { get; set; } = "";

        public Uri? InstanceHandle { get; private set; }

        /// <summary>
        ///     Returns the list with default arguments that are send to Chrome when starting
        /// </summary>
        public List<string> DefaultArguments { get; private set; } = new List<string>(0);

        private static bool IsLinux
        {
            get
            {
                var p = (int)Environment.OSVersion.Platform;
                return p == 4 || p == 6 || p == 128;
            }
        }

        /// <summary>
        ///     Returns the path to Chrome, <c>null</c> will be returned if Chrome could not be found
        /// </summary>
        /// <returns></returns>
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

                //Else Windows

                var currentPath =
                    // ReSharper disable once AssignNullToNotNullAttribute
                    new Uri(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                            throw new Exception("Failed to get assembly name")).LocalPath;

                // ReSharper disable once AssignNullToNotNullAttribute
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


        /// <summary>
        ///     Returns <c>true</c> when Chrome is running
        /// </summary>
        /// <returns></returns>
        private bool IsChromeRunning
        {
            get
            {
                lock (mutex)
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

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (!IsChromeRunning)
            {
                _chromeProcess = null;
                return;
            }

            // Sometimes Chrome does not close all processes so kill them
            Logger?.LogInformation("Stopping Chrome");
            KillProcessAndChildren();
            Logger?.LogInformation("Chrome stopped");

            _chromeProcess = null;
        }

        ~ChromeProcess()
        {
            Dispose();
        }


        /// <summary>
        ///     Resets the <see cref="DefaultArguments" /> to their default settings
        /// </summary>
        private void ResetArguments()
        {
            DefaultArguments = new List<string>();
            SetDefaultArgument("--headless");
            SetDefaultArgument("--disable-gpu");
            SetDefaultArgument("--hide-scrollbars");
            SetDefaultArgument("--mute-audio");
            SetDefaultArgument("--disable-background-networking");
            SetDefaultArgument("--disable-background-timer-throttling");
            SetDefaultArgument("--disable-default-apps");
            SetDefaultArgument("--disable-extensions");
            SetDefaultArgument("--disable-hang-monitor");
            //SetDefaultArgument("--disable-popup-blocking");
            // ReSharper disable once StringLiteralTypo
            SetDefaultArgument("--disable-prompt-on-repost");
            SetDefaultArgument("--disable-sync");
            SetDefaultArgument("--disable-translate");
            SetDefaultArgument("--metrics-recording-only");
            SetDefaultArgument("--no-first-run");
            SetDefaultArgument("--disable-crash-reporter");
            //SetDefaultArgument("--allow-insecure-localhost");
            // ReSharper disable once StringLiteralTypo
            SetDefaultArgument("--safebrowsing-disable-auto-update");
            //You can disable this, but when running in a container you need either this or docker run --cap-add=SYS_ADMIN
            SetDefaultArgument("--no-sandbox");
            SetDefaultArgument("--remote-debugging-port", "0");
            SetWindowSize(WindowSize.HD_1366_768);
        }

        /// <summary>
        ///     Adds an extra conversion argument to the <see cref="DefaultArguments" />
        /// </summary>
        /// <remarks>
        ///     This is a one time only default setting which can not be changed when doing multiple conversions.
        ///     Set this before doing any conversions.
        /// </remarks>
        /// <param name="argument"></param>
        private void SetDefaultArgument(string argument)
        {
            if (!DefaultArguments.Contains(argument, StringComparison.CurrentCultureIgnoreCase))
                DefaultArguments.Add(argument);
        }

        // <summary>
        /// Adds an extra conversion argument with value to the
        /// <see cref="DefaultArguments" />
        /// or replaces it when it already exists
        /// </summary>
        /// <remarks>
        ///     This is a one time only default setting which can not be changed when doing multiple conversions.
        ///     Set this before doing any conversions.
        /// </remarks>
        /// <param name="argument"></param>
        /// <param name="value"></param>
        private void SetDefaultArgument(string argument, string value)
        {
            if (IsChromeRunning)
                throw new ChromeException(
                    $"Chrome is already running, you need to set the parameter '{argument}' before staring Chrome");


            for (var i = 0; i < DefaultArguments.Count; i++)
            {
                if (!DefaultArguments[i].StartsWith(argument + "=")) continue;
                DefaultArguments[i] = argument + $"=\"{value}\"";
                return;
            }

            DefaultArguments.Add(argument + $"=\"{value}\"");
        }

        /// <summary>
        ///     Sets the viewport size to use when converting
        /// </summary>
        /// <remarks>
        ///     This is a one time only default setting which can not be changed when doing multiple conversions.
        ///     Set this before doing any conversions.
        /// </remarks>
        /// <param name="width">The width</param>
        /// <param name="height">The height</param>
        /// <exception cref="ArgumentOutOfRangeException">
        ///     Raised when <paramref name="width" /> or
        ///     <paramref name="height" /> is smaller then or zero
        /// </exception>
        public void SetWindowSize(int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));

            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            SetDefaultArgument("--window-size", width + "," + height);
        }

        // <summary>
        /// Sets the window size to use when converting
        /// </summary>
        /// <param name="size"></param>
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


        /// <summary>
        ///     Removes the given <paramref name="argument" /> from <see cref="DefaultArguments" />
        /// </summary>
        /// <param name="argument"></param>
        // ReSharper disable once UnusedMember.Local
        private void RemoveArgument(string argument)
        {
            if (DefaultArguments.Contains(argument))
                DefaultArguments.Remove(argument);
        }

        /// <summary>
        ///     Start Chrome headless
        /// </summary>
        /// <remarks>
        ///     If Chrome is already running then this step is skipped
        /// </remarks>
        /// <exception cref="ChromeException"></exception>
        public void EnsureRunning()
        {
            lock (mutex)
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
                    // ReSharper disable once AssignNullToNotNullAttribute
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

                processStartInfo.Environment[UniqueEnviromentKey] = UniqueEnviromentKey;

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
        ///     Raised when the Chrome process exits
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _chromeProcess_Exited(object? sender, EventArgs e)
        {
            try
            {
                // ReSharper disable once AccessToModifiedClosure
                if (_chromeProcess == null) return;
                Logger?.LogError("Chrome exited unexpectedly, arguments used: " + string.Join(" ", DefaultArguments));
                Logger?.LogError("Process id: " + _chromeProcess.Id);
                Logger?.LogError("Process exit time: " + _chromeProcess.ExitTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
                var exHr = Marshal.GetExceptionForHR(_chromeProcess.ExitCode);
                var exception = exHr == null ? null : ExceptionHelpers.GetInnerException(exHr);
                ;
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
        ///     Raised when Chrome sends data to the error output
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void _chromeProcess_ErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            try
            {
                if (args.Data == null) return;

                //This is emitted when chrome started
                if (args.Data.StartsWith("DevTools listening on"))
                {
                    // ReSharper disable once CommentTypo
                    // DevTools listening on ws://127.0.0.1:50160/devtools/browser/53add595-f351-4622-ab0a-5a4a100b3eae
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
        ///     Raised when Chrome send data to the standard output
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void _chromeProcess_OutputDataReceived(object sender, DataReceivedEventArgs args)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
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
        ///     Kill the process with given id and all it's children
        /// </summary>
        /// <param name="processId">The process id</param>
        private void KillProcessAndChildren()
        {
            var procs = Process.GetProcesses()
                .Where(proc =>
                {
                    try
                    {
                        return proc.ProcessName.ToLower().Contains("chrome") &&
                               proc.StartInfo.Environment.ContainsKey(UniqueEnviromentKey);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                })
                .ToList();

            foreach (var proc in procs)
                try
                {
                    proc.Kill();
                }
                catch
                {
                    //Nothing
                }

            try
            {
                _chromeProcess?.CloseMainWindow();
                _chromeProcess?.Kill();
            }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("is not running"))
                    Logger?.LogError(exception, exception.Message);
            }
        }
    }
}