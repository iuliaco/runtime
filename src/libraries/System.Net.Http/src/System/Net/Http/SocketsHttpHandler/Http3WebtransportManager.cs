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
        public ConcurrentDictionary<long, Http3WebtransportSession> sessions;
        private QuicConnection _connection;

        public Http3WebtransportManager(QuicConnection connection)
        {
            sessions = new ConcurrentDictionary<long, Http3WebtransportSession>();
            _connection = connection;
        }

        public bool AddSession(QuicStream connectStream, Http3WebtransportSession webtransportSession)
        {
            bool ans = sessions.TryAdd(connectStream.Id, webtransportSession);
            return ans;
        }

        public void AcceptServerStream(QuicStream stream, long sessionId)
        {
            Http3WebtransportSession? session;
            sessions.TryGetValue(sessionId, out session);
            // if no session with that id exists throw exception
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-4
            if (session == null)
            {
                stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.WebtransportBufferedStreamRejected);
                return;
            }
            session.AcceptServerStream(stream);
        }

        public void DeleteSession(long id)
        {
            sessions.TryRemove(id, out _);
        }

    }
}
