// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace System.Net.Http
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3WebtransportSession
    {
        // initially null, will be placed when I create session
        private Http3RequestStream _connectStream;

        public long id => _connectStream!.StreamId;

        private ConcurrentDictionary<long, QuicStream> _streams;

        internal const string WebTransportProtocolValue = "webtransport";
        internal const string VersionEnabledIndicator = "1";
        internal const string SecPrefix = "sec-webtransport-http3-";
        internal const string VersionHeaderPrefix = $"{SecPrefix}draft";
        internal const string CurrentSuppportedVersion = $"{VersionHeaderPrefix}02";

        public Http3WebtransportSession(Http3RequestStream connectStream)
        {
            _streams = new ConcurrentDictionary<long, QuicStream>();
            _connectStream = connectStream;
        }


        internal bool AcceptServerStream(QuicStream stream) => _streams.TryAdd(stream.Id, stream);

        public async ValueTask<QuicStream?> OpenUnidirectionalStreamAsync(Http3Connection connection)
        {
            QuicStream clientWTStream;
            try
            {
                clientWTStream = await connection.QuicConnection!.OpenOutboundStreamAsync(QuicStreamType.Unidirectional).ConfigureAwait(false);
                //await clientWTStream.WriteAsync(OutputStreamHeader, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("Test ce am stricat frate2    " + clientWTStream.Id);

                bool addStream = _streams.TryAdd(clientWTStream.Id, clientWTStream);
                if (!addStream)
                    return null;
                await clientWTStream.WriteAsync(BuildUnidirectionalClientFrame(), CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("Test ce am stricat frate23    " + BuildBidirectionalClientFrame()[0]);
                Console.WriteLine("Test ce am stricat frate23    " + BuildBidirectionalClientFrame()[1]);
                Console.WriteLine("Test ce am stricat frate23    " + BuildBidirectionalClientFrame()[2]);

                return clientWTStream;

            }
            catch (Exception)
            {
                return null;
            }
        }

        public async ValueTask<QuicStream?> OpenBidirectionalStreamAsync(Http3Connection connection)
        {
            QuicStream clientWTStream;
            try
            {
                clientWTStream = await connection.QuicConnection!.OpenOutboundStreamAsync(QuicStreamType.Bidirectional).ConfigureAwait(false);
                bool addStream = _streams.TryAdd(clientWTStream.Id, clientWTStream);
                if (!addStream)
                    return null;
                await clientWTStream.WriteAsync(BuildBidirectionalClientFrame(), CancellationToken.None).ConfigureAwait(false);
                return clientWTStream;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public byte[] BuildUnidirectionalClientFrame()
        {
            Span<byte> buffer = stackalloc byte[2 + VariableLengthIntegerHelper.MaximumEncodedLength];
            int webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(0), (long)Http3StreamType.WebTransportUnidirectional);
            int webtransportSessionLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(2), (long)id);
            int payloadLength = webtransportLength + webtransportSessionLength; // includes the webtransport stream and the session id
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);

            Console.WriteLine(payloadLength +" atat trimit coaie");
            return buffer.Slice(0, payloadLength).ToArray();
        }

        public byte[] BuildBidirectionalClientFrame()
        {
            Span<byte> buffer = stackalloc byte[2 + VariableLengthIntegerHelper.MaximumEncodedLength];
            int webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(0), (long)Http3StreamType.WebTransportBidirectional);
            int webtransportSessionLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(2), (long)id);
            int payloadLength = webtransportLength + webtransportSessionLength; // includes the webtransport stream and the session id
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);
            return buffer.Slice(0, payloadLength).ToArray();
        }

    }
}
