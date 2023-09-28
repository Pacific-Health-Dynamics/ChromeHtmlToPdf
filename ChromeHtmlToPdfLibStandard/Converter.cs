//
// Converter.cs
//
// Author: Kees van Spelde <sicos2002@hotmail.com>
//
// Copyright (c) 2017-2018 Magic-Sessions. (www.magic-sessions.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NON INFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChromeHtmlToPdfLib.Exceptions;
using ChromeHtmlToPdfLib.Helpers;
using ChromeHtmlToPdfLib.Settings;
using Microsoft.Extensions.Logging;

// ReSharper disable UnusedMember.Global

namespace ChromeHtmlToPdfLib
{
    /// <summary>
    ///     A converter class around Google Chrome headless to convert html to pdf.
    ///     You need to create a Converter for every Thread you are running or one per conversion. 
    /// </summary>
    public class Converter : IDisposable
    {
        /// <summary>
        ///     Handles the communication with Chrome dev tools, this will be null if chrome has not started yet.
        /// </summary>
        private readonly Browser _browser;

        private readonly ChromeProcess _chromeProcess;

        /// <summary>
        ///     Keeps track is we already disposed our resources
        /// </summary>
        private bool _disposed;

        /// <summary>
        ///     When set then logging is written to this stream
        /// </summary>
        private ILogger _logger;

        /// <summary>
        ///     When set then this folder is used for temporary files
        /// </summary>
        private string _tempDirectory;


        /// <summary>
        ///     Creates this object and sets it's needed properties
        /// </summary>
        /// <param name="chromeExeFileName">
        ///     When set then this has to be tThe full path to the chrome executable.
        ///     When not set then then the converter tries to find Chrome.exe by first looking in the path
        ///     where this library exists. After that it tries to find it by looking into the registry
        /// </param>
        /// <param name="userProfile">
        ///     If set then this directory will be used to store a user profile.
        ///     Leave blank or set to <c>null</c> if you want to use the default Chrome user profile location
        /// </param>
        /// <param name="logCallback">
        ///     When set then logging is written to this stream for all conversions. If
        ///     you want a separate log for each conversion then set the log stream on one of the ConvertToPdf" methods
        /// </param>
        /// <exception cref="FileNotFoundException">Raised when <see cref="chromeExeFileName" /> does not exists</exception>
        /// <exception cref="DirectoryNotFoundException">
        ///     Raised when the <paramref name="userProfile" /> directory is given but
        ///     does not exists
        /// </exception>
        public Converter(ChromeProcess chrome, ILogger logger = null)
        {
            _chromeProcess = chrome;
            _chromeProcess.EnsureRunning();
            _logger = logger;
            _browser = new Browser(chrome.InstanceHandle);
        }


        /// <summary>
        ///     An unique id that can be used to identify the logging of the converter when
        ///     calling the code from multiple threads and writing all the logging to the same file
        /// </summary>
        public string InstanceId { get; set; }


        /// <summary>
        ///     When set then this directory is used to store temporary files.
        ///     For example files that are made in combination with <see cref="PreWrapExtensions" />
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">Raised when the given directory does not exists</exception>
        public string TempDirectory
        {
            get => _tempDirectory;
            set
            {
                if (!Directory.Exists(value))
                    throw new DirectoryNotFoundException($"The directory '{value}' does not exists");

                _tempDirectory = value;
            }
        }

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_browser != null)
            {
                _browser.CloseAsync().Wait();
                _browser.Dispose();
            }
        }

        #endregion


        #region CheckIfOutputFolderExists

        /// <summary>
        ///     Checks if the path to the given <paramref name="outputFile" /> exists.
        ///     An <see cref="DirectoryNotFoundException" /> is thrown when the path is not valid
        /// </summary>
        /// <param name="outputFile"></param>
        /// <exception cref="DirectoryNotFoundException"></exception>
        private void CheckIfOutputFolderExists(string outputFile)
        {
            var directory = new FileInfo(outputFile).Directory;
            if (directory != null && !directory.Exists)
                throw new DirectoryNotFoundException($"The path '{directory.FullName}' does not exists");
        }

        #endregion


        #region WriteToLog

        /// <summary>
        ///     Writes a line and linefeed to the <see cref="_logger" />
        /// </summary>
        /// <param name="message">The message to write</param>
        private void WriteToLog(string message)
        {
            _logger?.LogDebug($"Instance {InstanceId ?? ""}: {message}");   
        }

        #endregion

        /// <summary>
        ///     Destructor
        /// </summary>
        ~Converter()
        {
            Dispose();
        }


        #region ConvertToPdf

        /// <summary>
        ///     Converts the given <paramref name="inputUri" /> to PDF
        /// </summary>
        public async Task ConvertToPdfAsync(ConvertUri inputUri,
            Stream outputStream,
            PageSettings pageSettings,
            CancellationToken cancellationToken = default)
        {
            if (inputUri.IsFile)
                if (!File.Exists(inputUri.OriginalString))
                    throw new FileNotFoundException($"The file '{inputUri.OriginalString}' does not exists");

            try
            {
                WriteToLog("Loading " + (inputUri.IsFile ? "file " + inputUri.OriginalString : "url " + inputUri));

                await _browser.NavigateToAsync(inputUri, cancellationToken);

                using (var memoryStream = new MemoryStream((await _browser.PrintToPdfAsync(pageSettings, cancellationToken)).Bytes))
                {
                    memoryStream.Position = 0;
                    await memoryStream.CopyToAsync(outputStream);
                }

                WriteToLog("Converted");
            }
            catch (Exception exception)
            {
                WriteToLog($"Error: {ExceptionHelpers.GetInnerException(exception)}'");
                throw;
            }
        }

        public void ConvertToPdf(ConvertUri inputUri,
            Stream outputStream,
            PageSettings pageSettings,
            CancellationToken cancellationToken = default)
        {
            ConvertToPdfAsync(inputUri, outputStream, pageSettings, cancellationToken).Wait(cancellationToken);
        }

        #endregion
    }
}