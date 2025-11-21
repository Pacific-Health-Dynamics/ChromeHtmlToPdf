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
using System.Threading;
using System.Threading.Tasks;
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
    public sealed class Converter : IDisposable
    {
        /// <summary>
        ///     When set then logging is written to this stream
        /// </summary>
        private readonly ILogger? _logger;

        /// <summary>
        ///     Handles the communication with Chrome dev tools, this will be null if chrome has not started yet.
        /// </summary>
        private Browser? _browser;

        private readonly ChromeProcess _chrome;

        /// <summary>
        ///     Keeps track is we already disposed our resources
        /// </summary>
        private bool _disposed;

        /// <summary>
        ///     When set then this folder is used for temporary files
        /// </summary>
        private string? _tempDirectory;


        /// <summary>
        ///     Creates this object and sets it's needed properties
        /// </summary>
        public Converter(ChromeProcess chrome, ILogger? logger = null)
        {
            chrome.EnsureRunning();
            _chrome = chrome;
            _logger = logger;
        }


        /// <summary>
        ///     When set then this directory is used to store temporary files.
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">Raised when the given directory does not exists</exception>
        public string? TempDirectory
        {
            get => _tempDirectory;
            set
            {
                if (!Directory.Exists(value))
                    throw new DirectoryNotFoundException($"The directory '{value}' does not exists");

                _tempDirectory = value;
            }
        }


        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _browser?.Dispose();
            _browser = null;
        }

        /// <summary>
        ///     Destructor
        /// </summary>
        ~Converter()
        {
            Dispose();
        }


        /// <summary>
        ///     Converts the given <paramref name="inputUri" /> to PDF
        /// </summary>
        public async Task ConvertToPdfAsync(ConvertUri inputUri,
            Stream outputStream,
            PageSettings pageSettings,
            CancellationToken cancellationToken)
        {
            await _chrome.Sem.WaitAsync(cancellationToken);
            try
            {
                _browser = new Browser(_chrome.InstanceHandle ?? throw new Exception("InstanceHandle is null"),
                    _logger);
                _logger?.LogTrace("Connecting to chrome...");
                await _browser.ConnectAsync(cancellationToken);
                _logger?.LogTrace("Connected");

                if (inputUri.IsFile)
                    if (!File.Exists(inputUri.OriginalString))
                        throw new FileNotFoundException($"The file '{inputUri.OriginalString}' does not exists");


                _logger?.LogTrace(
                    "Loading " + (inputUri.IsFile ? "file " + inputUri.OriginalString : "url " + inputUri));

                await _browser.NavigateToAsync(inputUri, cancellationToken);

                var result = (await _browser.PrintToPdfAsync(pageSettings, cancellationToken))?.Bytes ??
                             throw new Exception("Failed to convert, no result");

                await outputStream.WriteAsync(result, 0, result.Length, cancellationToken);
                await outputStream.FlushAsync(cancellationToken);
                _logger?.LogTrace("Converted");
            }
            catch (Exception exception)
            {
                _logger?.LogError($"Error: {ExceptionHelpers.GetInnerException(exception)}'");
                throw;
            }
            finally
            {
                _chrome.Sem.Release();
            }
        }

        [Obsolete("Use ConvertToPdfAsync instead")]
        public void ConvertToPdf(ConvertUri inputUri,
            Stream outputStream,
            PageSettings pageSettings,
            CancellationToken cancellationToken = default)
        {
#pragma warning disable VSTHRD002
            ConvertToPdfAsync(inputUri, outputStream, pageSettings, cancellationToken).Wait(cancellationToken);
#pragma warning restore VSTHRD002
        }
    }
}