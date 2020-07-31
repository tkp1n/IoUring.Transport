using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Connections;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals.Inbound
{
    internal sealed class IoUringConnectionListener : ConnectionListener
    {
        private readonly IoUringTransport _transport;
        private readonly IoUringOptions _options;
        private readonly Channel<IoUringConnection> _acceptQueue;
        private EndPoint _localEndPoint;
        private ConnectionListenerState _state = ConnectionListenerState.New;

        private IoUringConnectionListener(EndPoint endpoint, IoUringTransport transport, IoUringOptions options, IConnectionProperties listenerProperties)
        {
            _transport = transport;
            _options = options;
            ListenerProperties = listenerProperties;
            _acceptQueue = Channel.CreateUnbounded<IoUringConnection>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = options.ApplicationSchedulingMode == PipeScheduler.Inline
            });

            _localEndPoint = endpoint;
        }

        public override EndPoint LocalEndPoint => _localEndPoint;

        public override IConnectionProperties ListenerProperties { get; }

        private object Gate => this;

        public static async ValueTask<ConnectionListener> BindAsync(EndPoint endpoint, IoUringTransport transport, IoUringOptions ioUringOptions, IConnectionProperties options = null, CancellationToken cancellationToken = default)
        {
            var listener = new IoUringConnectionListener(endpoint, transport, ioUringOptions, options);
            await listener.BindAsync(cancellationToken);
            return listener;
        }

        private async ValueTask BindAsync(CancellationToken cancellationToken = default)
        {
            // TODO: support cancellation
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionListener);
                if (_state != ConnectionListenerState.New) ThrowHelper.ThrowNewInvalidOperationException();
                _state = ConnectionListenerState.Binding;
            }

            try
            {
                _transport.IncrementThreadRefCount();
                var endpoint = LocalEndPoint;
                switch (endpoint)
                {
                    case IPEndPoint ipEndPoint when ipEndPoint.AddressFamily == AddressFamily.InterNetwork || ipEndPoint.AddressFamily == AddressFamily.InterNetworkV6:
                        var threads = _transport.TransportThreads;
                        EndPoint boundEndPoint = endpoint;
                        foreach (var thread in threads)
                        {
                            boundEndPoint = thread.Bind(ipEndPoint, _acceptQueue);
                        }

                        _localEndPoint = boundEndPoint;
                        if (_options.ReceiveOnIncomingCpu)
                        {
                            threads[0].SetReceiveOnIncomingCpu((IPEndPoint) boundEndPoint);
                        }

                        break;
                    case UnixDomainSocketEndPoint unixDomainSocketEndPoint:
                        _localEndPoint = _transport.AcceptThread.Bind(unixDomainSocketEndPoint, _acceptQueue);
                        break;
                    case FileHandleEndPoint fileHandleEndPoint:
                        _localEndPoint = _transport.AcceptThread.Bind(fileHandleEndPoint, _acceptQueue);
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

        public override async ValueTask<Connection> AcceptAsync(IConnectionProperties options = null, CancellationToken cancellationToken = default)
        {
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionListener);
                if (_state != ConnectionListenerState.Bound) ThrowHelper.ThrowNewInvalidOperationException();
            }

            await foreach (var connection in _acceptQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                connection.ProvidedProperties = options;
                return connection;
            }

            return null;
        }

        private async ValueTask UnbindAsync()
        {
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.ConnectionListener);
                if (_state != ConnectionListenerState.Bound) ThrowHelper.ThrowNewInvalidOperationException();
                _state = ConnectionListenerState.Unbinding;
            }

            try
            {
                if (LocalEndPoint is IPEndPoint ipEndPoint)
                {
                    var threads = _transport.TransportThreads;
                    foreach (var thread in threads)
                    {
                        await thread.Unbind(ipEndPoint);
                    }
                }
                else
                {
                    await _transport.AcceptThread.Unbind(LocalEndPoint);
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

        protected override void Dispose(bool disposing)
        {
            ValueTask t = DisposeAsyncCore();

            if (t.IsCompleted) t.GetAwaiter().GetResult();
            t.AsTask().GetAwaiter().GetResult();
        }

        protected override async ValueTask DisposeAsyncCore()
        {
            await UnbindAsync();
            lock (Gate)
            {
                if (_state >= ConnectionListenerState.Disposing)
                {
                    return; // Dispose already in progress
                }

                _state = ConnectionListenerState.Disposing;
            }

            _acceptQueue.Writer.TryComplete();
            _transport.DecrementThreadRefCount();

            lock (Gate)
            {
                _state = ConnectionListenerState.Disposed;
            }
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