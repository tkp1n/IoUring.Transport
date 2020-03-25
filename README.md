# IoUring.Transport

Experimental, managed ASP.NET Core Transport layer based on `io_uring`. This library is inspired by [kestrel-linux-transport](https://github.com/redhat-developer/kestrel-linux-transport/), a similar linux-specific transport layer based  on `epoll`.

This transport layer supports both server ([`IConnectionListenerFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.connections.iconnectionlistenerfactory?view=aspnetcore-3.1)) and client ([`IConnectionFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.connections.iconnectionfactory?view=aspnetcore-3.1)) scenarios. It can be registered with `services.AddIoUringTransport();` in the `ConfigureServices` method.

## Design

### Preparation

A configurable number of `TransportThread`s is started. [Benchmarks](https://github.com/tmds/Tmds.LinuxAsync/issues/39#issuecomment-600915703) have shown that a number of threads beyond the number of physical cores is not profitable. Each thread opens an accept-socket on the server endpoint (IP and port) using the `SO_REUSEPORT` option. This allows all threads to `accept` inbound connections and will let the kernel load balance between the accept-sockets. The threads are also able to `connect` outbound connections.

All threads are provided with the writing end of the same `Channel` to write accepted connections to. This `Channel` will be read from when `ConnectAsync` is invoked on the `IConnectionListener`. The `Channel` is unbounded, and back-pressure to temporarily disable `accept`ing new connections is not yet supported.

The `IConnectionFactory` will delegate the request for handling new outbound connections to a `TransportThead` in a round-robin fashion.

Each thread creates an `io_uring` to schedule IO operations and to get notified of their completion.

Each thread also creates an `eventfd` with an initial value of 0 and places a `poll` operation (`IORING_OP_POLL_ADD` / `POLLIN`) on that `eventfd` onto the `io_uring`. This allows us - as we shall later see and use - to unblock the thread using a normal `write` to the `eventfd` if the thread is blocked by an `io_uring_enter` syscall waiting for an IO operation to complete.

### Event loop

Before the event loop is started, we add above-mentioned `poll` on the `eventfd` to the `io_uring`.

The event loop is then made up of the following phases:

1. Handle async operations
2. Submit operations to the kernel
3. Run completions

#### Handle asynchronous operations

At any given moment in time, work items for the event loop can arrive from another (application) thread. To hand over the work item, it is added to a `ConcurrentQueue`, which is processed by the transport thread in the "handle async operations" phase. The different types of work items include:

* `accept` - the application requested to listen on the initial or an additional server endpoint. The requesting application thread performs the `socket`, `bind` and `listen` syscalls. The following `accept`s however, are added to the `io_uring` as `IORING_OP_ACCEPT` by the transport thread when completing this type of work item.
* `connect` - the application requested to connect to another server. The requesting application thread performs the `socket` syscall. The following `connect` however, is added to the `io_uring` as `IORING_OP_CONNECT` by the transport thread when completing this type of work item.
* `read` (poll) - a `FlushAsync` to the application completed **asynchronously**, indicating that there was a need for back-pressure. The synchronous case is handled with a fast-path below. The connection can now be read from again, which is why an `IORING_OP_POLL_ADD` for incoming bytes (`POLLIN`) is added to the `io_uring` when completing this type of work item. With kernel versions >= 5.7 this could be skipped, and a `recv` (`IORING_OP_RECV`) be issued directly instead.
* `write` (poll) - a `ReadAsync` from the application completed **asynchronously**. The synchronous case is handled with a fast-path below. The connection can now be written to again, which is why an `IORING_OP_POLL_ADD` for "writability" (`POLLOUT`) is added to the `io_uring` when completing this type of work item. With kernel versions >= 5.7 this could be skipped, and a `send` (`IORING_OP_SEND`) be issued directly instead.
* `close` - for one of many reasons, a connection can be closed. This is currently handled with an inline syscall to `close` when handling this type of work item. With kernel versions >= 5.6 this could be replaced with the addition of an `IORING_OP_CLOSE` to the `io_uring`.

#### Submit operations to the kernel

I/O operations are added to the `io_uring` during the phase "Run completions" of the previous event loop iteration and when handling asynchronous operations (above). This phase will submit all pending operations to the kernel, execute operations that can be process synchronously inline and reap the completion of all operations that have completed since the last loop iteration. All of this is achieved with a single syscall `io_uring_enter`.

If no operations are pending at this stage, the transport thread signals that it is going to block, double-check the async operation queue, and then invoke `io_uring_enter` with `min_complete = 1`. This causes the transport thread to block until an I/O operation completes.
As mentioned above a `write` to the `eventfd` can now unblock the transport thread, as it causes the completion of the `poll` on the `eventfd`. The first operations added to the async operation queue after the transport thread signaled that it is going to block issues such a `write`.

#### Run completions

All I/O operations completed before or during the `io_uring_enter` are handled in this phase. Typically each (successfully) completed operation causes another operation to be prepared for submission in the next iteration of the event loop. Recognized types of completed operations are:

* **eventfd poll completion**: The `poll` for the `eventfd` completed. This indicates that an operation was placed into the asnyc operation queue. The immediate action taken is to prepare a `read` (`IORING_OP_READV`) for the `eventfd`. The next `io_uring_enter` will not block, as this `readv` will complete synchronously.
* **eventfd read completion**: The `readv` from the `eventfd` completed. The immediate action taken is to prepare a `poll` (`IORING_OP_POLL_ADD`) for the `eventfd`. This ensures that the transport thread could again be unblocked if the next `io_uring_enter` blocks.
* **accept completion**: A connection was successfully `accept`ed. A `poll` for incoming data (`POLLIN`) is added for the new connection, and the application is queried for data to be written via a `ReadAsyc`. After registering the new connection with the transport thread, another `accept` (`IORING_OP_ACCEPT`) is prepared to accept additional connections.
* **read poll completion**: The `poll` for available data (`POLLIN`) on a socket completed. A `readv` (`IORING_OP_READV`) is added to the `io_uring` to read the data from the socket.
* **write poll completion**: The `poll` for "writability" (`POLLOUT`) of a socket completed. A `writev` (`IORING_OP_WRITEV`) for the data previously acquired during a `ReadAsync` is added to the `io_uring`.
* **read completion**: The `readv` previously added for the affected socket completed. The `Pipeline` is advanced past the number of bytes read and handed over to the application using `FlushAsync`. If `FlushAsync` completes synchronously, a `poll` (`IORING_OP_POLL_ADD`) for incoming bytes (`POLLIN`) is added to the `io_uring` directly. In the asynchronous case, a callback is scheduled that will register the connection with the read-poll-queue and unblock the transport thread if necessary by writing to the `evetfd`.
* **write completion**: The `writev` previously added for the affected socket completed. The `Pipeline` is advanced past the number of bytes written, and more data from the application is read using `ReadAsync`. If `ReadAsync` completes synchronously, a `poll` for "writability" (`POLLOUT`) is added to the `io_uring` directly. In the asynchronous case, a callback is scheduled that will register the connection with the write-poll-queue and unblock the transport thread if necessary by writing to the `evetfd`.
* **connect completion**: The `connect` of an outbound connection completed. A `poll` for incoming data (`POLLIN`) is added for the new connection, and the application is queried for data to be written via a `ReadAsyc`.

### Use of `sqe->user_data` to keep context without allocations

Once an IO operation handed over to `io_uring` completes, the application needs to restore some contextual information regarding the operation that completed. This includes:

1. The type of operation that completed (listed in **bold** above).
2. The socket (and associated data) the operation was performed on

`io_uring` allows for 64-bit of user data to be provided with each submission that will be routed through to the completion of the request. The lower 32-bits of this value are set to the socket file descriptor, the operation is performed on, and the high 32-bits are set to an operation indicator. This ensures context can be restored after the completion of an asynchronous operation.

The socket file descriptor is used as an index into a `Dictionary` to fetch the data associated with the socket.

## Open issues in the implementation

* Polishing in general. This is currently a **very** minimal PoC.
* Testing with more than a simple demo app...
* Handle cases with more connections than slots in the `io_uring`s.
* Benchmark and optimize
* Enable CPU affinity
* Use multi-`iovec` `readv`s if more than `_memoryPool.MaxBufferSize` bytes are readable and ensure that the syscall to `ioctl(_fd, FIONREAD, &readableBytes)` is avoided in the typical cases where one `iovec` is enough.

## Room for improvement regarding the use of `io_uring`

* Profit form `IORING_FEAT_SUBMIT_STABLE`. Currently, the `iovec`s are allocated and fixed per connection to ensure they don't "move" during the execution of an operation.
