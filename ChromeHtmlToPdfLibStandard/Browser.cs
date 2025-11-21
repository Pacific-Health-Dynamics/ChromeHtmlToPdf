//
// Communicator.cs
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
using System.Threading;
using System.Threading.Tasks;
using ChromeHtmlToPdfLib.Exceptions;
using ChromeHtmlToPdfLib.Protocol;
using ChromeHtmlToPdfLib.Settings;
using Microsoft.Extensions.Logging;

namespace ChromeHtmlToPdfLib
{
    /// <summary>
    ///     Handles all the communication tasks with Chrome remote devtools
    /// </summary>
    /// <remarks>
    ///     See https://chromium.googlesource.com/v8/v8/+/master/src/inspector/js_protocol.json
    /// </remarks>
    internal sealed class Browser : IDisposable
    {
        /// <summary>
        ///     A connection to the browser (Chrome)
        /// </summary>
        private readonly Connection _browserConnection;

        private readonly Uri _webSocketUri;
        private readonly ILogger? _logger;

        /// <summary>
        ///     A connection to a page
        /// </summary>
        private Connection? _pageConnection;

        /// <summary>
        ///     Makes this object and sets the Chrome remote debugging url
        /// </summary>
        internal Browser(Uri browser, ILogger? logger)
        {
            _logger = logger;
            _webSocketUri = browser;
            // Open a websocket to the browser
            _browserConnection = new Connection(null, browser.ToString(), _logger);
        }


        /// <summary>
        ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            try
            {
#pragma warning disable VSTHRD002
                CloseAsync().Wait(5000);
#pragma warning restore VSTHRD002
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to close chrome tab");
            }

            _pageConnection?.Dispose();
            _browserConnection.Dispose();
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            _logger?.LogTrace($"Connecting to chrome at {_webSocketUri}..");
            await _browserConnection.ConnectAsync(cancellationToken);
            _logger?.LogTrace("Connected");


            var message = new Message("Target.createTarget");
            message.Parameters.Add("url", "about:blank");

            Page? page = null;
            var result = "";
            var count = 0;
            while (count < 3)
            {
                count++;
                using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                source.CancelAfter(TimeSpan.FromSeconds(10));
                _logger?.LogTrace("Creating chrome tab/page...");
                result = await _browserConnection.SendAsync(message, source.Token);
                page = Page.FromJson(result);
                if (page?.Result?.TargetId != null)
                    break;
            }

            var targetId = page?.Result?.TargetId;

            if (targetId == null)
                throw new ConversionException(
                    $"Failed to open a page, null. Url: {_webSocketUri}, Result is: {result}");

            // ws://localhost:9222/devtools/page/BA386DE8075EB19DDCE459B4B623FBE7
            // ws://127.0.0.1:50841/devtools/browser/9a919bf0-b243-479d-8396-ede653356e12
            var pageUrl =
                $"{_webSocketUri.Scheme}://{_webSocketUri.Host}:{_webSocketUri.Port}/devtools/page/{targetId}";
            _logger?.LogTrace($"Tab/page created at targetId: {targetId}, connecting to page at url: {pageUrl}...");
            _pageConnection = new Connection(targetId, pageUrl, _logger);
            await _pageConnection.ConnectAsync(cancellationToken);
            await _pageConnection.SendUnitAsync(new Message("Page.enable"), cancellationToken);
            _logger?.LogTrace("Connected");
        }


