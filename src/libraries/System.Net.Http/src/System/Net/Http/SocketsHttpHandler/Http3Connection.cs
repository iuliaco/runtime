// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Net.Quic;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;

namespace System.Net.Http
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3Connection : HttpConnectionBase
    {
        private readonly HttpConnectionPool _pool;
        private readonly HttpAuthority? _origin;
        private readonly HttpAuthority _authority;
        private readonly byte[]? _altUsedEncodedHeader;
        private QuicConnection? _connection;
        private Task? _connectionClosedTask;

        // Keep a collection of requests around so we can process GOAWAY.
        private readonly Dictionary<QuicStream, Http3RequestStream> _activeRequests = new Dictionary<QuicStream, Http3RequestStream>();

        // Set when GOAWAY is being processed, when aborting, or when disposing.
        private long _firstRejectedStreamId = -1;

        // Our control stream.
        private QuicStream? _clientControl;

        // Current SETTINGS from the server.
        private int _maximumHeadersLength = int.MaxValue; // TODO: this is not yet observed by Http3Stream when buffering headers.
        private bool _enableWebTransport; // by default setted with 0
        private TaskCompletionSource _expectedSettingsFrameProcessed = new TaskCompletionSource(); // True indicates that the settings frame was processed
        internal Http3WebtransportManager? WebtransportManager;

        // Once the server's streams are received, these are set to 1. Further receipt of these streams results in a connection error.
        private int _haveServerControlStream;
        private int _haveServerQpackDecodeStream;
        private int _haveServerQpackEncodeStream;

        // A connection-level error will abort any future operations.
        private Exception? _abortException;

        private const int TelemetryStatus_Opened = 1;
        private const int TelemetryStatus_Closed = 2;
        private int _markedByTelemetryStatus;

        public HttpAuthority Authority => _authority;
        public HttpConnectionPool Pool => _pool;

        public int MaximumRequestHeadersLength => _maximumHeadersLength;
        public bool EnableWebTransport => _enableWebTransport;
        public byte[]? AltUsedEncodedHeaderBytes => _altUsedEncodedHeader;
        public Exception? AbortException => Volatile.Read(ref _abortException);
        private object SyncObj => _activeRequests;

        /// <summary>
        /// If true, we've received GOAWAY, are aborting due to a connection-level error, or are disposing due to pool limits.
        /// </summary>
        private bool ShuttingDown
        {
            get
            {
                Debug.Assert(Monitor.IsEntered(SyncObj));
                return _firstRejectedStreamId != -1;
            }
        }

        public Http3Connection(HttpConnectionPool pool, HttpAuthority? origin, HttpAuthority authority, QuicConnection connection, bool includeAltUsedHeader)
        {
            _pool = pool;
            _origin = origin;
            _authority = authority;
            _connection = connection;

            if (includeAltUsedHeader)
            {
                bool altUsedDefaultPort = pool.Kind == HttpConnectionKind.Http && authority.Port == HttpConnectionPool.DefaultHttpPort || pool.Kind == HttpConnectionKind.Https && authority.Port == HttpConnectionPool.DefaultHttpsPort;
                string altUsedValue = altUsedDefaultPort ? authority.IdnHost : string.Create(CultureInfo.InvariantCulture, $"{authority.IdnHost}:{authority.Port}");
                _altUsedEncodedHeader = QPack.QPackEncoder.EncodeLiteralHeaderFieldWithoutNameReferenceToArray(KnownHeaders.AltUsed.Name, altUsedValue);
            }

            if (HttpTelemetry.Log.IsEnabled())
            {
                HttpTelemetry.Log.Http30ConnectionEstablished();
                _markedByTelemetryStatus = TelemetryStatus_Opened;
            }

            // Errors are observed via Abort().
            _ = SendSettingsAsync();

            // This process is cleaned up when _connection is disposed, and errors are observed via Abort().
            _ = AcceptStreamsAsync();
        }

        /// <summary>
        /// Starts shutting down the <see cref="Http3Connection"/>. Final cleanup will happen when there are no more active requests.
        /// </summary>
        public override void Dispose()
        {
            lock (SyncObj)
            {
                if (_firstRejectedStreamId == -1)
                {
                    _firstRejectedStreamId = long.MaxValue;
                    CheckForShutdown();
                }
            }
        }

        /// <summary>
        /// Called when shutting down, this checks for when shutdown is complete (no more active requests) and does actual disposal.
        /// </summary>
        /// <remarks>Requires <see cref="SyncObj"/> to be locked.</remarks>
        private void CheckForShutdown()
        {
            Debug.Assert(Monitor.IsEntered(SyncObj));
            Debug.Assert(ShuttingDown);

            if (_activeRequests.Count != 0)
            {
                return;
            }

            if (_connection != null)
            {
                WebtransportManager?.Dispose();
                // Close the QuicConnection in the background.
                _connectionClosedTask ??= _connection.CloseAsync((long)Http3ErrorCode.NoError).AsTask();

                QuicConnection connection = _connection;
                _connection = null;

                _ = _connectionClosedTask.ContinueWith(async closeTask =>
                {
                    if (closeTask.IsFaulted && NetEventSource.Log.IsEnabled())
                    {
                        Trace($"{nameof(QuicConnection)} failed to close: {closeTask.Exception!.InnerException}");
                    }

                    try
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Trace($"{nameof(QuicConnection)} failed to dispose: {ex}");
                    }

                    if (_clientControl != null)
                    {
                        await _clientControl.DisposeAsync().ConfigureAwait(false);
                        _clientControl = null;
                    }

                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                if (HttpTelemetry.Log.IsEnabled())
                {
                    if (Interlocked.Exchange(ref _markedByTelemetryStatus, TelemetryStatus_Closed) == TelemetryStatus_Opened)
                    {
                        HttpTelemetry.Log.Http30ConnectionClosed();
                    }
                }
            }
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, long queueStartingTimestamp, CancellationToken cancellationToken)
        {
            // Allocate an active request
            QuicStream? quicStream = null;
            Http3RequestStream? requestStream = null;

            try
            {
                if (request.IsWebTransportH3Request)
                {
                    await _expectedSettingsFrameProcessed.Task.ConfigureAwait(false);
                    if (EnableWebTransport is false)
                    {
                        throw new HttpRequestException(SR.net_unsupported_webtransport);
                    }
                    WebtransportManager ??= new Http3WebtransportManager(_connection!);
                }
                try
                {
                    QuicConnection? conn = _connection;
                    if (conn != null)
                    {
                        if (HttpTelemetry.Log.IsEnabled() && queueStartingTimestamp == 0)
                        {
                            queueStartingTimestamp = Stopwatch.GetTimestamp();
                        }

                        quicStream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);

                        requestStream = new Http3RequestStream(request, this, quicStream);
                        lock (SyncObj)
                        {
                            _activeRequests.Add(quicStream, requestStream);
                        }
                    }
                }
                // Swallow any exceptions caused by the connection being closed locally or even disposed due to a race.
                // Since quicStream will stay `null`, the code below will throw appropriate exception to retry the request.
                catch (ObjectDisposedException) { }
                catch (QuicException e) when (e.QuicError != QuicError.OperationAborted) { }
                finally
                {
                    if (HttpTelemetry.Log.IsEnabled() && queueStartingTimestamp != 0)
                    {
                        HttpTelemetry.Log.Http30RequestLeftQueue(Stopwatch.GetElapsedTime(queueStartingTimestamp).TotalMilliseconds);
                    }
                }

                if (quicStream == null)
                {
                    throw new HttpRequestException(SR.net_http_request_aborted, null, RequestRetryType.RetryOnConnectionFailure);
                }

                requestStream!.StreamId = quicStream.Id;

                bool goAway;
                lock (SyncObj)
                {
                    goAway = _firstRejectedStreamId != -1 && requestStream.StreamId >= _firstRejectedStreamId;
                }

                if (goAway)
                {
                    throw new HttpRequestException(SR.net_http_request_aborted, null, RequestRetryType.RetryOnConnectionFailure);
                }

                if (NetEventSource.Log.IsEnabled()) Trace($"Sending request: {request}");

                Task<HttpResponseMessage> responseTask = requestStream.SendAsync(cancellationToken);

                // null out requestStream to avoid disposing in finally block. It is now in charge of disposing itself.
                requestStream = null;

                return await responseTask.ConfigureAwait(false);
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.OperationAborted)
            {
                // This will happen if we aborted _connection somewhere and we have pending OpenOutboundStreamAsync call.
                // note that _abortException may be null if we closed the connection in response to a GOAWAY frame
                throw new HttpRequestException(SR.net_http_client_execution_error, _abortException, RequestRetryType.RetryOnConnectionFailure);
            }
            finally
            {
                if (requestStream is not null)
                {
                    await requestStream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Aborts the connection with an error.
        /// </summary>
        /// <remarks>
        /// Used for e.g. I/O or connection-level frame parsing errors.
        /// </remarks>
        internal Exception Abort(Exception abortException)
        {
            // Only observe the first exception we get.
            Exception? firstException = Interlocked.CompareExchange(ref _abortException, abortException, null);

            if (firstException != null)
            {
                if (NetEventSource.Log.IsEnabled() && !ReferenceEquals(firstException, abortException))
                {
                    // Lost the race to set the field to another exception, so just trace this one.
                    Trace($"{nameof(abortException)}=={abortException}");
                }

                return firstException;
            }

            // Stop sending requests to this connection.
            _pool.InvalidateHttp3Connection(this);

            long connectionResetErrorCode = (abortException as HttpProtocolException)?.ErrorCode ?? (long)Http3ErrorCode.InternalError;

            lock (SyncObj)
            {
                // Set _firstRejectedStreamId != -1 to make ShuttingDown = true.
                // It's possible GOAWAY is already being processed, in which case this would already be != -1.
                if (_firstRejectedStreamId == -1)
                {
                    _firstRejectedStreamId = long.MaxValue;
                }

                // Abort the connection. This will cause all of our streams to abort on their next I/O.
                if (_connection != null && _connectionClosedTask == null)
                {
                    _connectionClosedTask = _connection.CloseAsync((long)connectionResetErrorCode).AsTask();
                }

                CheckForShutdown();
            }

            return abortException;
        }

        private void OnServerGoAway(long firstRejectedStreamId)
        {
            // Stop sending requests to this connection.
            _pool.InvalidateHttp3Connection(this);

            var streamsToGoAway = new List<Http3RequestStream>();

            lock (SyncObj)
            {
                if (_firstRejectedStreamId != -1 && firstRejectedStreamId > _firstRejectedStreamId)
                {
                    // Server can send multiple GOAWAY frames.
                    // Spec says a server MUST NOT increase the stream ID in subsequent GOAWAYs,
                    // but doesn't specify what client should do if that is violated. Ignore for now.
                    if (NetEventSource.Log.IsEnabled())
                    {
                        Trace("HTTP/3 server sent GOAWAY with increasing stream ID. Retried requests may have been double-processed by server.");
                    }
                    return;
                }

                _firstRejectedStreamId = firstRejectedStreamId;

                foreach (KeyValuePair<QuicStream, Http3RequestStream> request in _activeRequests)
                {
                    if (request.Value.StreamId >= firstRejectedStreamId)
                    {
                        streamsToGoAway.Add(request.Value);
                    }
                }

                CheckForShutdown();
            }

            // GOAWAY each stream outside of the lock, so they can acquire the lock to remove themselves from _activeRequests.
            foreach (Http3RequestStream stream in streamsToGoAway)
            {
                stream.GoAway();
            }
        }

        public void RemoveStream(QuicStream stream)
        {
            lock (SyncObj)
            {
                bool removed = _activeRequests.Remove(stream);

                if (removed && ShuttingDown)
                {
                    CheckForShutdown();
                }
            }
        }

        public override long GetIdleTicks(long nowTicks) => throw new NotImplementedException("We aren't scavenging HTTP3 connections yet");

        public override void Trace(string message, [CallerMemberName] string? memberName = null) =>
            Trace(0, message, memberName);

        internal void Trace(long streamId, string message, [CallerMemberName] string? memberName = null) =>
            NetEventSource.Log.HandlerMessage(
                _pool?.GetHashCode() ?? 0,    // pool ID
                GetHashCode(),                // connection ID
                (int)streamId,                // stream ID
                memberName,                   // method name
                message);                     // message

        private async Task SendSettingsAsync()
        {
            try
            {
                _clientControl = await _connection!.OpenOutboundStreamAsync(QuicStreamType.Unidirectional).ConfigureAwait(false);
                // Server MUST NOT abort our control stream, setup a continuation which will react accordingly
                _ = _clientControl.WritesClosed.ContinueWith(t =>
                {
                    if (t.Exception?.InnerException is QuicException ex && ex.QuicError == QuicError.StreamAborted)
                    {
                        Abort(HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.ClosedCriticalStream));
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Current);

                await _clientControl.WriteAsync(_pool.Settings.Http3SettingsFrame, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Abort(ex);
            }
        }

        /// <summary>
        /// Encodes the settings frame we sent to the server inside the control stream
        /// </summary>
        public static byte[] BuildSettingsFrame(HttpConnectionSettings settings)
        {
            // Allocation: 3 bytes for frame header (control stream, settings frame and the total length of the settings included
            // the other 6 as follows: 4 for webtransport index, 1 for webtransport value, one for MaxResponseHeaderListSize index
            // And maximum possible for the value of MaxResponseHeaderListSize
            Span<byte> controlStreamSettingsBuilder = stackalloc byte[9 + VariableLengthIntegerHelper.MaximumEncodedLength];

            // header
            controlStreamSettingsBuilder[0] = (byte)Http3StreamType.Control;
            controlStreamSettingsBuilder[1] = (byte)Http3FrameType.Settings;

            // settings
            controlStreamSettingsBuilder[3] = (byte)Http3SettingType.MaxHeaderListSize;
            int MaxResponseHeadersByteSize = VariableLengthIntegerHelper.WriteInteger(controlStreamSettingsBuilder.Slice(4), settings.MaxResponseHeadersByteLength);
            int webtransportLength = VariableLengthIntegerHelper.WriteInteger(controlStreamSettingsBuilder.Slice(4 + MaxResponseHeadersByteSize), (long)Http3SettingType.EnableWebTransport);
            controlStreamSettingsBuilder[4 + MaxResponseHeadersByteSize + webtransportLength] = 1;

            // includes the settings inside the frame total size
            int payloadLength = 2 + MaxResponseHeadersByteSize + webtransportLength;
            controlStreamSettingsBuilder[2] = (byte)payloadLength;

            // total length of the settings sent inside the setting frame: the max response headers and webtransport
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);

            return controlStreamSettingsBuilder.Slice(0, 3 + payloadLength).ToArray();
        }

        /// <summary>
        /// Accepts unidirectional streams (control, QPack, ...) from the server.
        /// </summary>
        private async Task AcceptStreamsAsync()
        {
            try
            {
                while (true)
                {
                    ValueTask<QuicStream> streamTask;

                    lock (SyncObj)
                    {
                        if (ShuttingDown)
                        {
                            return;
                        }

                        // No cancellation token is needed here; we expect the operation to cancel itself when _connection is disposed.
                        streamTask = _connection!.AcceptInboundStreamAsync(CancellationToken.None);
                    }

                    QuicStream stream = await streamTask.ConfigureAwait(false);

                    // This process is cleaned up when _connection is disposed, and errors are observed via Abort().
                    _ = ProcessServerStreamAsync(stream);
                }
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.OperationAborted)
            {
                // Shutdown initiated by us, no need to abort.
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.ConnectionAborted)
            {
                Debug.Assert(ex.ApplicationErrorCode.HasValue);
                Http3ErrorCode code = (Http3ErrorCode)ex.ApplicationErrorCode.Value;

                Abort(HttpProtocolException.CreateHttp3ConnectionException(code, SR.net_http_http3_connection_close));
            }
            catch (Exception ex)
            {
                Abort(ex);
            }
        }

        /// <summary>
        /// Routes a stream to an appropriate stream-type-specific processor
        /// </summary>
        private async Task ProcessServerStreamAsync(QuicStream stream)
        {
            ArrayBuffer buffer = default;
            bool disposeStream = true;

            try
            {
                buffer = new ArrayBuffer(initialSize: 32, usePool: true);
                int bytesRead;

                try
                {
                    // webtransport has a header of 3 bytes and in order to avoid reading
                    // user content for webtransport stream we need at first to read 3 bytes
                    bytesRead = await stream.ReadAsync(buffer.AvailableMemorySliced(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch (QuicException ex) when (ex.QuicError == QuicError.StreamAborted)
                {
                    // Treat identical to receiving 0. See below comment.
                    bytesRead = 0;
                }

                if (bytesRead == 0)
                {
                    // https://quicwg.org/base-drafts/draft-ietf-quic-http.html#name-unidirectional-streams
                    // A sender can close or reset a unidirectional stream unless otherwise specified. A receiver MUST
                    // tolerate unidirectional streams being closed or reset prior to the reception of the unidirectional
                    // stream header.
                    return;
                }

                buffer.Commit(bytesRead);
                bool foundStreamType = VariableLengthIntegerHelper.TryRead(buffer.ActiveSpan, out long streamType, out bytesRead);
                while (foundStreamType is false)
                {
                    buffer.EnsureAvailableSpace(VariableLengthIntegerHelper.MaximumEncodedLength);
                    bytesRead = await stream.ReadAsync(buffer.AvailableMemory, CancellationToken.None).ConfigureAwait(false);
                    buffer.Commit(bytesRead);
                    foundStreamType = VariableLengthIntegerHelper.TryRead(buffer.ActiveSpan, out streamType, out bytesRead);
                }
                switch (streamType)
                {
                    case (byte)Http3StreamType.Control:
                        if (Interlocked.Exchange(ref _haveServerControlStream, 1) != 0)
                        {
                            // A second control stream has been received.
                            throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.StreamCreationError);
                        }

                        // Discard the stream type header.
                        buffer.Discard(1);

                        // Ownership of buffer is transferred to ProcessServerControlStreamAsync.
                        ArrayBuffer bufferCopy = buffer;
                        buffer = default;
                        disposeStream = false;
                        await ProcessServerControlStreamAsync(stream, bufferCopy).ConfigureAwait(false);
                        return;
                    case (byte)Http3StreamType.QPackDecoder:
                        if (Interlocked.Exchange(ref _haveServerQpackDecodeStream, 1) != 0)
                        {
                            // A second QPack decode stream has been received.
                            throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.StreamCreationError);
                        }

                        // The stream must not be closed, but we aren't using QPACK right now -- ignore.
                        buffer.Dispose();
                        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
                        return;
                    case (byte)Http3StreamType.QPackEncoder:
                        if (Interlocked.Exchange(ref _haveServerQpackEncodeStream, 1) != 0)
                        {
                            // A second QPack encode stream has been received.
                            throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.StreamCreationError);
                        }

                        // We haven't enabled QPack in our SETTINGS frame, so we shouldn't receive any meaningful data here.
                        // However, the standard says the stream must not be closed for the lifetime of the connection. Just ignore any data.
                        buffer.Dispose();
                        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
                        return;
                    case (byte)Http3StreamType.Push:
                        // We don't support push streams.
                        // Because no maximum push stream ID was negotiated via a MAX_PUSH_ID frame, server should not have sent this. Abort the connection with H3_ID_ERROR.
                        throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.IdError);
                    case (long)Http3StreamType.WebTransportBidirectional:
                    case (long)Http3StreamType.WebTransportUnidirectional:
                        if (EnableWebTransport)
                        {
                            VariableLengthIntegerHelper.TryRead(buffer.ActiveSpan.Slice(bytesRead), out long sessionId, out bytesRead);
                            disposeStream = false;
                            WebtransportManager!.AcceptStream(stream, sessionId);
                            return;
                        }
                        stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.StreamCreationError);
                        return;
                    default:
                        // Server initiated bidirectional streams are either push streams or extensions, and we support neither.
                        if (stream.CanWrite)
                        {
                            throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.StreamCreationError);
                        }
                        // Unknown stream type. Per spec, these must be ignored and aborted but not be considered a connection-level error.

                        if (NetEventSource.Log.IsEnabled())
                        {
                            // Read the rest of the integer, which might be more than 1 byte, so we can log it.
                            NetEventSource.Info(this, $"Ignoring server-initiated stream of unknown type {streamType}.");
                        }

                        stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.StreamCreationError);
                        return;


                }
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.OperationAborted)
            {
                // ignore the exception, we have already closed the connection
            }
            catch (Exception ex)
            {
                Abort(ex);
            }
            finally
            {
                if (disposeStream)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);

                }
                buffer.Dispose();
            }
        }

        /// <summary>
        /// Reads the server's control stream.
        /// </summary>
        private async Task ProcessServerControlStreamAsync(QuicStream stream, ArrayBuffer buffer)
        {
            try
            {
                using (buffer)
                {
                    // Read the first frame of the control stream. Per spec:
                    // A SETTINGS frame MUST be sent as the first frame of each control stream.

                    (Http3FrameType? frameType, long payloadLength) = await ReadFrameEnvelopeAsync().ConfigureAwait(false);

                    if (frameType == null)
                    {
                        // Connection closed prematurely, expected SETTINGS frame.
                        throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.ClosedCriticalStream);
                    }

                    if (frameType != Http3FrameType.Settings)
                    {
                        throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.MissingSettings);
                    }

                    await ProcessSettingsFrameAsync(payloadLength).ConfigureAwait(false);

                    // Read subsequent frames.

                    while (true)
                    {
                        (frameType, payloadLength) = await ReadFrameEnvelopeAsync().ConfigureAwait(false);

                        switch (frameType)
                        {
                            case Http3FrameType.GoAway:
                                await ProcessGoAwayFrameAsync(payloadLength).ConfigureAwait(false);
                                break;
                            case Http3FrameType.Settings:
                                // If an endpoint receives a second SETTINGS frame on the control stream, the endpoint MUST respond with a connection error of type H3_FRAME_UNEXPECTED.
                                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.UnexpectedFrame);
                            case Http3FrameType.Headers: // Servers should not send these frames to a control stream.
                            case Http3FrameType.Data:
                            case Http3FrameType.MaxPushId:
                            case Http3FrameType.ReservedHttp2Priority: // These frames are explicitly reserved and must never be sent.
                            case Http3FrameType.ReservedHttp2Ping:
                            case Http3FrameType.ReservedHttp2WindowUpdate:
                            case Http3FrameType.ReservedHttp2Continuation:
                                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.UnexpectedFrame);
                            case Http3FrameType.PushPromise:
                            case Http3FrameType.CancelPush:
                                // Because we haven't sent any MAX_PUSH_ID frame, it is invalid to receive any push-related frames as they will all reference a too-large ID.
                                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.IdError);
                            case null:
                                // End of stream reached. If we're shutting down, stop looping. Otherwise, this is an error (this stream should not be closed for life of connection).
                                bool shuttingDown;
                                lock (SyncObj)
                                {
                                    shuttingDown = ShuttingDown;
                                }
                                if (!shuttingDown)
                                {
                                    throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.ClosedCriticalStream);
                                }
                                return;
                            default:
                                await SkipUnknownPayloadAsync(frameType.GetValueOrDefault(), payloadLength).ConfigureAwait(false);
                                break;
                        }
                    }
                }
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.StreamAborted)
            {
                // Peers MUST NOT close the control stream
                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.ClosedCriticalStream);
            }

            async ValueTask<(Http3FrameType? frameType, long payloadLength)> ReadFrameEnvelopeAsync()
            {
                long frameType, payloadLength;
                int bytesRead;

                while (!Http3Frame.TryReadIntegerPair(buffer.ActiveSpan, out frameType, out payloadLength, out bytesRead))
                {
                    buffer.EnsureAvailableSpace(VariableLengthIntegerHelper.MaximumEncodedLength * 2);
                    bytesRead = await stream.ReadAsync(buffer.AvailableMemory, CancellationToken.None).ConfigureAwait(false);

                    if (bytesRead != 0)
                    {
                        buffer.Commit(bytesRead);
                    }
                    else if (buffer.ActiveLength == 0)
                    {
                        // End of stream.
                        return (null, 0);
                    }
                    else
                    {
                        // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                        throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.FrameError);
                    }
                }

                buffer.Discard(bytesRead);

                return ((Http3FrameType)frameType, payloadLength);
            }

            async ValueTask ProcessSettingsFrameAsync(long settingsPayloadLength)
            {
                while (settingsPayloadLength != 0)
                {
                    long settingId, settingValue;
                    int bytesRead;

                    while (!Http3Frame.TryReadIntegerPair(buffer.ActiveSpan, out settingId, out settingValue, out bytesRead))
                    {
                        buffer.EnsureAvailableSpace(VariableLengthIntegerHelper.MaximumEncodedLength * 2);
                        bytesRead = await stream.ReadAsync(buffer.AvailableMemory, CancellationToken.None).ConfigureAwait(false);

                        if (bytesRead != 0)
                        {
                            buffer.Commit(bytesRead);
                        }
                        else
                        {
                            // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                            throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.FrameError);
                        }
                    }

                    settingsPayloadLength -= bytesRead;

                    if (settingsPayloadLength < 0)
                    {
                        // An integer was encoded past the payload length.
                        // A frame payload that contains additional bytes after the identified fields or a frame payload that terminates before the end of the identified fields MUST be treated as a connection error of type H3_FRAME_ERROR.
                        throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.FrameError);
                    }

                    buffer.Discard(bytesRead);

                    switch ((Http3SettingType)settingId)
                    {
                        case Http3SettingType.MaxHeaderListSize:
                            _maximumHeadersLength = (int)Math.Min(settingValue, int.MaxValue);
                            break;
                        case Http3SettingType.EnableWebTransport:

                            // Per https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-3.1
                            // an endpoint that receives a value other than "0" or "1" MUST close the
                            // connection with the H3_SETTINGS_ERROR error code.
                            if (settingValue != 0 && settingValue != 1)
                            {
                                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.SettingsError);
                            }
                            _enableWebTransport = settingValue == 1;
                            break;
                        case Http3SettingType.ReservedHttp2EnablePush:
                        case Http3SettingType.ReservedHttp2MaxConcurrentStreams:
                        case Http3SettingType.ReservedHttp2InitialWindowSize:
                        case Http3SettingType.ReservedHttp2MaxFrameSize:
                            // Per https://tools.ietf.org/html/draft-ietf-quic-http-31#section-7.2.4.1
                            // these settings IDs are reserved and must never be sent.
                            throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.SettingsError);
                    }
                }

                _expectedSettingsFrameProcessed.TrySetResult();
            }

            async ValueTask ProcessGoAwayFrameAsync(long goawayPayloadLength)
            {
                long firstRejectedStreamId;
                int bytesRead;

                while (!VariableLengthIntegerHelper.TryRead(buffer.ActiveSpan, out firstRejectedStreamId, out bytesRead))
                {
                    buffer.EnsureAvailableSpace(VariableLengthIntegerHelper.MaximumEncodedLength);
                    bytesRead = await stream.ReadAsync(buffer.AvailableMemory, CancellationToken.None).ConfigureAwait(false);

                    if (bytesRead != 0)
                    {
                        buffer.Commit(bytesRead);
                    }
                    else
                    {
                        // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                        throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.FrameError);
                    }
                }

                buffer.Discard(bytesRead);
                if (bytesRead != goawayPayloadLength)
                {
                    // Frame contains unknown extra data after the integer.
                    throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.FrameError);
                }

                OnServerGoAway(firstRejectedStreamId);
            }

            async ValueTask SkipUnknownPayloadAsync(Http3FrameType frameType, long payloadLength)
            {
                while (payloadLength != 0)
                {
                    if (buffer.ActiveLength == 0)
                    {
                        int bytesRead = await stream.ReadAsync(buffer.AvailableMemory, CancellationToken.None).ConfigureAwait(false);

                        if (bytesRead != 0)
                        {
                            buffer.Commit(bytesRead);
                        }
                        else
                        {
                            // Our buffer has partial frame data in it but not enough to complete the read: bail out.
                            throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.FrameError);
                        }
                    }

                    long readLength = Math.Min(payloadLength, buffer.ActiveLength);
                    buffer.Discard((int)readLength);
                    payloadLength -= readLength;
                }
            }
        }
    }
}
