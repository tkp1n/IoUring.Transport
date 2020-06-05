# IoUring.Transport

Experimental, managed ASP.NET Core Transport layer based on `io_uring`. This library is inspired by [kestrel-linux-transport](https://github.com/redhat-developer/kestrel-linux-transport/), a similar linux-specific transport layer based  on `epoll`.

This transport layer supports both server ([`IConnectionListenerFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.connections.iconnectionlistenerfactory)) and client ([`IConnectionFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.connections.iconnectionfactory)) scenarios. 

## Setup

Get the latest preview release from [NuGet](https://www.nuget.org/packages/IoUring.Transport) or the last build from master via [MyGet](https://www.myget.org/feed/tkp1n/package/nuget/IoUring.Transport).

In the `ConfigureServices` method of the `Startup.cs` file of every basic ASP.NET application, add the following:


```csharp
using IoUring.Transport;
// ...
public void ConfigureServices(IServiceCollection services)
{
    // ...
    services.AddIoUringTransport();
    // ...
}
```

## Options

Various configuration options are available if needed (the default values are shown):

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // ...
    services.AddIoUringTransport(options =>
    {
        options.ThreadCount = Environment.ProcessorCount / 2;
        options.SetThreadAffinity = false;
        options.ApplicationSchedulingMode = PipeScheduler.ThreadPool;
        options.TcpNoDelay = true;
        options.RingSize = 128;
        options.ListenBacklog = 128;
    });
    // ...
}
```
