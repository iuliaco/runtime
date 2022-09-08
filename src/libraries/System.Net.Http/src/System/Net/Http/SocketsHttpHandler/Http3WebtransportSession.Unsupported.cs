// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [UnsupportedOSPlatform("browser")]
    public class Http3WebtransportSession : IAsyncDisposable, IDisposable
    {
        public long id { get => throw new PlatformNotSupportedException(); }
        public bool GetStreamStatus() => throw new PlatformNotSupportedException();
        public System.Threading.Channels.Channel<System.Net.Quic.QuicStream> IncomingStreamsQueue { get => throw new PlatformNotSupportedException(); }
        public Http3WebtransportSession(System.Net.Quic.QuicConnection connection, System.Net.Quic.QuicStream connectStream) => throw new PlatformNotSupportedException();
        public System.Threading.Tasks.ValueTask<QuicStream> GetIncomingWTStreamFromServerAsync() => throw new PlatformNotSupportedException();
        public ValueTask DisposeAsync() => throw new PlatformNotSupportedException();
        public static ValueTask<Http3WebtransportSession?> ConnectAsync(Uri uri, HttpClientHandler? handler, CancellationToken cancellationToken) => throw new PlatformNotSupportedException();
        public ValueTask<QuicStream?> OpenWebtransportStreamAsync(QuicStreamType type) => throw new PlatformNotSupportedException();
        public void Dispose() => throw new PlatformNotSupportedException();

    }
}
