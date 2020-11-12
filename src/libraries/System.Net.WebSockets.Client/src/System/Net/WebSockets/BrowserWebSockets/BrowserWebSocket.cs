// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Runtime.InteropServices.JavaScript;

using JavaScript = System.Runtime.InteropServices.JavaScript;

namespace System.Net.WebSockets
{
    // **Note** on `Task.ConfigureAwait(continueOnCapturedContext: true)` for the WebAssembly Browser.
    // The current implementation of WebAssembly for the Browser does not have a SynchronizationContext nor a Scheduler
    // thus forcing the callbacks to run on the main browser thread.  When threading is eventually implemented using
    // emscripten's threading model of remote worker threads, via SharedArrayBuffer, any API calls will have to be
    // remoted back to the main thread.  Most APIs only work on the main browser thread.
    // During discussions the concensus has been that it will not matter right now which value is used for ConfigureAwait
    // we should put this in place now.

    /// <summary>
    /// Provides a client for connecting to WebSocket services.
    /// </summary>
    internal sealed class BrowserWebSocket : WebSocket
    {
        private readonly Channel<ReceivePayload> _receiveMessageQueue = Channel.CreateUnbounded<ReceivePayload>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = true,
        });

        private TaskCompletionSource? _tcsClose;
        private WebSocketCloseStatus? _innerWebSocketCloseStatus;
        private string? _innerWebSocketCloseStatusDescription;

        private JSObject? _innerWebSocket;

        private Action<JSObject>? _onOpen;
        private Action<JSObject>? _onError;
        private Action<JSObject>? _onClose;
        private Action<JSObject>? _onMessage;

        private MemoryStream? _writeBuffer;
        private ReceivePayload? _bufferedPayload;
        private readonly CancellationTokenSource _cts;

        // Stages of this class.
        private int _state;

        private enum InternalState
        {
            Created = 0,
            Connecting = 1,
            Connected = 2,
            Disposed = 3
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="System.Net.WebSockets.BrowserWebSocket"/> class.
        /// </summary>
        public BrowserWebSocket()
        {
            _cts = new CancellationTokenSource();
        }

        #region Properties

        /// <summary>
        /// Gets the WebSocket state of the <see cref="System.Net.WebSockets.BrowserWebSocket"/> instance.
        /// </summary>
        /// <value>The state.</value>
        public override WebSocketState State
        {
            get
            {
                if (_innerWebSocket != null && !_innerWebSocket.IsDisposed)
                {
                    return ReadyStateToDotNetState((int)_innerWebSocket.GetObjectProperty("readyState"));
                }
                return (InternalState)_state switch
                {
                    InternalState.Created => WebSocketState.None,
                    InternalState.Connecting => WebSocketState.Connecting,
                    _ => WebSocketState.Closed
                };
            }
        }

        private static WebSocketState ReadyStateToDotNetState(int readyState) =>
            // https://developer.mozilla.org/en-US/docs/Web/API/WebSocket/readyState
            readyState switch
            {
                0 => WebSocketState.Connecting, // 0 (CONNECTING)
                1 => WebSocketState.Open, // 1 (OPEN)
                2 => WebSocketState.CloseSent, // 2 (CLOSING)
                3 => WebSocketState.Closed, // 3 (CLOSED)
                _ => WebSocketState.None
            };

        public override WebSocketCloseStatus? CloseStatus => _innerWebSocket == null ? null : _innerWebSocketCloseStatus;

        public override string? CloseStatusDescription => _innerWebSocket == null ? null : _innerWebSocketCloseStatusDescription;

        public override string? SubProtocol => _innerWebSocket != null && !_innerWebSocket.IsDisposed ? _innerWebSocket!.GetObjectProperty("protocol")?.ToString() : null;

        #endregion Properties

        internal async Task ConnectAsyncJavaScript(Uri uri, CancellationToken cancellationToken, List<string>? requestedSubProtocols)
        {
            // Check that we have not started already
            int priorState = Interlocked.CompareExchange(ref _state, (int)InternalState.Connecting, (int)InternalState.Created);
            if (priorState == (int)InternalState.Disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            else if (priorState != (int)InternalState.Created)
            {
                throw new InvalidOperationException(SR.net_WebSockets_AlreadyStarted);
            }

            TaskCompletionSource tcsConnect = new TaskCompletionSource();

            // For Abort/Dispose.  Calling Abort on the request at any point will close the connection.
            _cts.Token.Register(s => ((BrowserWebSocket)s!).AbortRequest(), this);

            try
            {
                if (requestedSubProtocols?.Count > 0)
                {
                    using (JavaScript.Array subProtocols = new JavaScript.Array())
                    {
                        foreach (string item in requestedSubProtocols)
                        {
                            subProtocols.Push(item);
                        }
                        _innerWebSocket = new HostObject("WebSocket", uri.ToString(), subProtocols);
                    }
                }
                else
                {
                    _innerWebSocket = new HostObject("WebSocket", uri.ToString());
                }
                _innerWebSocket.SetObjectProperty("binaryType", "arraybuffer");

                // Setup the onError callback
                _onError = errorEvt => errorEvt.Dispose();

                // Attach the onError callback
                _innerWebSocket.SetObjectProperty("onerror", _onError);

                // Setup the onClose callback
                _onClose = (closeEvt) =>
                {
                    using (closeEvt)
                    {
                        _innerWebSocketCloseStatus = (WebSocketCloseStatus)closeEvt.GetObjectProperty("code");
                        _innerWebSocketCloseStatusDescription = closeEvt.GetObjectProperty("reason")?.ToString();
                        _receiveMessageQueue.Writer.TryWrite(new ReceivePayload(Array.Empty<byte>(), WebSocketMessageType.Close));
                        NativeCleanup();
                        if ((InternalState)_state == InternalState.Connecting)
                        {
                            tcsConnect.TrySetException(new WebSocketException(WebSocketError.NativeError));
                        }
                        else
                        {
                            _tcsClose?.SetResult();
                        }
                    }
                };

                // Attach the onClose callback
                _innerWebSocket.SetObjectProperty("onclose", _onClose);

                // Setup the onOpen callback
                _onOpen = (evt) =>
                {
                    using (evt)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            // Change internal _state to 'Connected' to enable the other methods
                            if (Interlocked.CompareExchange(ref _state, (int)InternalState.Connected, (int)InternalState.Connecting) != (int)InternalState.Connecting)
                            {
                                // Aborted/Disposed during connect.
                                tcsConnect.TrySetException(new ObjectDisposedException(GetType().FullName));
                            }
                            else
                            {
                                tcsConnect.SetResult();
                            }
                        }
                        else
                        {
                            tcsConnect.SetCanceled(cancellationToken);
                        }
                    }
                };

                // Attach the onOpen callback
                _innerWebSocket.SetObjectProperty("onopen", _onOpen);

                // Setup the onMessage callback
                _onMessage = (messageEvent) => onMessageCallback(messageEvent);

                // Attach the onMessage callaback
                _innerWebSocket.SetObjectProperty("onmessage", _onMessage);
                await tcsConnect.Task.ConfigureAwait(continueOnCapturedContext: true);
            }
            catch (Exception wse)
            {
                Dispose();
                WebSocketException wex = new WebSocketException(SR.net_webstatus_ConnectFailure, wse);
                throw wex;
            }
        }

        private void onMessageCallback(JSObject messageEvent)
        {
            // get the events "data"
            using (messageEvent)
            {
                ThrowIfNotConnected();
                // If the messageEvent's data property is marshalled as a JSObject then we are dealing with
                // binary data
                object eventData = messageEvent.GetObjectProperty("data");
                switch (eventData)
                {
                    case ArrayBuffer buffer:
                        using (buffer)
                        {
                            _receiveMessageQueue.Writer.TryWrite(new ReceivePayload(buffer, WebSocketMessageType.Binary));
                            break;
                        }
                    case JSObject blobData:
                        using (blobData)
                        {
                            // Create a new "FileReader" object
                            using (HostObject reader = new HostObject("FileReader"))
                            {
                                Action<JSObject> loadend = (loadEvent) =>
                                {
                                    using (loadEvent)
                                    using (JSObject target = (JSObject)loadEvent.GetObjectProperty("target"))
                                    {
                                        // https://developer.mozilla.org/en-US/docs/Web/API/FileReader/readyState
                                        if ((int)target.GetObjectProperty("readyState") == 2) // DONE - The operation is complete.
                                        {
                                            using (ArrayBuffer binResult = (ArrayBuffer)target.GetObjectProperty("result"))
                                            {
                                                _receiveMessageQueue.Writer.TryWrite(new ReceivePayload(binResult, WebSocketMessageType.Binary));
                                            }
                                        }
                                    }
                                };
                                reader.Invoke("addEventListener", "loadend", loadend);
                                reader.Invoke("readAsArrayBuffer", blobData);
                            }
                            break;
                        }
                    case string message:
                        {
                            _receiveMessageQueue.Writer.TryWrite(new ReceivePayload(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text));
                            break;
                        }
                    default:
                        throw new NotImplementedException(SR.Format(SR.net_WebSockets_Invalid_Binary_Type, _innerWebSocket?.GetObjectProperty("binaryType").ToString()));
                }
            }
        }

        private void NativeCleanup()
        {
            // We need to clear the events on websocket as well or stray events
            // are possible leading to crashes.
            if (_onClose != null)
            {
                _innerWebSocket?.SetObjectProperty("onclose", "");
                _onClose = null;
            }
            if (_onError != null)
            {
                _innerWebSocket?.SetObjectProperty("onerror", "");
                _onError = null;
            }
            if (_onOpen != null)
            {
                _innerWebSocket?.SetObjectProperty("onopen", "");
                _onOpen = null;
            }
            if (_onMessage != null)
            {
                _innerWebSocket?.SetObjectProperty("onmessage", "");
                _onMessage = null;
            }
        }

        public override void Dispose()
        {
            System.Diagnostics.Debug.WriteLine("BrowserWebSocket::Dispose");
            int priorState = Interlocked.Exchange(ref _state, (int)InternalState.Disposed);
            if (priorState == (int)InternalState.Disposed)
            {
                // No cleanup required.
                return;
            }

            // registered by the CancellationTokenSource cts in the connect method
            _cts.Cancel(false);
            _cts.Dispose();

            _writeBuffer?.Dispose();
            _receiveMessageQueue.Writer.Complete();

            NativeCleanup();

            _innerWebSocket?.Dispose();
        }

        // This method is registered by the CancellationTokenSource cts in the connect method
        // and called by Dispose or Abort so that any open websocket connection can be closed.
        private async void AbortRequest()
        {
            if (State == WebSocketState.Open)
            {
                await CloseAsyncCore(WebSocketCloseStatus.NormalClosure, SR.net_WebSockets_Connection_Aborted, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
            }
        }

        /// <summary>
        /// Send data on <see cref="System.Net.WebSockets.ClientWebSocket"/> as an asynchronous operation.
        /// </summary>
        /// <returns>The async.</returns>
        /// <param name="buffer">Buffer.</param>
        /// <param name="messageType">Message type.</param>
        /// <param name="endOfMessage">If set to <c>true</c> end of message.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            ThrowIfNotConnected();

            if (messageType != WebSocketMessageType.Binary &&
                    messageType != WebSocketMessageType.Text)
            {
                throw new ArgumentException(SR.Format(SR.net_WebSockets_Argument_InvalidMessageType,
                    messageType,
                    nameof(SendAsync),
                    WebSocketMessageType.Binary,
                    WebSocketMessageType.Text,
                    nameof(CloseOutputAsync)),
                    nameof(messageType));
            }

            WebSocketValidate.ValidateArraySegment(buffer, nameof(buffer));

            if (!endOfMessage)
            {
                _writeBuffer ??= new MemoryStream();
                _writeBuffer.Write(buffer.Array!, buffer.Offset, buffer.Count);
                return Task.CompletedTask;
            }

            MemoryStream? writtenBuffer = _writeBuffer;
            _writeBuffer = null;
            
            if (writtenBuffer is not null)
            {
                writtenBuffer.Write(buffer.Array!, buffer.Offset, buffer.Count);
            }
            buffer = writtenBuffer?.ToArray() ?? buffer;

            try
            {
                switch (messageType)
                {
                    case WebSocketMessageType.Binary:
                        using (Uint8Array uint8Buffer = Uint8Array.From(buffer))
                        {
                            _innerWebSocket!.Invoke("send", uint8Buffer);
                        }
                        break;
                    default:
                        string strBuffer = buffer.Array == null ? string.Empty : Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count);
                        _innerWebSocket!.Invoke("send", strBuffer);
                        break;
                }
            }
            catch (Exception excb)
            {
                return Task.FromException(new WebSocketException(WebSocketError.NativeError, excb));
            }
            finally
            {
                writtenBuffer?.Dispose();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Receives data on <see cref="System.Net.WebSockets.ClientWebSocket"/> as an asynchronous operation.
        /// </summary>
        /// <returns>The async.</returns>
        /// <param name="buffer">Buffer.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            WebSocketValidate.ValidateArraySegment(buffer, nameof(buffer));

            ThrowIfDisposed();
            ThrowOnInvalidState(State, WebSocketState.Open, WebSocketState.CloseSent);
            _bufferedPayload ??= await _receiveMessageQueue.Reader.ReadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: true);

            try
            {
                bool endOfMessage = _bufferedPayload.BufferPayload(buffer, out WebSocketReceiveResult receiveResult);
                if (endOfMessage)
                    _bufferedPayload = null;
                return receiveResult;
            }
            catch (Exception exc)
            {
                throw new WebSocketException(WebSocketError.NativeError, exc);
            }
        }

        /// <summary>
        /// Aborts the connection and cancels any pending IO operations.
        /// </summary>
        public override void Abort()
        {
            if (_state == (int)InternalState.Disposed)
            {
                return;
            }
            _state = (int)WebSocketState.Aborted;
            Dispose();
        }

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _writeBuffer = null;
            ThrowIfNotConnected();
            await CloseAsyncCore(closeStatus, statusDescription, cancellationToken).ConfigureAwait(continueOnCapturedContext: true);
        }

        private async Task CloseAsyncCore(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ThrowOnInvalidState(State, WebSocketState.Open, WebSocketState.CloseReceived, WebSocketState.CloseSent);

            WebSocketValidate.ValidateCloseStatus(closeStatus, statusDescription);

            _tcsClose = new TaskCompletionSource();
            _innerWebSocketCloseStatus = closeStatus;
            _innerWebSocketCloseStatusDescription = statusDescription;
            _innerWebSocket!.Invoke("close", (int)closeStatus, statusDescription);
            await _tcsClose.Task.ConfigureAwait(continueOnCapturedContext: true);
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new PlatformNotSupportedException();

        private void ThrowIfNotConnected()
        {
            if (_state == (int)InternalState.Disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            else if (State != WebSocketState.Open)
            {
                throw new InvalidOperationException(SR.net_WebSockets_NotConnected);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_state == (int)InternalState.Disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }
    }
}
