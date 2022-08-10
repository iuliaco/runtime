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

namespace System.Net.Http
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3WebtransportManager
    {
        private ConcurrentDictionary<long, Http3WebtransportSession> _sessions;
        private Http3Connection _connection;

        //private bool _disposed;

        public Http3WebtransportManager(Http3Connection connection)
        {
            _sessions = new ConcurrentDictionary<long, Http3WebtransportSession>();
            _connection = connection;
        }


        public bool addSession(Http3RequestStream connectStream)
        {

            return _sessions.TryAdd(connectStream.StreamId, new Http3WebtransportSession(connectStream));
        }

        public bool AcceptServerStream(QuicStream stream, long sessionId)
        {
            Http3WebtransportSession? session;
            _sessions.TryGetValue(sessionId, out session);
            // if no session with that id exists throw exception
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-4
            if (session == null)
            {
                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.IdError);
            }
            return session.AcceptServerStream(stream);

        }

        public async ValueTask<bool> OpenUnidirectionalStreamAsync(long sessionId)
        {
            Http3WebtransportSession? session;
            _sessions.TryGetValue(sessionId, out session);
            // if no session with that id exists throw exception
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-4
            if (session == null)
            {
                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.IdError);
            }
            return await session.OpenUnidirectionalStreamAsync(_connection).ConfigureAwait(false);

        }

        public async ValueTask<bool> OpenBidirectionalStreamAsync(long sessionId)
        {
            Http3WebtransportSession? session;
            _sessions.TryGetValue(sessionId, out session);
            // if no session with that id exists throw exception
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-4
            if (session == null)
            {
                throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.IdError);
            }
            return await session.OpenBidirectionalStreamAsync(_connection).ConfigureAwait(false);

        }

    }
}
