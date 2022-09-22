// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Quic;
using System.Net.Test.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Http.Functional.Tests
{
    [Collection(nameof(DisableParallelization))]
    [ConditionalClass(typeof(HttpClientHandlerTestBase), nameof(IsQuicSupported))]
    public sealed class HttpWebtransportSessionTest : HttpClientHandlerTestBase
    {
        public HttpWebtransportSessionTest(ITestOutputHelper output) : base(output)
        {
        }

        internal static async Task SendWebtransportStreamHeaderAsync(QuicStream stream, long streamType, long sessionId)
        {
            var buffer = new byte[3];
            int bytesWritten = Http3LoopbackStream.EncodeHttpInteger(streamType, buffer);
            bytesWritten += Http3LoopbackStream.EncodeHttpInteger(sessionId, buffer.AsSpan(bytesWritten));
            await stream.WriteAsync(buffer.AsMemory(0, bytesWritten)).ConfigureAwait(false);
        }
        internal static async Task<QuicStream> OpenWebtransportStreamAsync(Http3LoopbackConnection connection, QuicStreamType type, long sessionId)
        {
            QuicStream stream = await connection.OpenOutboundStreamAsync(type);
            await SendWebtransportStreamHeaderAsync(
                stream,
                type == QuicStreamType.Unidirectional ? Http3LoopbackStream.UnidirectionalWebtransportStream : Http3LoopbackStream.BidirectionalWebtransportStream,
                sessionId);
            return stream;
        }

        private async Task<(long? frameType, long? session)> ReadWTFrameAsync(QuicStream stream)
        {
            long? frameType = await ReadIntegerAsync(stream).ConfigureAwait(false);
            if (frameType == null) return (null, null);

            long? session = await ReadIntegerAsync(stream).ConfigureAwait(false);
            if (session == null) throw new Exception("Unable to read session; unexpected end of stream.");

            return (frameType, session);
        }

        private async Task<long?> ReadIntegerAsync(QuicStream stream)
        {
            byte[] buffer = new byte[8];
            int bufferActiveLength = 0;

            long integerValue;
            int bytesRead;

            do
            {
                bytesRead = await stream.ReadAsync(buffer.AsMemory(bufferActiveLength++, 1)).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return bufferActiveLength == 1 ? (long?)null : throw new Exception("Unable to read varint; unexpected end of stream.");
                }
                Debug.Assert(bytesRead == 1);
            }
            while (!Http3LoopbackStream.TryDecodeHttpInteger(buffer.AsSpan(0, bufferActiveLength), out integerValue, out bytesRead));

            Debug.Assert(bytesRead == bufferActiveLength);

            return integerValue;
        }

        [Fact]
        public async Task WebTransportWrongServerAddress()
        {

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await Http3WebtransportSession.ConnectAsync((Uri)null, client, CancellationToken.None));
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await Http3WebtransportSession.ConnectAsync(new Uri("/relative",UriKind.Relative), client, CancellationToken.None));
                await Assert.ThrowsAsync<NotSupportedException>(async () => await Http3WebtransportSession.ConnectAsync(new Uri("foo://foo.bar"), client, CancellationToken.None));
            });

            await new[] { clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportNotSupportedByServer()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                await server.EstablishGenericConnectionAsync();

            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                await Assert.ThrowsAsync<HttpRequestException>(async () => await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None));
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportWrongServerStatusCode()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                (Http3LoopbackStream settingsStream, Http3LoopbackStream stream) = await connection.AcceptControlAndRequestStreamAsync();

                await using (settingsStream)
                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.NotFound);
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                await Assert.ThrowsAsync<HttpRequestException>(async () => await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None));
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportWrongServerResponseHeader()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                (Http3LoopbackStream settingsStream, Http3LoopbackStream stream) = await connection.AcceptControlAndRequestStreamAsync();

                await using (settingsStream)
                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync();
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                await Assert.ThrowsAsync<HttpRequestException>(async () => await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None));
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportSessionGoneError()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            SemaphoreSlim semaphore = new SemaphoreSlim(0);

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);

                var headers = new List<HttpHeaderData>();
                int contentLength = 2 * 1024 * 1024;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);
                headers.Append(header);

                (Http3LoopbackStream settingsStream, Http3LoopbackStream stream) = await connection.AcceptControlAndRequestStreamAsync();

                await using (settingsStream)
                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    QuicStream wtServerBidirectionalStream =await OpenWebtransportStreamAsync(connection, QuicStreamType.Bidirectional, stream.StreamId);
                    byte[] recvBytes = new byte[18];
                    string s = "Hello World ";
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    await semaphore.WaitAsync();
                    await semaphore.WaitAsync();
                    QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await wtServerBidirectionalStream.WriteAsync(recvBytes, true).ConfigureAwait(false));
                    Assert.Equal(386759528, ex.ApplicationErrorCode);
                    semaphore.Release();
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                semaphore.Release();
                await session.DisposeAsync();
                semaphore.Release();
                await semaphore.WaitAsync();
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebtransportBufferedStreamError()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            SemaphoreSlim semaphore = new SemaphoreSlim(0);
            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 2 * 1024 * 1024;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();

                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    var wtServerBidirectionalStream = await OpenWebtransportStreamAsync(connection, QuicStreamType.Bidirectional, stream.StreamId + 1);

                    byte[] recvBytes = new byte[18];
                    string s = "Hellp world";
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    await semaphore.WaitAsync();

                    QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await wtServerBidirectionalStream.WriteAsync(recvBytes, true).ConfigureAwait(false));
                    Assert.Equal(966049156, ex.ApplicationErrorCode);
                    semaphore.Release();

                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                await session.DisposeAsync();
                semaphore.Release();
                await semaphore.WaitAsync();

            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task SendWebtransportBidirectionClientStreamsReadWrite()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            string s = "Hello World ";

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 2 * 1024 * 1024;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();

                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                    for (int i = 0; i < 20; i++)
                    {
                        QuicStream clientStream = await connection.AcceptRawQuicStream();
                        (long? frameType, long? sessionId) = await ReadWTFrameAsync(clientStream);
                        Assert.Equal(stream.StreamId, sessionId);
                        byte[] recvBytes = new byte[20];

                        int bytesRead = await clientStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);

                        Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                        recvBytes = new byte[20];

                        recvBytes = Encoding.ASCII.GetBytes(s + i);
                        await clientStream.WriteAsync(recvBytes, true).ConfigureAwait(false);
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                for (int i = 0; i < 20; i++)
                {
                    var wtClientBidirectionalStream = await session.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                    byte[] recvBytes = new byte[20];
                    recvBytes = Encoding.ASCII.GetBytes(s + i);
                    await wtClientBidirectionalStream.WriteAsync(recvBytes, true);
                    recvBytes = new byte[20];
                    int bytesRead = await wtClientBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                    await wtClientBidirectionalStream.DisposeAsync();
                }
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task SendWebtransportUnidirectionClientStreamsWrite()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            string s = "Hello World ";

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 1024;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                (Http3LoopbackStream settingsStream, Http3LoopbackStream stream) = await connection.AcceptControlAndRequestStreamAsync();

                await using (settingsStream)
                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    for (int i = 0; i < 20; i++)
                    {
                        QuicStream clientStream = await connection.AcceptRawQuicStream();
                        (long? frameType, long? sessionId) = await ReadWTFrameAsync(clientStream);
                        Assert.Equal(stream.StreamId, sessionId);
                        byte[] recvBytes = new byte[20];
                        int bytesRead = await clientStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                        Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);

                for (int i = 0; i < 20; i++)
                {
                    var wtClientUnidirectionalStream = await session.OpenOutboundStreamAsync(QuicStreamType.Unidirectional);
                    byte[] recvBytes = new byte[20];
                    recvBytes = Encoding.ASCII.GetBytes(s + i);
                    await wtClientUnidirectionalStream.WriteAsync(recvBytes, true);
                    await wtClientUnidirectionalStream.DisposeAsync();
                }
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task SendWebtransportBidirectionServerStreamsReadWrite()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            string s = "Hello World ";

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 10;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();

                await using (stream)
                {

                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    for (int i = 0; i < 20; i++)
                    {
                        var wtServerBidirectionalStream = await OpenWebtransportStreamAsync(connection, QuicStreamType.Bidirectional, stream.StreamId);
                        byte[] recvBytes = new byte[18];
                        int bytesRead = 0;
                        bytesRead = await wtServerBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                        Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                        bytesRead = await wtServerBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                        recvBytes = Encoding.ASCII.GetBytes(s + i);
                        await wtServerBidirectionalStream.WriteAsync(recvBytes, true).ConfigureAwait(false);

                    }

                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                for (int i = 0; i < 20; i++)
                {
                    QuicStream help = await session.AcceptInboundStreamAsync();
                    byte[] recvBytes = new byte[20];
                    recvBytes = Encoding.ASCII.GetBytes(s + i);
                    await help.WriteAsync(recvBytes, true, CancellationToken.None).ConfigureAwait(false);
                    recvBytes = new byte[20];
                    int bytesRead = await help.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                    await help.DisposeAsync();
                }

            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task SendWebtransportUnidirectionServerStreamsRead()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            string s = "Hello World ";

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 10;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();
                Assert.True(connection.EnableWebtransport);
                await stream.ReadRequestDataAsync(false);
                await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                for (int i = 0; i < 10; i++)
                {
                    var wtServerUnidirectionalStream = await OpenWebtransportStreamAsync(connection, QuicStreamType.Unidirectional, stream.StreamId);
                    byte[] recvBytes = new byte[20];
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    await wtServerUnidirectionalStream.WriteAsync(recvBytes, true).ConfigureAwait(false);
                }
                
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);

                for (int i = 0; i < 10; i++)
                {

                    QuicStream help = await session.AcceptInboundStreamAsync();
                    byte[] recvBytes = new byte[20];
                    int bytesRead = await help.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                    await help.DisposeAsync();
                }
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task SendWebtransportUnidirectionServerStreamsWriteError()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            string s = "Hello World ";

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 10;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);
                headers.Add(header);

                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();

                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    var wtServerUnidirectionalStream = await OpenWebtransportStreamAsync(connection, QuicStreamType.Unidirectional, stream.StreamId);

                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                QuicStream help = await session.AcceptInboundStreamAsync();
                byte[] recvBytes = new byte[20];
                recvBytes = Encoding.ASCII.GetBytes(s);
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await help.WriteAsync(recvBytes, true, CancellationToken.None).ConfigureAwait(false));

                await help.DisposeAsync();
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }



        [Fact]
        public async Task WebtransportNoStreamsReceivedFromServerCancelOperation()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            SemaphoreSlim semaphore = new SemaphoreSlim(0);


            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 10;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);
                headers.Add(header);

                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();

                await using (stream)
                {
                    Assert.True(connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                    await semaphore.WaitAsync();
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.CancelAfter(5000);
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await session.AcceptInboundStreamAsync(cancellationTokenSource.Token));
                semaphore.Release();
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportAllowMultipleSessionsDifferentTermination()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            SemaphoreSlim semaphore = new SemaphoreSlim(0);

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                var headers = new List<HttpHeaderData>();
                int contentLength = 10;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();
                await stream.ReadRequestDataAsync(false);
                await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                stream = await connection.AcceptRequestStreamAsync();
                await stream.ReadRequestDataAsync(false);
                await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                stream = await connection.AcceptRequestStreamAsync();
                await stream.ReadRequestDataAsync(false);
                await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                await stream.DisposeAsync();
                semaphore.Release();
                await connection.CloseAsync(10000);
                semaphore.Release();
                await semaphore.WaitAsync();

            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session1 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                Http3WebtransportSession session2 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                await session2.DisposeAsync();
                Http3WebtransportSession session3 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                await semaphore.WaitAsync();
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await session2.OpenOutboundStreamAsync(QuicStreamType.Unidirectional));
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await session3.OpenOutboundStreamAsync(QuicStreamType.Unidirectional));
                await semaphore.WaitAsync();
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await session1.OpenOutboundStreamAsync(QuicStreamType.Unidirectional));
                semaphore.Release();

            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportMultipleSessionsMultipleStreams()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            SemaphoreSlim semaphore = new SemaphoreSlim(0);
            string s = "Hello World";


            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                var headers = new List<HttpHeaderData>();
                int contentLength = 10;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                Http3LoopbackStream stream1 = await connection.AcceptRequestStreamAsync();
                await stream1.ReadRequestDataAsync(false);
                await stream1.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                Http3LoopbackStream stream2 = await connection.AcceptRequestStreamAsync();
                await stream2.ReadRequestDataAsync(false);
                await stream2.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                QuicStream wtClientBidirectionalStream1 = await connection.AcceptRawQuicStream();
                (long? frameType1, long? sessionId1) = await ReadWTFrameAsync(wtClientBidirectionalStream1);
                Assert.Equal(stream1.StreamId, sessionId1);

                QuicStream wtClientBidirectionalStream2 = await connection.AcceptRawQuicStream();
                (long? frameType2, long? sessionId2) = await ReadWTFrameAsync(wtClientBidirectionalStream2);
                Assert.Equal(stream2.StreamId, sessionId2);

                var wtServerBidirectionalStream1 = await OpenWebtransportStreamAsync(connection, QuicStreamType.Bidirectional, stream1.StreamId);

                var wtServerBidirectionalStream2 = await OpenWebtransportStreamAsync(connection, QuicStreamType.Bidirectional, stream2.StreamId);

                byte[] recvBytes = new byte[20];
                recvBytes = Encoding.ASCII.GetBytes(s);
                await wtServerBidirectionalStream1.WriteAsync(recvBytes, true).ConfigureAwait(false);
                await wtServerBidirectionalStream2.WriteAsync(recvBytes, true).ConfigureAwait(false);
                await wtClientBidirectionalStream1.WriteAsync(recvBytes, true).ConfigureAwait(false);
                await wtClientBidirectionalStream2.WriteAsync(recvBytes, true).ConfigureAwait(false);

                recvBytes = new byte[18];
                int bytesRead = await wtServerBidirectionalStream1.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                recvBytes = new byte[18];
                bytesRead = await wtServerBidirectionalStream2.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                recvBytes = new byte[18];
                bytesRead = await wtClientBidirectionalStream1.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                recvBytes = new byte[18];
                bytesRead = await wtClientBidirectionalStream1.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                semaphore.Release();
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session1 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                Http3WebtransportSession session2 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                var wtClientBidirectionalStream1 = await session1.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                var wtClientBidirectionalStream2 = await session2.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);

                var wtServerBidirectionalStream1 = await session1.AcceptInboundStreamAsync();
                var wtServerBidirectionalStream2 = await session2.AcceptInboundStreamAsync();

                byte[] recvBytes = new byte[18];
                int bytesRead = await wtServerBidirectionalStream1.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                recvBytes = new byte[18];
                bytesRead = await wtServerBidirectionalStream2.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                recvBytes = new byte[18];
                bytesRead = await wtClientBidirectionalStream1.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                recvBytes = new byte[18];
                bytesRead = await wtClientBidirectionalStream1.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                Assert.Equal((s).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));

                recvBytes = new byte[18];
                recvBytes = Encoding.ASCII.GetBytes(s);
                await wtServerBidirectionalStream1.WriteAsync(recvBytes, true).ConfigureAwait(false);
                await wtServerBidirectionalStream2.WriteAsync(recvBytes, true).ConfigureAwait(false);
                await wtClientBidirectionalStream1.WriteAsync(recvBytes, true).ConfigureAwait(false);
                await wtClientBidirectionalStream2.WriteAsync(recvBytes, true).ConfigureAwait(false);

                await semaphore.WaitAsync();

                session2.Dispose();
                session1.Dispose();

            });
            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);

        }

        [Theory]
        [InlineData(QuicStreamType.Unidirectional, true)]
        [InlineData(QuicStreamType.Unidirectional, false)]
        [InlineData(QuicStreamType.Bidirectional, true)]
        [InlineData(QuicStreamType.Bidirectional, false)]
        public async Task WebtransportStreamMultipleReadsAndWrites(QuicStreamType type, bool clientInitiated)
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();
            byte[] s_data = "Hello world!"u8.ToArray();
            const int sendCount = 5;
            int expectedBytesCount = s_data.Length * sendCount;
            byte[] expected = new byte[expectedBytesCount];
            Memory<byte> m = expected;
            for (int i = 0; i < sendCount; i++)
            {
                s_data.CopyTo(m);
                m = m[s_data.Length..];
            }
            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                var headers = new List<HttpHeaderData>();
                int contentLength = 10;
                HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
                headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
                headers.Add(header);

                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishGenericConnectionAsync(settings);
                Http3LoopbackStream stream = await connection.AcceptRequestStreamAsync();
                await stream.ReadRequestDataAsync(false);
                await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                QuicStream help;
                if(!clientInitiated)
                {
                    help = await OpenWebtransportStreamAsync(connection, type, stream.StreamId);
                }
                else
                {
                    help = await connection.AcceptRawQuicStream();
                    (long? frameType, long? sessionId) = await ReadWTFrameAsync(help);
                    Assert.Equal(stream.StreamId, sessionId);

                }

                if (!clientInitiated || (clientInitiated && type == QuicStreamType.Bidirectional))
                {
                    for (int i = 0; i < sendCount; i++)
                    {
                        await help.WriteAsync(s_data);
                    }
                    await help.WriteAsync(Memory<byte>.Empty, completeWrites: true);

                }

                if (clientInitiated || (!clientInitiated && type == QuicStreamType.Bidirectional))
                {
                    byte[] buffer = new byte[expectedBytesCount];
                    int bytesRead = await ReadAll(help, buffer);
                    Assert.Equal(expectedBytesCount, bytesRead);
                    Assert.Equal(expected, buffer);
                }

            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                QuicStream help;
                if (clientInitiated == false)
                {
                    help = await session.AcceptInboundStreamAsync();
                    Assert.NotNull(help);
                }
                else
                {
                    help = await session.OpenOutboundStreamAsync(type);
                }

                if (clientInitiated || (!clientInitiated && type == QuicStreamType.Bidirectional))
                {
                    for (int i = 0; i < sendCount; i++)
                    {
                        await help.WriteAsync(s_data);
                    }
                    await help.WriteAsync(Memory<byte>.Empty, completeWrites: true);

                }

                if (!clientInitiated || (clientInitiated && type == QuicStreamType.Bidirectional))
                {
                    byte[] buffer = new byte[expectedBytesCount];
                    int bytesRead = await ReadAll(help, buffer);
                    Assert.Equal(expectedBytesCount, bytesRead);
                    Assert.Equal(expected, buffer);
                }

               await session.DisposeAsync();
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        internal static async Task<int> ReadAll(QuicStream stream, byte[] buffer)
        {
            Memory<byte> memory = buffer;
            int bytesRead = 0;
            while (true)
            {
                int res = await stream.ReadAsync(memory);
                if (res == 0)
                {
                    break;
                }
                bytesRead += res;
                memory = memory[res..];
            }

            return bytesRead;
        }

    }
}
