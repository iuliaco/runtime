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
using System.IO;
using System.Threading.Channels;

namespace System.Net.Http
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [UnsupportedOSPlatform("browser")]

    public class Http3WebtransportSession : IAsyncDisposable
    {
        // initially null, will be placed when I create session
        private QuicStream _connectStream;
        private QuicConnection _connection;

        public long id
        {
            get { return _connectStream.Id; }
        }

        private readonly Channel<QuicStream> _incomingStreamsQueue = Channel.CreateUnbounded<QuicStream>(new UnboundedChannelOptions()
        {
            SingleWriter = true
        });
        private int _disposed;

        public Channel<QuicStream> incomingStreamsQueue => _incomingStreamsQueue;

        internal const string WebTransportProtocolValue = "webtransport";
        internal const string VersionEnabledIndicator = "1";
        internal const string SecPrefix = "sec-webtransport-http3-";
        internal const string VersionHeaderPrefix = $"{SecPrefix}draft";
        internal const string CurrentSuppportedVersion = $"{VersionHeaderPrefix}02";


        public Http3WebtransportSession(QuicConnection connection, QuicStream connectStream)
        {
            // _streams = new ConcurrentDictionary<long, QuicStream>();
            _connectStream = connectStream;
            _connection = connection;
            Console.WriteLine("created object");
            _ = _connectStream.WritesClosed.ContinueWith(async t =>
            {
                Console.WriteLine("Server closed stream");
                await DisposeAsync().ConfigureAwait(false);

            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Current);

        }




        public static async ValueTask<Http3WebtransportSession?> connectAsync(Uri uri, HttpClientHandler? handler, CancellationToken cancellationToken)
        {
            HttpClientHandler clientHandler = handler ?? new HttpClientHandler();
            var invoker = new HttpClient(clientHandler);
            Console.WriteLine("inceeeerc: ");

            Http3WebtransportSession? webSes;
            try
            {
                HttpRequestMessage request;
                request = new HttpRequestMessage(HttpMethod.Connect, uri) { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.Protocol = WebTransportProtocolValue;
                Task<HttpResponseMessage> sendTask = invoker.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var response = await sendTask.ConfigureAwait(false);
                WebtransportHttpContent connectedWebtransSessionContent = (WebtransportHttpContent)response.Content;
                Console.WriteLine("inceeeerc: 2");

                webSes = connectedWebtransSessionContent.webtransportSession;

            }
            catch (HttpRequestException ex)
            {
                throw ex;
            }

            return webSes;
        }


        public async ValueTask<QuicStream> getIncomingWTStreamFromServerAsync()
        {
            Console.WriteLine("Connect stream " + _connectStream.CanRead);
            QuicStream quicStream = await incomingStreamsQueue.Reader.ReadAsync().ConfigureAwait(false);
            return quicStream;
        }

        public bool getStreamStatus() => _connectStream.CanRead;

        internal void AcceptServerStream(QuicStream stream)
        {
            if (_disposed == 1)
            {
                // WebtransportSessionGone
                stream.Abort(QuicAbortDirection.Read, (long)0x107d7b68);
                return;
            }

            bool added = _incomingStreamsQueue.Writer.TryWrite(stream); //_streams.TryAdd(stream.Id, stream);
            Debug.Assert(added);

        }

        internal async Task AbortIncomingSessionWebtransportStreams(long errorCode)
        {
            _incomingStreamsQueue.Writer.Complete();
            var incomingStreams = _incomingStreamsQueue.Reader.ReadAllAsync().ConfigureAwait(false);
            await foreach (QuicStream incomingStream in incomingStreams)
            {
                incomingStream.Abort(QuicAbortDirection.Read, errorCode);
            }

        }

        public async ValueTask<QuicStream?> OpenWebtransportStreamAsync(QuicStreamType type)
        {
            QuicStream clientWTStream;
            try
            {
                clientWTStream = await _connection.OpenOutboundStreamAsync(type).ConfigureAwait(false);
                if (type == QuicStreamType.Unidirectional)
                    await clientWTStream.WriteAsync(BuildUnidirectionalClientFrame(), CancellationToken.None).ConfigureAwait(false);
                else
                    await clientWTStream.WriteAsync(BuildBidirectionalClientFrame(), CancellationToken.None).ConfigureAwait(false);

                return clientWTStream;
            }
            catch (Exception)
            {
                return null;
            }
        }


        private byte[] BuildUnidirectionalClientFrame()
        {
            Span<byte> buffer = stackalloc byte[2 + VariableLengthIntegerHelper.MaximumEncodedLength];
            int webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(0), (long)Http3StreamType.WebTransportUnidirectional);
            int webtransportSessionLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(2), (long)id);
            int payloadLength = webtransportLength + webtransportSessionLength; // includes the webtransport stream and the session id
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);

            return buffer.Slice(0, payloadLength).ToArray();
        }

        private byte[] BuildBidirectionalClientFrame()
        {
            Span<byte> buffer = stackalloc byte[2 + VariableLengthIntegerHelper.MaximumEncodedLength];
            int webtransportLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(0), (long)Http3StreamType.WebTransportBidirectional);
            int webtransportSessionLength = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(2), (long)id);
            int payloadLength = webtransportLength + webtransportSessionLength; // includes the webtransport stream and the session id
            Debug.Assert(payloadLength <= VariableLengthIntegerHelper.OneByteLimit);
            return buffer.Slice(0, payloadLength).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            await _connectStream.DisposeAsync().ConfigureAwait(false);
            await AbortIncomingSessionWebtransportStreams((long)0x107d7b68).ConfigureAwait(false);

        }

    }
    internal sealed class WebtransportHttpContent : HttpContent
    {
        public Http3WebtransportSession webtransportSession;
        public WebtransportHttpContent(Http3WebtransportSession session)
        {
            webtransportSession = session;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotImplementedException();
        protected internal override bool TryComputeLength(out long length) => throw new NotImplementedException();
    }

}
