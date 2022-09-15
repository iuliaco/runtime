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

        public Http3WebtransportManager(QuicConnection connection)
        {
            _sessions = new ConcurrentDictionary<long, Http3WebtransportSession>();
            _connection = connection;
        }

        public bool AddSession(QuicStream connectStream, Http3WebtransportSession webtransportSession)
        {
            bool ans = _sessions.TryAdd(connectStream.Id, webtransportSession);
            return ans;
        }

        public bool FindSession(long streamId, out Http3WebtransportSession? session)
        {
            return _sessions.TryGetValue(streamId, out session);
        }

        public void AcceptServerStream(QuicStream stream, long sessionId)
        {
            Http3WebtransportSession? session;
            _sessions.TryGetValue(sessionId, out session);
            // if no session with that id exists throw exception
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-4
            if (session == null)
            {
                stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportBufferedStreamRejected);
                return;
            }
            session.AcceptServerStream(stream);
        }

        public async ValueTask<QuicStream?> CreateClientStream(QuicStreamType type, long sessionId)
        {
            QuicStream clientWTStream;
            try
            {
                clientWTStream = await _connection.OpenOutboundStreamAsync(type).ConfigureAwait(false);
                await clientWTStream.WriteAsync(BuildWebtransportStreamClientFrame(type, sessionId), default).ConfigureAwait(false);

                return clientWTStream;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private byte[] BuildWebtransportStreamClientFrame(QuicStreamType type, long sessionId)
        {
            Span<byte> buffer = stackalloc byte[2 + VariableLengthIntegerHelper.MaximumEncodedLength];
            int webtransportLength;
            if (type == QuicStreamType.Unidirectional)
                webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(0), (long)Http3StreamType.WebTransportUnidirectional);
            else
                webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(0), (long)Http3StreamType.WebTransportBidirectional);
            int webtransportSessionLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(2), (long)sessionId);
            int payloadLength = webtransportLength + webtransportSessionLength; // includes the webtransport stream and the session id
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);

            return buffer.Slice(0, payloadLength).ToArray();
        }

        public void DeleteSession(long id)
        {
            _sessions.TryRemove(id, out _);
        }

        public async ValueTask DisposeAsync()
        {
            List<long> toRemove = new List<long>();
            foreach (KeyValuePair<long, Http3WebtransportSession> pair in _sessions)
            {
               toRemove.Add(pair.Key);
            }

            foreach (long id in toRemove)
            {
                await _sessions[id].DisposeAsync().ConfigureAwait(false);
            }
        }
        public void Dispose()
        {
            List<long> toRemove = new List<long>();
            foreach (KeyValuePair<long, Http3WebtransportSession> pair in _sessions)
            {
                toRemove.Add(pair.Key);
            }
            foreach (long id in toRemove)
            {
                _sessions[id].Dispose();
            }
        }
    }
}
