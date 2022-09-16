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
        internal Http3WebtransportSession() => throw new PlatformNotSupportedException();
        public long Id { get => throw new PlatformNotSupportedException(); }
        public System.Threading.Tasks.ValueTask<QuicStream?> GetIncomingWebtransportStreamFromServerAsync() => throw new PlatformNotSupportedException();
        public bool TryGetIncomingWebtransportStreamFromServer(out System.Net.Quic.QuicStream? quicStream) => throw new PlatformNotSupportedException();
        public ValueTask DisposeAsync() => throw new PlatformNotSupportedException();
        public static ValueTask<Http3WebtransportSession?> ConnectAsync(Uri uri, HttpMessageInvoker? invoker, CancellationToken cancellationToken) => throw new PlatformNotSupportedException();
        public ValueTask<QuicStream?> OpenWebtransportStreamAsync(QuicStreamType type) => throw new PlatformNotSupportedException();
        public void Dispose() => throw new PlatformNotSupportedException();

    }
}
