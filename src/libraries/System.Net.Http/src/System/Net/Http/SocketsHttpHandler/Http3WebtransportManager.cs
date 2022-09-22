// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;

namespace System.Net.Http
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3WebtransportManager : IDisposable, IAsyncDisposable
    {
        private ConcurrentDictionary<long, Http3WebtransportSession> _sessions;
        private QuicConnection _connection;
        private int _disposed;

        public Http3WebtransportManager(QuicConnection connection)
        {
            _sessions = new ConcurrentDictionary<long, Http3WebtransportSession>();
            _connection = connection;
        }

        public void AddSession(QuicStream connectStream, Http3WebtransportSession webtransportSession)
        {
            if (_disposed == 1)
            {
                throw new ObjectDisposedException(nameof(Http3WebtransportManager));
            }
            lock (_sessions)
            {
                bool ans = _sessions.TryAdd(connectStream.Id, webtransportSession);
                Debug.Assert(ans);
            }
        }

        public bool FindSession(long streamId, out Http3WebtransportSession? session)
        {
            if (_disposed == 1)
            {
                throw new ObjectDisposedException(nameof(Http3WebtransportManager));
            }

            return _sessions.TryGetValue(streamId, out session);
        }

        public void AcceptStream(QuicStream stream, long sessionId)
        {
            if (_disposed == 1)
            {
                throw new ObjectDisposedException(nameof(Http3WebtransportManager));
            }

            _sessions.TryGetValue(sessionId, out Http3WebtransportSession? session);
            // if no session with that id exists throw exception
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-4
            if (session == null)
            {
                stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportBufferedStreamRejected);
                return;
            }
            session.AcceptStream(stream);
        }

        public async ValueTask<QuicStream> CreateClientStreamAsync(QuicStreamType type, long sessionId, CancellationToken cancellationToken = default)
        {
            if (_disposed == 1)
            {
                throw new ObjectDisposedException(nameof(Http3WebtransportManager));
            }

            QuicStream clientWebtransportStream;
            try
            {
                clientWebtransportStream = await _connection.OpenOutboundStreamAsync(type, cancellationToken).ConfigureAwait(false);
                await clientWebtransportStream.WriteAsync(BuildWebtransportStreamClientFrame(type, sessionId), cancellationToken).ConfigureAwait(false);

                return clientWebtransportStream;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private byte[] BuildWebtransportStreamClientFrame(QuicStreamType type, long sessionId)
        {
            Span<byte> buffer = stackalloc byte[2 + VariableLengthIntegerHelper.MaximumEncodedLength];
            long streamType = type == QuicStreamType.Unidirectional ? (long)Http3StreamType.WebTransportUnidirectional : (long)Http3StreamType.WebTransportBidirectional;
            int webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(0), streamType);
            int webtransportSessionLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(webtransportLength), sessionId);
            int payloadLength = webtransportLength + webtransportSessionLength; // includes the webtransport stream and the session id
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);

            return buffer.Slice(0, payloadLength).ToArray();
        }

        public void DeleteSession(long id)
        {
            if (_disposed == 1)
            {
                throw new ObjectDisposedException(nameof(Http3WebtransportManager));
            }

            _sessions.TryRemove(id, out _);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            List<Http3WebtransportSession> toRemove = new List<Http3WebtransportSession>();
            lock (_sessions)
            {
                foreach (KeyValuePair<long, Http3WebtransportSession> pair in _sessions)
                {
                    toRemove.Add(pair.Value);
                }
                _sessions.Clear();
            }
            foreach (Http3WebtransportSession session in toRemove)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            Debug.Assert(_sessions.IsEmpty);
        }
    }
}
