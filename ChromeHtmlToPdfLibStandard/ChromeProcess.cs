using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using ChromeHtmlToPdfLib.Enums;
using ChromeHtmlToPdfLib.Exceptions;
using ChromeHtmlToPdfLib.Helpers;
using Microsoft.Extensions.Logging;

namespace ChromeHtmlToPdfLib
{
    public class ChromeProcess : IDisposable
    {
        private const string UniqueEnviromentKey = "ChromePdfConverter";

        /// <summary>
        ///     Chrome with it's full path
        /// </summary>
        private readonly string _chromeExeFileName;

        private readonly object mutex = new object();

        /// <summary>
        ///     Exceptions thrown from a Chrome startup event
        /// </summary>
        private Exception _chromeEventException;

        /// <summary>
        ///     Returns the location of Chrome
        /// </summary>
        private string _chromeLocation;

        /// <summary>
        ///     The process id under which Chrome is running
        /// </summary>
        private Process _chromeProcess;


        /// <summary>
        ///     Flag to wait in code when starting Chrome
        /// </summary>
        private ManualResetEvent _chromeWaitEvent;


        /// <summary>
        ///     The timeout for a conversion
        /// </summary>
        private int? _conversionTimeout;

        /// <summary>
        ///     Keeps track is we already disposed our resources
        /// </summary>
        private bool _disposed;

        /// <summary>
        ///     The password for the <see cref="_userName" />
        /// </summary>
        private string _password;

        /// <summary>
        ///     The proxy bypass list
        /// </summary>
        private string _proxyBypassList;

        /// <summary>
        ///     A proxy server
        /// </summary>
        private string _proxyServer;

        /// <summary>
        ///     The user to use when starting Chrome, when blank then Chrome is started under the code running user
        /// </summary>
        private string _userName;

        /// <summary>
        ///     A web proxy
        /// </summary>
        //private WebProxy _webProxy;
        
        private ILogger _logger;


