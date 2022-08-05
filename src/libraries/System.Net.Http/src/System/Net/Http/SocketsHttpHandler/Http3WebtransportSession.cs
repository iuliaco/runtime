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

        /*private static readonly ReadOnlyMemory<byte> OutputStreamHeader = new(new byte[] {
            0x40 *//*quic variable-length integer length*//*,
            (byte)Http3StreamType.WebTransportUnidirectional,
            0x00 *//*body*//*});*/

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

        public async ValueTask<bool> OpenUnidirectionalStreamAsync(Http3Connection connection)
        {
            QuicStream clientWTStream;
            try
            {
                clientWTStream = await connection.QuicConnection!.OpenOutboundStreamAsync(QuicStreamType.Unidirectional).ConfigureAwait(false);
                //await clientWTStream.WriteAsync(OutputStreamHeader, CancellationToken.None).ConfigureAwait(false);

                bool addStream = _streams.TryAdd(clientWTStream.Id, clientWTStream);
                if (!addStream)
                    return false;
                await clientWTStream.WriteAsync(BuildUnidirectionalClientFrame(), CancellationToken.None).ConfigureAwait(false);
                return true;

            }
            catch (Exception)
            {
                return false;
            }
        }

        public byte[] BuildUnidirectionalClientFrame()
        {
            Span<byte> buffer = stackalloc byte[2 + VariableLengthIntegerHelper.MaximumEncodedLength];
            // will always be 4B
            int webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(2), (long)id);
            int payloadLength = 2 + webtransportLength; // includes the se1tting ID, the integer value, and the webtransport value.
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);

            buffer[0] = (byte)0x40;
            buffer[1] = (byte)Http3StreamType.WebTransportUnidirectional;
            buffer[2 + webtransportLength] = (byte)0x00;


            return buffer.Slice(0, payloadLength).ToArray();
        }

    }
}
