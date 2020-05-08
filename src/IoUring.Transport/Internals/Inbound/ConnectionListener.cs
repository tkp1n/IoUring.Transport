using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class ConnectionListener : IConnectionListener
    {
        private readonly IoUringTransport _transport;
        private readonly Channel<ConnectionContext> _acceptQueue;
        private ConnectionListenerState _state = ConnectionListenerState.New;

        private ConnectionListener(EndPoint endpoint, IoUringTransport transport, IoUringOptions options)
        {
            _transport = transport;
            _acceptQueue = Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions
            {
                SingleReader = true, // reads happen sequentially
                SingleWriter = false,
                AllowSynchronousContinuations = options.ApplicationSchedulingMode == PipeScheduler.Inline
            });

            EndPoint = endpoint;
        }

        public EndPoint EndPoint { get; private set; }

        private object Gate => this;

        public static async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, IoUringTransport transport, IoUringOptions options)
        {
            var listener = new ConnectionListener(endpoint, transport, options);
            await listener.BindAsync();
            return listener;
        }

        private async ValueTask BindAsync()
        {
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionListener);
                if (_state != ConnectionListenerState.New) ThrowHelper.ThrowNewInvalidOperationException();
                _state = ConnectionListenerState.Binding;
            }

            try
            {
                _transport.IncrementThreadRefCount();
                var endpoint = EndPoint;
                switch (endpoint)
                {
                    case IPEndPoint ipEndPoint when ipEndPoint.AddressFamily == AddressFamily.InterNetwork || ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6:
                        var threads = _transport.TransportThreads;
                        EndPoint boundEndPoint = endpoint;
                        foreach (var thread in threads)
                        {
                            boundEndPoint = thread.Bind(ipEndPoint, _acceptQueue);
                        }

                        EndPoint = boundEndPoint;

                        break;
                    case UnixDomainSocketEndPoint unixDomainSocketEndPoint:
                        EndPoint = _transport.AcceptThread.Bind(unixDomainSocketEndPoint, _acceptQueue);
                        break;
                    case FileHandleEndPoint fileHandleEndPoint:
                        EndPoint = _transport.AcceptThread.Bind(fileHandleEndPoint, _acceptQueue);
                        break;
                    default:
                        ThrowHelper.ThrowNewNotSupportedException_EndPointNotSupported();
                        break;
                }

                lock (Gate)
                {
                    if (_state != ConnectionListenerState.Binding) ThrowHelper.ThrowNewInvalidOperationException();
                    _state = ConnectionListenerState.Bound;
                }
            }
            catch (Exception)
            {
                await DisposeAsync();
                throw;
            }
        }

        public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionListener);
                if (_state != ConnectionListenerState.Bound) ThrowHelper.ThrowNewInvalidOperationException();
            }

            await foreach (var connection in _acceptQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                return connection;
            }

            return null;
        }

        public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionListener);
                if (_state != ConnectionListenerState.Bound) ThrowHelper.ThrowNewInvalidOperationException();
                _state = ConnectionListenerState.Unbinding;
            }

            try
            {
                if (EndPoint is IPEndPoint ipEndPoint)
                {
                    var threads = _transport.TransportThreads;
                    foreach (var thread in threads)
                    {
                        await thread.Unbind(ipEndPoint);
                    }
                }
                else
                {
                    await _transport.AcceptThread.Unbind(EndPoint);
                }

                _acceptQueue.Writer.TryComplete();

                lock (Gate)
                {
                    if (_state != ConnectionListenerState.Unbinding) ThrowHelper.ThrowNewInvalidOperationException();
                    _state = ConnectionListenerState.Unbound;
                }
            }
            catch (Exception)
            {
                await DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing)
                {
                    return default; // Dispose already in progress
                }

                _state = ConnectionListenerState.Disposing;
            }

            _acceptQueue.Writer.TryComplete();
            _transport.DecrementThreadRefCount();

            lock (Gate)
            {
                _state = ConnectionListenerState.Disposed;
            }

            return default;
        }

        private enum ConnectionListenerState
        {
            New,
            Binding,
            Bound,
            Unbinding,
            Unbound,
            Disposing,
            Disposed
        }
    }
}