        public ChromeProcess(string chromeExeFileName = null, string userProfile = null, ILogger logger = null)
        {
            ResetArguments();

            _logger = logger;

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
        ///     Optional path to the chrome executable;
        /// </summary>
        public string ChromeLocationDirectory { get; set; } = "";

        public Uri InstanceHandle { get; private set; }

        /// <summary>
        ///     Returns the list with default arguments that are send to Chrome when starting
        /// </summary>
        public List<string> DefaultArguments { get; private set; }

        private static bool IsLinux
        {
            get
            {
                int p = (int) Environment.OSVersion.Platform;
                return (p == 4) || (p == 6) || (p == 128);
            }
        }

        private static string[] LinuxChromePaths = new[]
        {
            "/usr/local/sbin",
            "/usr/local/bin",
            "/usr/sbin",
            "/usr/bin",
            "/sbin",
            "/bin",
            "/opt/google/chrome"
        };

        private static string[] LinuxChromeBinNames = new[]
        {
            "google-chrome",
            "chrome",
            "chromium",
            "chromium-browser"
        };
        
        /// <summary>
        ///     Returns the path to Chrome, <c>null</c> will be returned if Chrome could not be found
        /// </summary>
        /// <returns></returns>
        public string ChromePath
        {
            get
            {
                if (!string.IsNullOrEmpty(_chromeLocation))
                    return _chromeLocation;

                if (IsLinux)
                {
                    foreach (var path in LinuxChromePaths)
                    {
                        foreach (var bin in LinuxChromeBinNames)
                        {
                            if (File.Exists(Path.Combine(path, bin)))
                                return Path.Combine(path, bin);
                        }
                    }
                    throw new ChromeException("Unable to locate chrome, try setting ChromeLocationDirectory");
                }
                
                //Else Windows

                var currentPath =
                    // ReSharper disable once AssignNullToNotNullAttribute
                    new Uri(Path.GetDirectoryName(Assembly.GetExecutingAssembly().GetName().CodeBase)).LocalPath;

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
                        _logger?.LogWarning($"Failed to get chrome status {ex}");
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
            WriteToLog("Stopping Chrome");
            KillProcessAndChildren();
            WriteToLog("Chrome stopped");

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
            //SetDefaultArgument("--no-sandbox");
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
        ///     Instructs Chrome to use the provided proxy server
        /// </summary>
        /// <param name="value"></param>
        /// <example>
        ///     &lt;scheme&gt;=&lt;uri&gt;[:&lt;port&gt;][;...] | &lt;uri&gt;[:&lt;port&gt;] | "direct://"
        ///     This tells Chrome to use a custom proxy configuration. You can specify a custom proxy configuration in three ways:
        ///     1) By providing a semi-colon-separated mapping of list scheme to url/port pairs.
        ///     For example, you can specify:
        ///     "http=foopy:80;ftp=foopy2"
        ///     to use HTTP proxy "foopy:80" for http URLs and HTTP proxy "foopy2:80" for ftp URLs.
        ///     2) By providing a single uri with optional port to use for all URLs.
        ///     For example:
        ///     "foopy:8080"
        ///     will use the proxy at foopy:8080 for all traffic.
        ///     3) By using the special "direct://" value.
        ///     "direct://" will cause all connections to not use a proxy.
        /// </example>
        /// <remarks>
        ///     Set this parameter before starting Chrome
        /// </remarks>
        public void SetProxyServer(string value)
        {
            _proxyServer = value;
            SetDefaultArgument("--proxy-server", value);
        }


        /// <summary>
        ///     This tells chrome to bypass any specified proxy for the given semi-colon-separated list of hosts.
        ///     This flag must be used (or rather, only has an effect) in tandem with <see cref="SetProxyServer" />.
        ///     Note that trailing-domain matching doesn't require "." separators so "*google.com" will match "igoogle.com" for
        ///     example.
        /// </summary>
        /// <param name="values"></param>
        /// <example>
        ///     "foopy:8080" --proxy-bypass-list="*.google.com;*foo.com;127.0.0.1:8080"
        ///     will use the proxy server "foopy" on port 8080 for all hosts except those pointing to *.google.com, those pointing
        ///     to *foo.com and those pointing to localhost on port 8080.
        ///     igoogle.com requests would still be proxied. ifoo.com requests would not be proxied since *foo, not *.foo was
        ///     specified.
        /// </example>
        /// <remarks>
        ///     Set this parameter before starting Chrome
        /// </remarks>
        public void SetProxyBypassList(string values)
        {
            _proxyBypassList = values;
            SetDefaultArgument("--proxy-bypass-list", values);
        }


        /// <summary>
        ///     This tells Chrome to use the PAC file at the specified URL.
        /// </summary>
        /// <param name="value"></param>
        /// <example>
        ///     "http://wpad/windows.pac"
        ///     will tell Chrome to resolve proxy information for URL requests using the windows.pac file.
        /// </example>
        /// <remarks>
        ///     Set this parameter before starting Chrome
        /// </remarks>
        public void SetProxyPacUrl(string value)
        {
            SetDefaultArgument("--proxy-pac-url", value);
        }


        /// <summary>
        ///     This tells Chrome to use the given user-agent string
        /// </summary>
        /// <param name="value"></param>
        /// <remarks>
        ///     Set this parameter before starting Chrome
        /// </remarks>
        public void SetUserAgent(string value)
        {
            SetDefaultArgument("--user-agent", value);
        }


        /// <summary>
        ///     Sets the user under which Chrome wil run. This is useful if you are on a server and
        ///     the user under which the code runs doesn't have access to the internet.
        /// </summary>
        /// <param name="userName">The username with or without a domain name (e.g DOMAIN\USERNAME)</param>
        /// <param name="password">The password for the <paramref name="userName" /></param>
        /// <remarks>
        ///     Set this parameter before starting Chrome
        /// </remarks>
        public void SetUser(string userName, string password)
        {
            _userName = userName;
            _password = password;
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
                    WriteToLog($"Chrome is already running on PID {_chromeProcess.Id}... skipped");
                    return;
                }

                _chromeEventException = null;
                var workingDirectory = Path.GetDirectoryName(_chromeExeFileName);

                WriteToLog(
                    $"Starting Chrome from location '{_chromeExeFileName}' with working directory '{workingDirectory}'");
                WriteToLog($"\"{_chromeExeFileName}\" {string.Join(" ", DefaultArguments)}");

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
                    processStartInfo.LoadUserProfile = false;
                    
                    if (!string.IsNullOrWhiteSpace(_userName))
                    {
                        var userName = string.Empty;
                        var domain = string.Empty;

                        if (_userName.Contains("\\"))
                        {
                            userName = _userName.Split('\\')[1];
                            domain = _userName.Split('\\')[0];
                        }

                        WriteToLog($"Starting Chrome with username '{userName}' on domain '{domain}'");

                        processStartInfo.Domain = domain;
                        processStartInfo.UserName = userName;

                        var secureString = new SecureString();
                        foreach (var t in _password)
                            secureString.AppendChar(t);

                        processStartInfo.Password = secureString;
                        processStartInfo.LoadUserProfile = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to set user info {ex.Message}");
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
                    WriteToLog("Could not start the Chrome process due to the following reason: " +
                               ExceptionHelpers.GetInnerException(exception));
                    throw;
                }

                WriteToLog("Chrome process started");

                _chromeProcess.BeginErrorReadLine();
                _chromeProcess.BeginOutputReadLine();


                if (_conversionTimeout.HasValue)
                    _chromeWaitEvent.WaitOne(_conversionTimeout.Value);
                else
                    _chromeWaitEvent.WaitOne();

                if (_chromeEventException != null)
                {
                    WriteToLog("Exception: " + ExceptionHelpers.GetInnerException(_chromeEventException));
                    throw _chromeEventException;
                }

                WriteToLog("Chrome started");
            }
        }

        /// <summary>
        ///     Raised when the Chrome process exits
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _chromeProcess_Exited(object sender, EventArgs e)
        {
            try
            {
                // ReSharper disable once AccessToModifiedClosure
                if (_chromeProcess == null) return;
                WriteToLog("Chrome exited unexpectedly, arguments used: " + string.Join(" ", DefaultArguments));
                WriteToLog("Process id: " + _chromeProcess.Id);
                WriteToLog("Process exit time: " + _chromeProcess.ExitTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
                var exception =
                    ExceptionHelpers.GetInnerException(Marshal.GetExceptionForHR(_chromeProcess.ExitCode));
                WriteToLog("Exception: " + exception);
                throw new ChromeException("Chrome exited unexpectedly, " + exception);
            }
            catch (Exception exception)
            {
                _chromeEventException = exception;
                if (_chromeProcess != null)
                    _chromeProcess.Exited -= _chromeProcess_Exited;
                _chromeWaitEvent.Set();
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
                    WriteToLog($"Connected to dev protocol on uri '{InstanceHandle}'");
                    _chromeWaitEvent.Set();
                }
                else if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    WriteToLog($"Error: {args.Data}");
                }
            }
            catch (Exception exception)
            {
                _chromeEventException = exception;
                _chromeProcess.ErrorDataReceived -= _chromeProcess_ErrorDataReceived;
                _chromeWaitEvent.Set();
            }
        }

        private void WriteToLog(string message)
        {
            _logger?.LogDebug( $"Chrome PID {_chromeProcess?.Id ?? 0}: {message}");
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
                    WriteToLog($"Error: {args.Data}");
            }
            catch (Exception exception)
            {
                _chromeEventException = exception;
                _chromeProcess.OutputDataReceived -= _chromeProcess_OutputDataReceived;
                _chromeWaitEvent.Set();
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
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                    //Nothing
                }
            }

            try
            {
                _chromeProcess.CloseMainWindow();
                _chromeProcess.Kill();
            }
            catch (Exception exception)
            {
                if (!exception.Message.Contains("is not running"))
                    WriteToLog(exception.Message);
            }
        }
    }
}