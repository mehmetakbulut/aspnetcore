// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Microsoft.AspNetCore.Components.Server.Circuits
{
    internal class RemoteJSDataStream : Stream
    {
        private readonly RemoteJSRuntime _runtime;
        private readonly Guid _streamId;
        private readonly long _totalLength;
        private readonly CancellationToken _cancellationToken;
        private readonly Stream _pipeReaderStream;
        private readonly Pipe _pipe;
        private long _bytesRead;

        public static async Task<bool> SupplyData(RemoteJSRuntime runtime, string streamId, ReadOnlySequence<byte> chunk, string error)
        {
            if (!runtime.RemoteJSDataStreamInstances.TryGetValue(Guid.Parse(streamId), out var instance))
            {
                // There is no data stream with the given identifier. It may have already been disposed.
                // We notify JS that the stream has been cancelled/disposed.
                return false;
            }

            await instance.SupplyData(chunk, error);
            return true;
        }

        public static async Task<RemoteJSDataStream> CreateRemoteJSDataStreamAsync(
            RemoteJSRuntime runtime,
            IJSDataReference jsDataReference,
            long totalLength,
            long maxBufferSize,
            long maximumIncomingBytes,
            CancellationToken cancellationToken = default)
        {
            var streamId = Guid.NewGuid();
            var remoteJSDataStream = new RemoteJSDataStream(runtime, streamId, totalLength, maxBufferSize, cancellationToken);
            await runtime.InvokeVoidAsync("Blazor._internal.sendJSDataStream", jsDataReference, streamId, maximumIncomingBytes);
            return remoteJSDataStream;
        }

        private RemoteJSDataStream(
            RemoteJSRuntime runtime,
            Guid streamId,
            long totalLength,
            long maxBufferSize,
            CancellationToken cancellationToken)
        {
            _runtime = runtime;
            _streamId = streamId;
            _totalLength = totalLength;
            _cancellationToken = cancellationToken;

            _runtime.RemoteJSDataStreamInstances.Add(_streamId, this);

            _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: maxBufferSize, resumeWriterThreshold: maxBufferSize));
            _pipeReaderStream = _pipe.Reader.AsStream();
        }

        // Ideally we'd have IAsyncEnumerable<ReadOnlySequence<byte>> here so we can pass through the
        // data without having to copy it into a temporary buffer in BlazorPackHubProtocolWorker. But trying
        // this gives strange errors; sometimes the "chunk" variable below has a negative length, even though
        // the logic never returns a corrupted item as far as I can tell.
        private async Task SupplyData(ReadOnlySequence<byte> chunk, string error)
        {
            try
            {
                if (!string.IsNullOrEmpty(error))
                {
                    throw new InvalidOperationException($"An error occurred while reading the remote stream: {error}");
                }

                if (chunk.Length == 0)
                {
                    throw new InvalidOperationException($"The incoming data chunk cannot be empty.");
                }

                _bytesRead += chunk.Length;

                if (_bytesRead > _totalLength)
                {
                    throw new InvalidOperationException($"The incoming data stream declared a length {_totalLength}, but {_bytesRead} bytes were read.");
                }

                CopyToPipeWriter(chunk, _pipe.Writer);
                _pipe.Writer.Advance((int)chunk.Length);
                await _pipe.Writer.FlushAsync(_cancellationToken);

                if (_bytesRead == _totalLength)
                {
                    await _pipe.Writer.CompleteAsync();
                }
            }
            catch (Exception e)
            {
                await _pipe.Writer.CompleteAsync(e);
                return;
            }
        }

        private static void CopyToPipeWriter(ReadOnlySequence<byte> chunk, PipeWriter writer)
        {
            var pipeBuffer = writer.GetSpan((int)chunk.Length);
            chunk.CopyTo(pipeBuffer);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _pipeReaderStream.Length;

        public override long Position
        {
            get => _pipeReaderStream.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
            => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("Synchronous reads are not supported.");

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
            return await _pipeReaderStream.ReadAsync(buffer.AsMemory(offset, count), linkedCts.Token);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
            return await _pipeReaderStream.ReadAsync(buffer, linkedCts.Token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _runtime.RemoteJSDataStreamInstances.Remove(_streamId);
            }
        }
    }
}