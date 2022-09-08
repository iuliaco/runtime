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
using static System.Net.Test.Common.Configuration;
using static System.Net.Test.Common.LoopbackServer;

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
        public async Task ClientSettingsWebTransportAPIServer()
        {
            using var listener = new TestUtilities.TestEventListener(_output, TestUtilities.TestEventListener.NetworkingEvents);

            Task clientTask = Task.Run(async () =>
            {
                Http3WebtransportSession session = await Http3WebtransportSession.connectAsync(new Uri("https://127.0.0.1:5002/"), CreateHttpClientHandler(), CancellationToken.None);
                Console.WriteLine("LOOOOOL    Aici are id ul " + session.id);

                await Task.Delay(3_000);
                await session.DisposeAsync();

                /*     QuicStream help = await session.getIncomingWTStreamFromServerAsync();
                     // string s = "Ana are mere";
                     byte[] bytes = new byte[150]; //= Encoding.ASCII.GetBytes(s);
                     Console.Write("Connect stream details " + session.getStreamStatus());
                     Console.WriteLine("New wt stream details======================================= " + help.Id + " " + help.CanRead + " " + help);

                     await help.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                     help = await session.getIncomingWTStreamFromServerAsync();

                     Console.WriteLine(Encoding.ASCII.GetString(bytes));
                     await help.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false);

                     Console.WriteLine(Encoding.ASCII.GetString(bytes))*/
                ;
                await Task.Delay(10_000);
            });

            await new[] { clientTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task ClientSettingsWebTransportAPI()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {


                // full client check
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);
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

                    for (int i = 0; i < 20; i++)
                    {
                        var wtServerBidirectionalStream = await connection.OpenBidirectionalWTStreamAsync(stream.StreamId);
                        byte[] recvBytes = new byte[18];
                        int bytesRead = 0;
                        bytesRead = await wtServerBidirectionalStream.ReadDataStreamAsync(recvBytes);
                        while (bytesRead != 0)
                        {
                            Console.WriteLine("AM PRIMIT SERVER " + Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                            bytesRead = await wtServerBidirectionalStream.ReadDataStreamAsync(recvBytes);

                        }
                        string s = "Ana nu are mere " + i;
                        recvBytes = Encoding.ASCII.GetBytes(s);
                        await wtServerBidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false);

                    }

                    for (int i = 0; i < 10; i++)
                    {
                        var wtServerUnidirectionalStream = await connection.OpenUnidirectionalWTStreamAsync(stream.StreamId);
                        byte[] recvBytes = new byte[18];
                        string s = "Ana nu are mere " + i;
                        recvBytes = Encoding.ASCII.GetBytes(s);
                        await wtServerUnidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false);
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();

                Http3WebtransportSession session = await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None);
                Console.WriteLine("AJUNG AICIIIII");
                for (int i = 0; i < 20; i++)
                {
                    QuicStream help = await session.getIncomingWTStreamFromServerAsync();
                    string s = "Ana are mere " + i;
                    byte[] bytes = new byte[20];
                    bytes = Encoding.ASCII.GetBytes(s);
                    await help.WriteAsync(bytes, true, CancellationToken.None).ConfigureAwait(false);
                    bytes = new byte[20];
                    int bytesRead = await help.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                    while (bytesRead != 0)
                    {
                        Console.WriteLine("AM PRIMIT CLIENT " + Encoding.ASCII.GetString(bytes).Substring(0, bytesRead));
                        bytes = new byte[20];
                        bytesRead = await help.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                    }

                    await help.DisposeAsync();
                }

                for (int i = 0; i < 10; i++)
                {

                    QuicStream help = await session.getIncomingWTStreamFromServerAsync();
                    byte[] bytes = new byte[20];
                    int bytesRead = await help.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                    while (bytesRead != 0)
                    {
                        Console.WriteLine("AM PRIMIT CLIENT UNIDIRECTIONAL " + Encoding.ASCII.GetString(bytes).Substring(0, bytesRead));
                        bytes = new byte[20];
                        bytesRead = await help.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                    }
                    await help.DisposeAsync();
                }
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        // Needs semaphore
        [Fact]
        public async Task WebtransportSessionGoneError()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);

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
                    await Task.Delay(2_000);
                    Console.WriteLine(wtServerBidirectionalStream.CanWrite + "Pot scrie sau ceva?");
                    byte[] recvBytes = new byte[18];
                    string s = "Ana nu are mere ";
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await wtServerBidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false));
                    Console.WriteLine(ex.ApplicationErrorCode + " Pot scrie sau ceva??????????????????????");
                    Assert.Equal(276659048, ex.ApplicationErrorCode);
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                Http3WebtransportSession session = await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None);

                await session.DisposeAsync();
                await Task.Delay(2_000);

            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }


        // Needs semaphore
        [Fact]
        public async Task WebtransportBufferedStreamError()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);
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

                    var wtServerBidirectionalStream = await connection.OpenBidirectionalWTStreamAsync(stream.StreamId + 1);
                    await Task.Delay(1_000);
                    Console.WriteLine(wtServerBidirectionalStream.CanWrite + "Pot scrie sau ceva?");
                    byte[] recvBytes = new byte[18];
                    string s = "Ana nu are mere ";
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    QuicException ex = await Assert.ThrowsAsync<QuicException>(async () => await wtServerBidirectionalStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false));
                    Console.WriteLine(ex.ApplicationErrorCode + " Pot scrie sau ceva??????????????????????");
                    Assert.Equal(966049156, ex.ApplicationErrorCode);
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                Http3WebtransportSession session = await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None);

                await Task.Delay(5_000);

            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task SendWebtransportClientStreams()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);
                var headers = new List<HttpHeaderData>();
                int contentLength = 2 * 1024 * 1024;
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
                        Http3LoopbackStream clientStream = await connection.AcceptWebtransportStreamAsync();
                        (long? frameType, long? sessionId) = await clientStream.ReadWTFrameAsync();
                        Assert.Equal(stream.StreamId, sessionId);
                        byte[] recvBytes = new byte[20];
                        int bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);
                        while (bytesRead != 0)
                        {
                            Console.WriteLine("AM PRIMIT SERVER " + Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                            recvBytes = new byte[20];
                            bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);

                        }
                        string s = "Ana are mere " + i;
                        recvBytes = Encoding.ASCII.GetBytes(s);
                        await clientStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false);
                    }

                    for (int i = 0; i < 20; i++)
                    {
                        Http3LoopbackStream clientStream = await connection.AcceptWebtransportStreamAsync();
                        (long? frameType, long? sessionId) = await clientStream.ReadWTFrameAsync();
                        Assert.Equal(stream.StreamId, sessionId);
                        byte[] recvBytes = new byte[20];
                        int bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);
                        while (bytesRead != 0)
                        {
                            Console.WriteLine("AM PRIMIT SERVER " + Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                            recvBytes = new byte[20];
                            bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);

                        }
                    }
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                Http3WebtransportSession session = await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None);
                for (int i = 0; i < 20; i++)
                {
                    var wtClientBidirectionalStream = await session.OpenWebtransportStreamAsync(QuicStreamType.Bidirectional);
                    byte[] recvBytes = new byte[20];
                    string s = "Ana nu are mere " + i;
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    await wtClientBidirectionalStream.WriteAsync(recvBytes, true);
                    recvBytes = new byte[20];
                    int bytesRead = await wtClientBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    while (bytesRead != 0)
                    {
                        Console.WriteLine("AM PRIMIT CLIENT " + Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                        recvBytes = new byte[20];
                        bytesRead = await wtClientBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    }
                    await wtClientBidirectionalStream.DisposeAsync();
                }

                for (int i = 0; i < 20; i++)
                {
                    var wtClientBidirectionalStream = await session.OpenWebtransportStreamAsync(QuicStreamType.Unidirectional);
                    byte[] recvBytes = new byte[20];
                    string s = "Ana nu are mere " + i;
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    await wtClientBidirectionalStream.WriteAsync(recvBytes, true);
                    await wtClientBidirectionalStream.DisposeAsync();
                }
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task DemoSendWebtransportClientStreams()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);
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
                    await stream.ReadRequestDataAsync();
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers, "", false);
                    for (int i = 0; i < 20; i++)
                    {
                        Http3LoopbackStream clientStream = await connection.AcceptWebtransportStreamAsync();
                        (long? frameType, long? sessionId) = await clientStream.ReadWTFrameAsync();
                        Assert.Equal(stream.StreamId, sessionId);
                        byte[] recvBytes = new byte[300];
                        int bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);
                        while (bytesRead != 0)
                        {
                            Console.WriteLine("Received on the serverside: " + Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                            recvBytes = new byte[300];
                            bytesRead = await clientStream.ReadDataStreamAsync(recvBytes);

                        }
                        string s = "Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. " + i;
                        recvBytes = Encoding.ASCII.GetBytes(s);
                        await clientStream.SendDataStreamAsync(recvBytes).ConfigureAwait(false);
                    }

                }
            });

            Task clientTask = Task.Run(async () =>
            {
                Http3WebtransportSession session = await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None);
                for (int i = 0; i < 20; i++)
                {
                    var wtClientBidirectionalStream = await session.OpenWebtransportStreamAsync(QuicStreamType.Bidirectional);
                    byte[] recvBytes = new byte[300];
                    string s = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.  " + i;
                    recvBytes = Encoding.ASCII.GetBytes(s);
                    await wtClientBidirectionalStream.WriteAsync(recvBytes, true);
                    recvBytes = new byte[100];
                    int bytesRead = await wtClientBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    while (bytesRead != 0)
                    {
                        Console.WriteLine("Received on the clientside: " + Encoding.ASCII.GetString(recvBytes).Substring(0, bytesRead));
                        recvBytes = new byte[100];
                        bytesRead = await wtClientBidirectionalStream.ReadAsync(recvBytes, CancellationToken.None).ConfigureAwait(false);
                    }
                    await wtClientBidirectionalStream.DisposeAsync();
                }

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
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);
                (Http3LoopbackStream settingsStream, Http3LoopbackStream stream) = await connection.AcceptControlAndRequestStreamAsync();

                await using (settingsStream)
                await using (stream)
                {
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync();
                    await stream.SendResponseAsync();
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None));
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportWrongServerResponseStatus()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                // full client check
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);
                (Http3LoopbackStream settingsStream, Http3LoopbackStream stream) = await connection.AcceptControlAndRequestStreamAsync();

                await using (settingsStream)
                await using (stream)
                {
                    Assert.Equal(1, connection.EnableWebtransport);
                    await stream.ReadRequestDataAsync();
                    await stream.SendResponseAsync(HttpStatusCode.NotFound);
                }
            });

            Task clientTask = Task.Run(async () =>
            {
                HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None));
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task WebTransportServerNotSupported()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            Task serverTask = Task.Run(async () =>
            {
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync();

            });

            Task clientTask = Task.Run(async () =>
            {
                HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await Http3WebtransportSession.connectAsync(server.Address, CreateHttpClientHandler(), CancellationToken.None));
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task ClientWebTransportMultipleSessions_Success()
        {
            using Http3LoopbackServer server = CreateHttp3LoopbackServer();

            var headers = new List<HttpHeaderData>();
            int contentLength = 2 * 1024 * 1024;
            HttpHeaderData header = new HttpHeaderData("sec-webtransport-http3-draft", "draft02");
            headers.Add(new HttpHeaderData("Content-Length", contentLength.ToString(CultureInfo.InvariantCulture)));
            headers.Add(header);
            headers.Append(header);
            Task serverTask = Task.Run(async () =>
            {
                // full client check
                ICollection<(long settingId, long settingValue)> settings = new LinkedList<(long settingId, long settingValue)>();
                settings.Add((Http3LoopbackStream.EnableWebTransport, 1));
                await using Http3LoopbackConnection connection = (Http3LoopbackConnection)await server.EstablishSettingsFrameGenericConnectionAsync(settings);

                (Http3LoopbackStream settingsStream, Http3LoopbackStream stream) = await connection.AcceptControlAndRequestStreamAsync();

                await using (settingsStream)
                await using (stream)
                {
                    Assert.Equal(1, connection.EnableWebtransport);
                    var requestData = await stream.ReadRequestDataAsync();
                    Console.Write(requestData);
                    Assert.Equal(1, requestData.GetHeaderValueCount("sec-webtransport-http3-draft02"));
                    Assert.Equal("1", requestData.GetSingleHeaderValue("sec-webtransport-http3-draft02"));
                    await stream.SendResponseAsync(HttpStatusCode.OK, headers);
                }

                await using Http3LoopbackStream stream2 = await connection.AcceptRequestStreamAsync();
                await stream2.HandleRequestAsync(HttpStatusCode.OK, headers);
                await using Http3LoopbackStream stream3 = await connection.AcceptRequestStreamAsync();
                await stream3.HandleRequestAsync(HttpStatusCode.OK, headers);
                var wtClientUnidirectionalStream = await connection.AcceptWebtransportStreamAsync();
                (long? frameType, long? sessionId) = await wtClientUnidirectionalStream.ReadWTFrameAsync();
                Console.Write("OOOO " + sessionId + " " + stream.StreamId);

                var wtClientBidirectionalStream = await connection.AcceptWebtransportStreamAsync();
                (long? frameType2, long? sessionId2) = await wtClientBidirectionalStream.ReadWTFrameAsync();
                Console.Write("OOOO " + sessionId2 + " " + stream.StreamId);
                var wtServerUnidirectionalStream = await connection.OpenUnidirectionalWTStreamAsync(sessionId);
                var wtServerBidirectionalStream = await connection.OpenBidirectionalWTStreamAsync(sessionId);
                //  wtServerUnidirectionalStream.
                byte[] bytes = new byte[35];// = Encoding.ASCII.GetBytes(s);
                await Task.Delay(5_000);

                await wtClientUnidirectionalStream.ReadDataStreamAsync(bytes);
                Console.Write(Encoding.ASCII.GetString(bytes));
                await wtClientBidirectionalStream.ReadDataStreamAsync(bytes);
                Console.Write(Encoding.ASCII.GetString(bytes));
                bytes = bytes.Reverse().ToArray();
                Console.Write("Reversed?? " + Encoding.ASCII.GetString(bytes));

                await wtClientBidirectionalStream.SendDataStreamAsync(bytes);

            });

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                using HttpRequestMessage request2 = new()
                {
                    Method = HttpMethod.Connect,
                    RequestUri = server.Address,
                    Version = HttpVersion30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request2.Headers.Protocol = "webtransport";
                using HttpResponseMessage response2 = await client.SendAsync(request2);
                HttpRequestMessage request = new(HttpMethod.Connect, server.Address);
                request.Version = HttpVersion.Version30;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request.Headers.Protocol = "webtransport";
                using HttpResponseMessage response = await client.SendAsync(request);
                Console.Write(response);
                HttpRequestMessage request3 = new(HttpMethod.Connect, server.Address);
                request3.Version = HttpVersion.Version30;
                request3.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request3.Headers.Protocol = "webtransport";
                using HttpResponseMessage response3 = await client.SendAsync(request3);
                // await Task.Delay(10_000);
            });

            await new[] { clientTask, serverTask }.WhenAllOrAnyFailed(20_000);
        }

        [Fact]
        public async Task ClientSettingsWebTransportServerReceived_Success()
        {
            // using var listener = new TestUtilities.TestEventListener(_output, TestUtilities.TestEventListener.NetworkingEvents);

            Task clientTask = Task.Run(async () =>
            {
                using HttpClient client = CreateHttpClient();
                using HttpRequestMessage request = new()
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri("https://127.0.0.1:5002/"),
                    Version = HttpVersion30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                using HttpResponseMessage response = await client.SendAsync(request);
                Console.Write(response);
                using HttpRequestMessage request3 = new()
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri("https://127.0.0.1:5002/"),
                    Version = HttpVersion30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                using HttpResponseMessage response3 = await client.SendAsync(request3);
                Console.Write(response3);
                HttpRequestMessage request2 = new(HttpMethod.Connect, new Uri("https://127.0.0.1:5002/"));
                request2.Version = HttpVersion.Version30;
                request2.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request2.Headers.Protocol = "webtransport";
                using HttpResponseMessage response2 = await client.SendAsync(true, request2, HttpCompletionOption.ResponseHeadersRead);
                // using HttpResponseMessage response2 = Http3WebtransportSession.connectAsync();
                //using HttpResponseMessage response2 = await client.SendAsync(request2);
                Console.WriteLine(response2);

                // await Task.Delay(10_000);

            });


            await new[] { clientTask }.WhenAllOrAnyFailed(20_000);
        }
    }
}
