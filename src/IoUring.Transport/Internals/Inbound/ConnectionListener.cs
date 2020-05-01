using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace IoUring.Transport.Internals.Inbound
{
    internal class ConnectionListener : IConnectionListener
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
                if (_state >= ConnectionListenerState.Disposing) throw new ObjectDisposedException(nameof(ConnectionListener));
                if (_state != ConnectionListenerState.New) throw new InvalidOperationException();
                _state = ConnectionListenerState.Binding;
            }

            Debug.WriteLine($"Binding ConnectionListener for {EndPoint}");

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
                        throw new NotSupportedException($"Unknown Endpoint {endpoint.GetType()}");
                }

                lock (Gate)
                {
                    if (_state != ConnectionListenerState.Binding) throw new InvalidOperationException();
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
                if (_state >= ConnectionListenerState.Disposing) throw new ObjectDisposedException(nameof(ConnectionListener));
                if (_state != ConnectionListenerState.Bound) throw new InvalidOperationException();
            }

            Debug.WriteLine($"Accepting on ConnectionListner for {EndPoint}");

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
                if (_state >= ConnectionListenerState.Disposing) throw new ObjectDisposedException(nameof(ConnectionListener));
                if (_state != ConnectionListenerState.Bound) throw new InvalidOperationException();
                _state = ConnectionListenerState.Unbinding;
            }

            Debug.WriteLine($"Unbinding ConnectionListner for {EndPoint}");

            try
            {
                switch (EndPoint)
                {
                    case IPEndPoint ipEndPoint:
                        var threads = _transport.TransportThreads;
                        foreach (var thread in threads)
                        {
                            await thread.Unbind(ipEndPoint);
                        }
                        break;
                    default:
                        await _transport.AcceptThread.Unbind(EndPoint);
                        break;
                }

                _acceptQueue.Writer.TryComplete();

                lock (Gate)
                {
                    if (_state != ConnectionListenerState.Unbinding) throw new InvalidOperationException();
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

            Debug.WriteLine($"Disposing ConnectionListener for {EndPoint}");

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