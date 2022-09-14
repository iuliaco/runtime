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

    public class Http3WebtransportSession : IAsyncDisposable, IDisposable
    {
        private readonly QuicStream _connectStream;
        private static HttpMessageInvoker? s_defaultInvoker;

        public long Id
        {
            get { return _connectStream.Id; }
        }

        private readonly Channel<QuicStream> _incomingStreamsQueue = Channel.CreateUnbounded<QuicStream>(new UnboundedChannelOptions()
        {
            SingleWriter = true
        });

        private int _disposed;
        internal Http3WebtransportManager _WebtransportManager;

        internal const string WebTransportProtocolValue = "webtransport";
        internal const string VersionEnabledIndicator = "1";
        internal const string SecPrefix = "sec-webtransport-http3-";
        internal const string VersionHeaderPrefix = $"{SecPrefix}draft";
        internal const string CurrentSuppportedVersion = $"{VersionHeaderPrefix}02";

        internal Http3WebtransportSession(QuicStream connectStream, Http3WebtransportManager manager)
        {
            _WebtransportManager = manager;
            _connectStream = connectStream;
            _ = _connectStream.WritesClosed.ContinueWith(async t =>
            {
                await DisposeAsync().ConfigureAwait(false);

            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Current);

        }

        /// <summary>
        /// Creates a webtransport session by creating a webtransport connect request, sending it to <see cref="Uri">uri</see>.
        /// </summary>
        public static async ValueTask<Http3WebtransportSession?> ConnectAsync(Uri uri, HttpMessageInvoker? invoker, CancellationToken cancellationToken)
        {
            if (invoker is null)
            {
                s_defaultInvoker ??= new HttpMessageInvoker(new HttpClientHandler());
                invoker = s_defaultInvoker;
            }

            Http3WebtransportSession? webtransportSession;
            try
            {
                HttpRequestMessage request;
                request = new HttpRequestMessage(HttpMethod.Connect, uri) { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.Protocol = WebTransportProtocolValue;
                Task<HttpResponseMessage> sendTask = invoker is HttpClient client ? client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken) : invoker.SendAsync(request, cancellationToken);
                HttpResponseMessage response = await sendTask.ConfigureAwait(false);
                WebtransportHttpContent connectedWebtransSessionContent = (WebtransportHttpContent)response.Content;
                webtransportSession = connectedWebtransSessionContent.webtransportSession;
                if (!response.IsSuccessStatusCode || !response.Headers.Contains(Http3WebtransportSession.VersionHeaderPrefix))
                {
                    await webtransportSession.AbortIncomingSessionWebtransportStreams((long)Http3ErrorCode.WebtransportBufferedStreamRejected).ConfigureAwait(false);
                    await webtransportSession.DisposeAsync().ConfigureAwait(false);
                    HttpRequestException exception = new(SR.net_webtransport_server_rejected);
                    throw exception;
                }
            }
            catch (HttpRequestException ex)
            {
                throw ex;
            }

            return webtransportSession;
        }

        /// <summary>
        /// Takes the next incoming <see cref="QuicStream">quic stream from the server</see>.
        /// </summary>
        public async ValueTask<QuicStream?> GetIncomingWTStreamFromServerAsync()
        {
            if (_disposed == 1)
                return null;
            QuicStream quicStream = await _incomingStreamsQueue.Reader.ReadAsync().ConfigureAwait(false);
            return quicStream;
        }

        internal void AcceptServerStream(QuicStream stream)
        {
            if (_disposed == 1)
            {
                // WebtransportSessionGone error code
                stream.Abort(QuicAbortDirection.Read, 0x107d7b68);
                return;
            }

            bool added = _incomingStreamsQueue.Writer.TryWrite(stream);
            Debug.Assert(added);
        }

        internal async Task AbortIncomingSessionWebtransportStreams(long errorCode)
        {
            // check if they were not aborted before
            if(_incomingStreamsQueue.Writer.TryComplete())
            {
                var incomingStreams = _incomingStreamsQueue.Reader.ReadAllAsync().ConfigureAwait(false);
                await foreach (QuicStream incomingStream in incomingStreams)
                {
                    incomingStream.Abort(QuicAbortDirection.Read, errorCode);
                }
            }
        }

        /// <summary>
        /// Creates a new <see cref="QuicStream">quic stream and sends it to the server</see>.
        /// </summary>
        public async ValueTask<QuicStream?> OpenWebtransportStreamAsync(QuicStreamType type)
        {
            if (_disposed == 1)
                return null;
            return await _WebtransportManager.CreateClientStream(type, Id).ConfigureAwait(false);
        }
        private void RemoveFromSessionsDictionary()
        {
            _WebtransportManager.DeleteSession(Id);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            RemoveFromSessionsDictionary();
            await AbortIncomingSessionWebtransportStreams((long)0x107d7b68).ConfigureAwait(false);
            await _connectStream.DisposeAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            RemoveFromSessionsDictionary();
            _connectStream.Dispose();
            _incomingStreamsQueue.Writer.Complete();
            while (_incomingStreamsQueue.Reader.TryRead(out QuicStream? stream))
            {
                stream!.Abort(QuicAbortDirection.Read, 0x107d7b68);
            }
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
