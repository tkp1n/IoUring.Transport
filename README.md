# IoUring.Transport

Experimental, managed ASP.NET Core Transport layer based on `io_uring`. This library is inspired by [kestrel-linux-transport](https://github.com/redhat-developer/kestrel-linux-transport/), a similar linux-specific transport layer based  on `epoll`.

This transport layer supports both server ([`IConnectionListenerFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.connections.iconnectionlistenerfactory)) and client ([`IConnectionFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.connections.iconnectionfactory)) scenarios. It can be registered with `services.AddIoUringTransport();` in the `ConfigureServices` method.