        /// <summary>
        ///     Instructs Chrome to navigate to the given <paramref name="uri" />
        /// </summary>
        public async Task NavigateToAsync(Uri uri, CancellationToken cancellationToken)
        {
            var conn = _pageConnection ?? throw new ConversionException("Call Browser.Connect first");
            var loaded = false;
            for (var i = 1; i < 4; i++)
            {
                var timeOut = TimeSpan.FromSeconds(15 * i);
                using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                source.CancelAfter(timeOut);
                var pageReady = new TaskCompletionSource<bool>();
                conn.AddMessageListener(pageReady, data =>
                {
                    var message = data.Message;
                    if (data.Error != null)
                    {
                        pageReady.TrySetResult(false);
                    }
                    else if (message != null)
                    {
                        var page = PageEvent.FromJson(message);

                        switch (page?.Method)
                        {
                            //case "Page.lifecycleEvent" when page.Params?.Name == "DOMContentLoaded":
                            //case "Page.frameStoppedLoading":
                            case "Page.loadEventFired":
                                pageReady.TrySetResult(true);
                                break;
                        }
                    }
                });

                try
                {
                    var message = new Message("Page.navigate");
                    message.AddParameter("url", uri.ToString());

                    _logger?.LogTrace($"Navigating to to page: {uri}");
                    await conn.SendAsync(message, source.Token);
                    _logger?.LogTrace("Navigated");
#if NET6_0_OR_GREATER
                    loaded = await pageReady.Task.WaitAsync(timeOut, source.Token);
#else
#pragma warning disable VSTHRD003
                    loaded = await Task.Run(async () => await pageReady.Task, source.Token);
#pragma warning restore VSTHRD003
#endif
                    break;
                }
                catch (Exception e)
                {
                    _logger?.LogError($"Failed to load page content, {e.Message}, {e}");
                }
                finally
                {
                    conn.RemoveMessageListener(pageReady);
                }
            }

            if (!loaded)
            {
                _logger.LogTrace("!!! DID NOT Received Page.loadEventFired !!!");
                throw new ConversionException("Failed to open chrome tab and load document");
            }
        }

        /// <summary>
        ///     Instructs Chrome to print the page
        /// </summary>
        internal async Task<PrintToPdfResponse?> PrintToPdfAsync(PageSettings pageSettings,
            CancellationToken cancellationToken)
        {
            var conn = _pageConnection ?? throw new ConversionException("Call Browser.Connect first");

            var message = new Message("Page.printToPDF");
            message.AddParameter("landscape", pageSettings.Landscape);
            message.AddParameter("displayHeaderFooter", pageSettings.DisplayHeaderFooter);
            message.AddParameter("printBackground", pageSettings.PrintBackground);
            message.AddParameter("scale", pageSettings.Scale);
            message.AddParameter("paperWidth", pageSettings.PaperWidth);
            message.AddParameter("paperHeight", pageSettings.PaperHeight);
            message.AddParameter("marginTop", pageSettings.MarginTop);
            message.AddParameter("marginBottom", pageSettings.MarginBottom);
            message.AddParameter("marginLeft", pageSettings.MarginLeft);
            message.AddParameter("marginRight", pageSettings.MarginRight);
            message.AddParameter("pageRanges", pageSettings.PageRanges ?? string.Empty);
            message.AddParameter("ignoreInvalidPageRanges", pageSettings.IgnoreInvalidPageRanges);
            if (pageSettings.HeaderTemplate != null && !string.IsNullOrEmpty(pageSettings.HeaderTemplate))
                message.AddParameter("headerTemplate", pageSettings.HeaderTemplate);
            if (pageSettings.FooterTemplate != null && !string.IsNullOrEmpty(pageSettings.FooterTemplate))
                message.AddParameter("footerTemplate", pageSettings.FooterTemplate);
            message.AddParameter("preferCSSPageSize", pageSettings.PreferCSSPageSize);

            var result = await conn.SendAsync(message, cancellationToken);

            var printToPdfResponse = PrintToPdfResponse.FromJson(result);

            if (string.IsNullOrEmpty(printToPdfResponse?.Result?.Data))
                throw new ConversionException("Conversion failed");

            return printToPdfResponse;
        }


        private async Task CloseAsync()
        {
            var conn = _pageConnection;
            if (conn == null)
                return;

            var target = conn.TargetId;
            if (target != null)
            {
                var message = new Message("Target.closeTarget");
                message.AddParameter("targetId", target);
                await conn.SendUnitAsync(message, default);
            }
        }
    }
}