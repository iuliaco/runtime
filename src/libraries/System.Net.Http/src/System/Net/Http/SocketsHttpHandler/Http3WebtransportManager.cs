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
        private QuicConnection _connection;

        //private bool _disposed;

        public Http3WebtransportManager(QuicConnection connection)
        {
            _sessions = new ConcurrentDictionary<long, Http3WebtransportSession>();
            _connection = connection;
        }


        public bool addSession(QuicStream connectStream, Http3WebtransportSession webtransportSession)
        {

            return _sessions.TryAdd(connectStream.Id, webtransportSession);
        }

        public async ValueTask<bool> AcceptServerStream(QuicStream stream, long sessionId)
        {
            Http3WebtransportSession? session;
            Console.WriteLine(" SPEEER nuuuuu " + sessionId);
            _sessions.TryGetValue(sessionId, out session);
            // if no session with that id exists throw exception
            // https://datatracker.ietf.org/doc/html/draft-ietf-webtrans-http3#section-4
            if (session == null)
            {
                Console.WriteLine("aici?????");
                return false;
                // throw HttpProtocolException.CreateHttp3ConnectionException(Http3ErrorCode.IdError);
            }
            await session.isEstablished.Task.ConfigureAwait(false);
            var sper = session.AcceptServerStream(stream);
            Console.WriteLine(" SPEEER " + sper);
            return sper;
        }

     /*   public async ValueTask<QuicStream?> OpenUnidirectionalStreamAsync(long sessionId)
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

        public async ValueTask<QuicStream?> OpenBidirectionalStreamAsync(long sessionId)
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
*/
    }
}
