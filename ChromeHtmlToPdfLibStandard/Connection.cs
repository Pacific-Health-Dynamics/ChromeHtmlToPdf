using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChromeHtmlToPdfLib.Exceptions;
using ChromeHtmlToPdfLib.Protocol;
using Microsoft.Extensions.Logging;

namespace ChromeHtmlToPdfLib
{
    internal sealed class Event
    {
        public string? Message { get; set; }
        public Exception? Error { get; set; }
    }

    /// <summary>
    ///     A connection to a page (tab) in Chrome
    /// </summary>
    internal sealed class Connection : IDisposable
    {
        private readonly Dictionary<int, TaskCompletionSource<string>> _completionSources =
            new Dictionary<int, TaskCompletionSource<string>>();

        private readonly Dictionary<object, Action<Event>> _listeners = new Dictionary<object, Action<Event>>();
        private readonly ILogger? _logger;
        private readonly object _mutex;
        private readonly CancellationTokenSource _source;
        private readonly ClientWebSocket _ws;
        private bool _disposed;

        /// <summary>
        ///     Makes this object and sets all it's needed properties
        /// </summary>
        internal Connection(string? targetId, string url, ILogger? logger)
        {
            _mutex = this;
            _source = new CancellationTokenSource();
            _logger = logger;
            TargetId = targetId;
            Url = url;
            _ws = new ClientWebSocket();
        }

        public string? TargetId { get; set; }


        /// <summary>
        ///     Returns the websocket url
        /// </summary>
        private string Url { get; }


        /// <summary>
        ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            try
            {
                lock (_mutex)
                {
                    _listeners.Clear();
                    _source.Cancel();
                    _source.Dispose();
                    _ws.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to dispose web socket. {ex.Message} {ex.StackTrace}");
            }

            _disposed = true;
        }

        public void AddMessageListener(object id, Action<Event> listener)
        {
            lock (_mutex)
            {
                if (!_listeners.ContainsKey(id))
                    _listeners.Add(id, listener);
            }
        }

        public bool RemoveMessageListener(object id)
        {
            lock (_mutex)
            {
                return _listeners.Remove(id);
            }
        }

        private void Notify(Event message)
        {
            List<Action<Event>> actions;
            lock (_mutex)
            {
                actions = _listeners.Values.ToList();
            }

            actions.ForEach(l =>
            {
                try
                {
                    l.Invoke(message);
                }
                catch (Exception lex)
                {
                    _logger?.LogError(lex, "Listener failed");
                }
            });
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var token = _source.Token;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, token);
            await _ws.ConnectAsync(new Uri(Url), linked.Token);

            Task unused = Task.Run<Task>(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var stream = new MemoryStream();
                        while (!token.IsCancellationRequested)
                        {
                            var buffer = new byte[2097152];
                            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            if (result.MessageType != WebSocketMessageType.Text)
                                break;
                            await stream.WriteAsync(buffer, 0, result.Count, token);
                            _logger?.LogTrace($"Received {result.Count} bytes");
                            if (result.EndOfMessage)
                            {
                                var str = Encoding.UTF8.GetString(stream.ToArray());
                                _logger?.LogTrace($"Received message {str}");

                                Notify(new Event
                                {
                                    Message = str
                                });

                                var baseMessage = MessageBase.FromJson(str);
                                if (baseMessage != null)
                                    lock (_mutex)
                                    {
                                        if (_completionSources.TryGetValue(baseMessage.Id, out var source))
                                        {
                                            _completionSources.Remove(baseMessage.Id);
                                            source.SetResult(str);
                                        }
                                    }

                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _logger?.LogError($"Websocket reader closed: {ex.Message} {ex.StackTrace}");
                        Notify(new Event
                        {
                            Message = null,
                            Error = ex
                        });
                    }
                }
            }, token);
        }


        /// <summary>
        ///     Sends a message asynchronously to the <see cref="WebSocket" />
        /// </summary>
        internal async Task<string> SendAsync(Message message, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _source.Token);
            var json = message.ToJson();
            _logger?.LogTrace($"Sending message: '{json}'");
            var completionSource = new TaskCompletionSource<string>();
            lock (_completionSources)
            {
                _completionSources.Add(message.Id, completionSource);
            }

            string? res = null;
            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text,
                true,
                linked.Token);

#if NET6_0_OR_GREATER
            res = await completionSource.Task.WaitAsync(linked.Token);
#else
#pragma warning disable VSTHRD003
            res = await Task.Run(async () => await completionSource.Task, linked.Token);
#pragma warning restore VSTHRD003
#endif
            CheckForError(res);
            return res;
        }

        internal async Task SendUnitAsync(Message message, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _source.Token);
            var json = message.ToJson();
            _logger?.LogTrace($"Sending message: '{json}'");
            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text,
                true,
                linked.Token);
        }


        /// <summary>
        ///     Checks if <paramref name="message" /> contains an error and if so raises an exception
        /// </summary>
        /// <param name="message"></param>
        private void CheckForError(string message)
        {
            var error = Error.FromJson(message);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (error?.InnerError != null && error.InnerError.Code != 0)
                throw new ChromeException(error.InnerError.Message ?? "Chrome internal error");
        }
    }
}