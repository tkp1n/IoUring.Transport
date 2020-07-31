using System;
using System.Net.Connections;
using IoUring.Transport.Internals;
using IoUring.Transport.Internals.Inbound;
using IoUring.Transport.Internals.Outbound;
using Microsoft.Extensions.DependencyInjection;

namespace IoUring.Transport
{
    public static class ServiceCollectionIoUringExtensions
    {
        public static IServiceCollection AddIoUringTransport(this IServiceCollection serviceCollection)
        {
            if (!OsCompatibility.IsCompatible) return serviceCollection;

            serviceCollection.AddSingleton<IoUringTransport>();
            serviceCollection.AddSingleton<ConnectionFactory, IoUringConnectionFactory>();
            serviceCollection.AddSingleton<ConnectionListenerFactory, IoUringConnectionListenerFactory>();

            return serviceCollection;
        }

        public static IServiceCollection AddIoUringTransport(this IServiceCollection serviceCollection, Action<IoUringOptions> options) =>
            !OsCompatibility.IsCompatible ? serviceCollection : serviceCollection.Configure(options).AddIoUringTransport();
    }
}