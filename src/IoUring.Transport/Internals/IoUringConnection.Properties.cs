using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Connections;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;

namespace IoUring.Transport.Internals
{
    internal abstract partial class IoUringConnection : Connection,
                                                         IConnectionProperties,
                                                         IConnectionIdFeature,
                                                         IConnectionTransportFeature,
                                                         IConnectionItemsFeature,
                                                         IMemoryPoolFeature,
                                                         IConnectionLifetimeFeature
    {
        private IDictionary<object, object> _items;
        private string _connectionId;
        internal IConnectionProperties ProvidedProperties { get; set; }

        bool IConnectionProperties.TryGet(Type propertyKey, out object property)
        {
            if (propertyKey == typeof(IConnectionIdFeature) ||
                propertyKey == typeof(IConnectionTransportFeature) ||
                propertyKey == typeof(IConnectionItemsFeature) ||
                propertyKey == typeof(IMemoryPoolFeature) ||
                propertyKey == typeof(IConnectionLifetimeFeature) ||
                this is IConnectionInherentKeepAliveFeature)
            {
                property = this;
                return true;
            }

            if (ProvidedProperties != null)
            {
                return ProvidedProperties.TryGet(propertyKey, out property);
            }

            property = default;
            return false;
        }

        public string ConnectionId
        {
            get => _connectionId ??= CorrelationIdGenerator.GetNextId();
            set => _connectionId = value;
        }

        public IDuplexPipe Transport { get; set; }

        public IDictionary<object, object> Items
        {
            get => _items ??= new ConnectionItems();
            set => _items = value;
        }

        public MemoryPool<byte> MemoryPool { get; }

        public void Abort()
        {
            ValueTask t = CloseAsync(ConnectionCloseMethod.Abort, CancellationToken.None);

            if (t.IsCompleted) t.GetAwaiter().GetResult();
            else t.AsTask().GetAwaiter().GetResult();
        }

        public CancellationToken ConnectionClosed { get; set; }
    }
}