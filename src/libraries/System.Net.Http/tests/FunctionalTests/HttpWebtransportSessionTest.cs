// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Test.Common;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
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
                    Assert.Equal(1, connection.EnableWebtransport);
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
                    Assert.Equal(1, connection.EnableWebtransport);
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
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    var wtServerBidirectionalStream = await connection.OpenBidirectionalWTStreamAsync(stream.StreamId);
                    await semaphore.WaitAsync();
                    byte[] recvBytes = new byte[18];
                    string s = "Hello World ";
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    await semaphore.WaitAsync();
                    QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await wtServerBidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false));
                    Assert.Equal(276659048, ex.ApplicationErrorCode);
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
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    var wtServerBidirectionalStream = await connection.OpenBidirectionalWTStreamAsync(stream.StreamId + 1);
                    // Delay needed so quic will not unify the header and the body of the wt stream
                    await Task.Delay(500);

                    byte[] recvBytes = new byte[18];
                    string s = "Hellp world";
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await wtServerBidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false));
                    Assert.Equal(966049156, ex.ApplicationErrorCode);
                    semaphore.Release();

                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
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
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                    for (int i = 0; i < 20; i++)
                    {
                        Http3LoopbackStream clientStream = await connection.AcceptRequestStreamAsync(false);
                        (long? frameType, long? sessionId) = await clientStream.ReadWTFrameAsync();
                        Assert.Equal(stream.StreamId, sessionId);
                        byte[] recvBytes = new byte[20];

                        int bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);
                        while (bytesRead != 0)
                        {
                            Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                            recvBytes = new byte[20];
                            bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);

                        }
                        recvBytes = Encoding.ASCII.GetBytes(s + i);
                        await clientStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false);
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                for (int i = 0; i < 20; i++)
                {
                    var wtClientBidirectionalStream = await session.OpenWebtransportStreamAsync(QuicStreamType.Bidirectional);
                    byte[] recvBytes = new byte[20];
                    recvBytes = Encoding.ASCII.GetBytes(s + i);
                    await wtClientBidirectionalStream.WriteAsync(recvBytes, true);
                    recvBytes = new byte[20];
                    int bytesRead = await wtClientBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    while (bytesRead != 0)
                    {
                        Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                        recvBytes = new byte[20];
                        bytesRead = await wtClientBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    }
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
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    for (int i = 0; i < 20; i++)
                    {
                        Http3LoopbackStream clientStream = await connection.AcceptRequestStreamAsync(false);
                        (long? frameType, long? sessionId) = await clientStream.ReadWTFrameAsync();
                        Assert.Equal(stream.StreamId, sessionId);
                        byte[] recvBytes = new byte[20];
                        int bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);
                        while (bytesRead != 0)
                        {
                            Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                            recvBytes = new byte[20];
                            bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);

                        }
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);

                for (int i = 0; i < 20; i++)
                {
                    var wtClientBidirectionalStream = await session.OpenWebtransportStreamAsync(QuicStreamType.Unidirectional);
                    byte[] recvBytes = new byte[20];
                    recvBytes = Encoding.ASCII.GetBytes(s + i);
                    await wtClientBidirectionalStream.WriteAsync(recvBytes, true);
                    await wtClientBidirectionalStream.DisposeAsync();
                }
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task SendWebtransportBidirectionServerStreamsReadWrite()
        {
            using HttpClient client = CreateHttpClient();
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

                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    for (int i = 0; i < 20; i++)
                    {
                        var wtServerBidirectionalStream = await connection.OpenBidirectionalWTStreamAsync(stream.StreamId);
                        byte[] recvBytes = new byte[18];
                        int bytesRead = 0;
                        bytesRead = await wtServerBidirectionalStream.ReadDataStreamAsync(recvBytes);
                        while (bytesRead != 0)
                        {
                            Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                            bytesRead = await wtServerBidirectionalStream.ReadDataStreamAsync(recvBytes);

                        }
                        recvBytes = Encoding.ASCII.GetBytes(s + i);
                        await wtServerBidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false);

                    }

                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                for (int i = 0; i < 20; i++)
                {
                    QuicStream help = await session.GetIncomingWTStreamFromServerAsync();
                    byte[] recvBytes = new byte[20];
                    recvBytes = Encoding.ASCII.GetBytes(s + i);
                    await help.WriteAsync(recvBytes, true, CancellationToken.None).ConfigureAwait(false);
                    recvBytes = new byte[20];
                    int bytesRead = await help.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    while (bytesRead != 0)
                    {
                        Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                        recvBytes = new byte[20];
                        bytesRead = await help.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    }

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

                await using (stream)
                {
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    for (int i = 0; i < 10; i++)
                    {
                        var wtServerUnidirectionalStream = await connection.OpenUnidirectionalWTStreamAsync(stream.StreamId);
                        byte[] recvBytes = new byte[20];
                        recvBytes = Encoding.ASCII.GetBytes(s + i);
                        await wtServerUnidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false);
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);

                for (int i = 0; i < 10; i++)
                {

                    QuicStream help = await session.GetIncomingWTStreamFromServerAsync();
                    byte[] recvBytes = new byte[20];
                    int bytesRead = await help.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    while (bytesRead != 0)
                    {
                        Assert.Equal((s + i).Substring(0, bytesRead), Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                        recvBytes = new byte[20];
                        bytesRead = await help.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    }
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
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync(false);
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);

                    var wtServerUnidirectionalStream = await connection.OpenUnidirectionalWTStreamAsync(stream.StreamId);
                    
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                QuicStream help = await session.GetIncomingWTStreamFromServerAsync();
                byte[] recvBytes = new byte[20];
                recvBytes = Encoding.ASCII.GetBytes(s);
                InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await help.WriteAsync(recvBytes, true, CancellationToken.None).ConfigureAwait(false));

                await help.DisposeAsync();
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
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                Http3WebtransportSession session1 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                Http3WebtransportSession session2 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                await session2.DisposeAsync();
                Http3WebtransportSession session3 = await Http3WebtransportSession.ConnectAsync(server.Address, client, CancellationToken.None);
                await semaphore.WaitAsync();
                Assert.Null(await session2.OpenWebtransportStreamAsync(QuicStreamType.Unidirectional));
                Assert.Null(await session3.OpenWebtransportStreamAsync(QuicStreamType.Unidirectional));
                await semaphore.WaitAsync();
                Assert.Null(await session1.OpenWebtransportStreamAsync(QuicStreamType.Unidirectional));
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }
    }
}
