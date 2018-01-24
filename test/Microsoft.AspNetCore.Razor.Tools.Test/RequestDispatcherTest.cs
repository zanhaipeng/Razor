// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CommandLine;
using Moq;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Tools
{
    public class RequestDispatcherTest
    {
        private static BuildRequest EmptyBuildRequest => new BuildRequest(1, Array.Empty<BuildRequest.Argument>());

        private static BuildResponse EmptyBuildResponse => new CompletedBuildResponse(
            returnCode: 0,
            utf8output: false,
            output: string.Empty);

        [Fact]
        public async Task ReadFailure()
        {
            // Arrange
            var stream = new Mock<Stream>();
            var compilerHost = CreateCompilerHost();
            var connectionHost = CreateConnectionHost();
            var dispatcher = new DefaultRequestDispatcher(connectionHost, compilerHost, CancellationToken.None);
            var connection = CreateConnection(stream.Object);

            // Act
            var result = await dispatcher.AcceptConnection(
                Task.FromResult<Connection>(connection), accept: true, cancellationToken: CancellationToken.None);

            // Assert
            Assert.Equal(ConnectionResult.Reason.CompilationNotStarted, result.CloseReason);
        }

        /// <summary>
        /// A failure to write the results to the client is considered a client disconnection.  Any error
        /// from when the build starts to when the write completes should be handled this way. 
        /// </summary>
        [Fact]
        public async Task WriteError()
        {
            // Arrange
            var memoryStream = new MemoryStream();
            await EmptyBuildRequest.WriteAsync(memoryStream, CancellationToken.None).ConfigureAwait(true);
            memoryStream.Position = 0;

            var stream = new Mock<Stream>(MockBehavior.Strict);
            stream
                .Setup(x => x.ReadAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((byte[] array, int start, int length, CancellationToken ct) => memoryStream.ReadAsync(array, start, length, ct));

            var connection = CreateConnection(stream.Object);
            var compilerHost = CreateCompilerHost();
            compilerHost.ExecuteFunc = (req, ct) =>
            {
                return EmptyBuildResponse;
            };
            var connectionHost = CreateConnectionHost();
            var dispatcher = new DefaultRequestDispatcher(connectionHost, compilerHost, CancellationToken.None);

            // Act
            // We expect WriteAsync to fail because the mock stream doesn't have a corresponding setup.
            var connectionResult = await dispatcher.AcceptConnection(
                Task.FromResult<Connection>(connection), accept: true, cancellationToken: CancellationToken.None);

            // Assert
            Assert.Equal(ConnectionResult.Reason.ClientDisconnect, connectionResult.CloseReason);
            Assert.Null(connectionResult.KeepAlive);
        }

        /// <summary>
        /// Ensure the Connection correctly handles the case where a client disconnects while in the 
        /// middle of a build event.
        /// </summary>
        [Fact]
        public async Task ClientDisconnectsDuringBuild()
        {
            // Arrange
            var memoryStream = new MemoryStream();
            await EmptyBuildRequest.WriteAsync(memoryStream, CancellationToken.None);
            memoryStream.Position = 0;
            var connectionHost = Mock.Of<ConnectionHost>();

            // Fake a long running build task here that we can validate later on.
            var buildTaskSource = new TaskCompletionSource<bool>();
            var buildTaskCancellationToken = default(CancellationToken);
            var compilerHost = CreateCompilerHost();
            compilerHost.ExecuteFunc = (request, ct) =>
            {
                Task.WaitAll(buildTaskSource.Task);

                return EmptyBuildResponse;
            };
            
            var dispatcher = new DefaultRequestDispatcher(connectionHost, compilerHost, CancellationToken.None);
            var readyTaskSource = new TaskCompletionSource<bool>();
            var disconnectTaskSource = new TaskCompletionSource<bool>();
            var connection = CreateConnection(memoryStream);
            connection.WaitForDisconnectAsyncFunc = (ct) =>
            {
                buildTaskCancellationToken = ct;
                readyTaskSource.SetResult(true);
                return disconnectTaskSource.Task;
            };

            var handleTask = dispatcher.AcceptConnection(
                Task.FromResult<Connection>(connection), accept: true, cancellationToken: CancellationToken.None);

            // Wait until WaitForDisconnectAsync task is actually created and running.
            await readyTaskSource.Task.ConfigureAwait(false);

            // Act
            // Now simulate a disconnect by the client.
            disconnectTaskSource.SetResult(true);
            var connectionResult = await handleTask;
            buildTaskSource.SetResult(true);

            // Assert
            Assert.Equal(ConnectionResult.Reason.ClientDisconnect, connectionResult.CloseReason);
            Assert.Null(connectionResult.KeepAlive);
            Assert.True(buildTaskCancellationToken.IsCancellationRequested);
        }

        [Fact]
        public async Task AcceptFalse_RejectsBuildRequest()
        {
            // Arrange
            var stream = new TestableStream();
            await EmptyBuildRequest.WriteAsync(stream.ReadStream, CancellationToken.None);
            stream.ReadStream.Position = 0;

            var connection = CreateConnection(stream);
            var connectionHost = CreateConnectionHost();
            var compilerHost = CreateCompilerHost();
            var dispatcher = new DefaultRequestDispatcher(connectionHost, compilerHost, CancellationToken.None);

            // Act
            var connectionResult = await dispatcher.AcceptConnection(
                Task.FromResult<Connection>(connection), accept: false, cancellationToken: CancellationToken.None);

            // Assert
            Assert.Equal(ConnectionResult.Reason.CompilationNotStarted, connectionResult.CloseReason);
            stream.WriteStream.Position = 0;
            var response = await BuildResponse.ReadAsync(stream.WriteStream).ConfigureAwait(false);
            Assert.Equal(BuildResponse.ResponseType.Rejected, response.Type);
        }

        [Fact]
        public async Task ShutdownRequest_ReturnsShutdownResponse()
        {
            // Arrange
            var stream = new TestableStream();
            await BuildRequest.CreateShutdown().WriteAsync(stream.ReadStream, CancellationToken.None);
            stream.ReadStream.Position = 0;

            var connection = CreateConnection(stream);
            var connectionHost = CreateConnectionHost();
            var compilerHost = CreateCompilerHost();
            var dispatcher = new DefaultRequestDispatcher(connectionHost, compilerHost, CancellationToken.None);

            // Act
            var connectionResult = await dispatcher.AcceptConnection(
                Task.FromResult<Connection>(connection), accept: true, cancellationToken: CancellationToken.None);

            // Assert
            Assert.Equal(ConnectionResult.Reason.ClientShutdownRequest, connectionResult.CloseReason);
            stream.WriteStream.Position = 0;
            var response = await BuildResponse.ReadAsync(stream.WriteStream).ConfigureAwait(false);
            Assert.Equal(BuildResponse.ResponseType.Shutdown, response.Type);
        }

        private TestableConnection CreateConnection(Stream stream, string identifier = null)
        {
            return new TestableConnection(stream, identifier ?? "identifier");
        }

        private ConnectionHost CreateConnectionHost()
        {
            return Mock.Of<ConnectionHost>();
        }

        private TestableCompilerHost CreateCompilerHost()
        {
            return new TestableCompilerHost();
        }

        private class TestableCompilerHost : CompilerHost
        {
            internal Func<BuildRequest, CancellationToken, BuildResponse> ExecuteFunc;

            public override BuildResponse Execute(BuildRequest request, CancellationToken cancellationToken)
            {
                if (ExecuteFunc != null)
                {
                    return ExecuteFunc(request, cancellationToken);
                }

                return EmptyBuildResponse;
            }
        }

        private class TestableConnection : Connection
        {
            internal Func<CancellationToken, Task> WaitForDisconnectAsyncFunc;

            public TestableConnection(Stream stream, string identifier)
            {
                Stream = stream;
                Identifier = identifier;
                WaitForDisconnectAsyncFunc = ct => Task.Delay(Timeout.Infinite, ct);
            }

            public override Task WaitForDisconnectAsync(CancellationToken cancellationToken)
            {
                return WaitForDisconnectAsyncFunc(cancellationToken);
            }
        }

        private sealed class TestableStream : Stream
        {
            internal readonly MemoryStream ReadStream = new MemoryStream();
            internal readonly MemoryStream WriteStream = new MemoryStream();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length { get { throw new NotImplementedException(); } }
            public override long Position
            {
                get { throw new NotImplementedException(); }
                set { throw new NotImplementedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return ReadStream.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadStream.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteStream.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return WriteStream.WriteAsync(buffer, offset, count, cancellationToken);
            }
        }
    }
}
