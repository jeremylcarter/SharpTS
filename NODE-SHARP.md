# SharpTS.Node - HTTP Module Implementation Guide

A standalone .NET class library providing Node.js-compatible HTTP server functionality for SharpTS.

**Created:** 2026-01-31

---

## 1. Purpose & Vision

### Goal

Create a minimal, Express-like HTTP server library in pure C# that:

1. Works standalone as a .NET library
2. Can be wrapped by SharpTS's interpreter as the `http` module
3. Follows Node.js/Express conventions for familiarity

### Target Usage (Pure C#)

```csharp
using SharpTS.Node.Http;

var server = Http.CreateServer((req, res) =>
{
    res.StatusCode = 200;
    res.SetHeader("Content-Type", "text/plain");
    res.End("Hello world\n");
});

server.Listen(3000, () =>
{
    Console.WriteLine("Server running at http://localhost:3000/");
});
```

### Target Usage (SharpTS TypeScript)

```typescript
import http from "http";

const server = http.createServer((req, res) => {
  res.statusCode = 200;
  res.setHeader("Content-Type", "text/plain");
  res.end("Hello world\n");
});

server.listen(3000, () => {
  console.log("Server running at http://localhost:3000/");
});
```

---

## 2. Reference Implementations

The `ReferenceExamples/` directory contains four reference packages (git-ignored).

### 2.0 Recommendation Summary

| Package            | Use For                                 | Don't Use For                         |
| ------------------ | --------------------------------------- | ------------------------------------- |
| **SharpEventLoop** | Event loop core, SynchronizationContext | -                                     |
| **ExpressSharp**   | API design, req/res wrappers            | Threading model (thread-per-request)  |
| **Wired.IO**       | Multi-accept pattern, utilities         | Foundation (too complex, wrong model) |
| **eLoop**          | Advanced scheduler scenarios            | Initial implementation                |

**Primary Foundation:** SharpEventLoop + ExpressSharp patterns + HttpListener
**Borrow Selectively:** Wired.IO utilities (MimeTypes, string caching)

### 2.1 SharpEventLoop (Recommended Foundation)

**Location:** `ReferenceExamples/SharpEventLoop/`

**Key Insight:** Clean event loop using `BlockingCollection<Action>` with custom `SynchronizationContext`.

```csharp
// Core pattern from EventLoopInternal.cs
internal sealed class EventLoopInternal : IDisposable
{
    private readonly BlockingCollection<Action> _actions;

    public void Enter()
    {
        var currentContext = new EventLoopSynchronizationContext(Enqueue);
        SynchronizationContext.SetSynchronizationContext(currentContext);

        foreach (var action in _actions.GetConsumingEnumerable())
        {
            action();  // Execute on this thread
        }
    }

    public bool Run(Func<Task> worker)
    {
        return Enqueue(async () => await worker());
    }
}
```

**Why This Matters:**

- `SynchronizationContext` ensures `await` continuations run on the event loop thread
- `BlockingCollection` provides thread-safe queue with blocking consumption
- Clean shutdown via `Dispose()`

**Use For:** Event loop core, async context management

### 2.2 ExpressSharp (API Reference)

**Location:** `ReferenceExamples/ExpressSharp/`

**Key Insight:** Express-like API built on `HttpListener`.

```csharp
// Pattern from Express.cs
public class Express
{
    private readonly ExpressConfiguration _config;

    public Express()
    {
        _config.server = new HttpListener();
        _config.bindings = new Dictionary<string, Action<Request, Response>>();
    }

    public void GET(string path, Action<Request, Response> callback)
        => _config.Bind($"GET {path}", callback);

    public void Listen(ushort port)
    {
        _config.SetPort(port);
        while (_config.listening)
        {
            var context = _config.server.GetContext();
            // BAD: Thread-per-request
            new Thread(() => AcceptRequest(context)).Start();
        }
    }
}
```

**What to Keep:**

- `HttpListener` as the underlying server
- Route binding pattern (`"GET /path"` → callback)
- `Request`/`Response` wrapper classes

**What to Fix:**

- Replace `new Thread()` with event loop queuing
- Add proper async support

**Use For:** API design, request/response wrapping

### 2.3 eLoop (Scheduler Reference)

**Location:** `ReferenceExamples/eLoop/`

**Key Insight:** Netty-style scheduler abstractions.

```csharp
// From Scheduler.cs
public static class Scheduler
{
    public static ITaskScheduler Inline { get; }
    public static ITaskScheduler ThreadPool { get; }

    public static ITaskScheduler CreateEventScheduler();
    public static ITaskScheduler CreateThreadScheduler(bool syncContext);
    public static ITaskScheduler CreateQueueScheduler(bool syncContext);
}
```

**Use For:** Scheduler abstraction if needed for advanced scenarios

### 2.4 Wired.IO (Performance Reference - Borrow Selectively)

**Location:** `ReferenceExamples/Wired.IO/`

**Architecture Overview:**

```
┌─────────────────────────────────────────────────────────────────┐
│ Wired.IO Architecture                                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Raw Socket (not HttpListener)                            │    │
│  │  - Socket.AcceptAsync()                                 │    │
│  │  - Dual-stack IPv6/IPv4                                 │    │
│  │  - TCP_NODELAY for low latency                          │    │
│  └─────────────────────────────────────────────────────────┘    │
│                         ↓                                        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Multiple Accept Loops (4 concurrent)                     │    │
│  │  var acceptTasks = new Task[4];                         │    │
│  │  for (var i = 0; i < 4; i++)                            │    │
│  │      acceptTasks[i] = AcceptLoopAsync(...);             │    │
│  └─────────────────────────────────────────────────────────┘    │
│                         ↓                                        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ System.IO.Pipelines (Zero-copy HTTP parsing)             │    │
│  │  - PipeReader for request parsing                       │    │
│  │  - PipeWriter for response writing                      │    │
│  │  - Single-segment fast path + multi-segment fallback   │    │
│  └─────────────────────────────────────────────────────────┘    │
│                         ↓                                        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Object Pooling                                           │    │
│  │  - ObjectPool<TContext> for context reuse               │    │
│  │  - ArrayPool<byte> for body buffers                     │    │
│  │  - String caching for routes, headers, methods          │    │
│  └─────────────────────────────────────────────────────────┘    │
│                         ↓                                        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Async Pipeline Model                                     │    │
│  │  await pipeline(context);  // Not event-loop based      │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

**Why NOT Use as Foundation:**

| Reason                      | Details                                                        |
| --------------------------- | -------------------------------------------------------------- |
| **Wrong API Model**         | Builder/Pipeline pattern, not `createServer((req, res) => {})` |
| **No Event Loop**           | Async pipeline, not single-threaded event loop                 |
| **Too Complex**             | 900+ lines for HTTP handler, 2000+ total                       |
| **Hand-rolled HTTP Parser** | More code to maintain than HttpListener provides               |
| **Overkill for V1**         | Optimized for TechEmpower benchmarks, not simplicity           |

**What to Borrow:**

```csharp
// 1. Multi-accept loop pattern (simplified)
var acceptTasks = new Task[Environment.ProcessorCount];
for (var i = 0; i < acceptTasks.Length; i++)
    acceptTasks[i] = AcceptLoopAsync(stoppingToken);
await Task.WhenAll(acceptTasks);

// 2. Utilities (copy if needed)
// - MimeTypes.cs - content type lookup
// - StringCache - reduce string allocations
// - PoolBufferedStream - if using Pipelines later
```

**Future Consideration:**
When SharpTS.Node needs high performance (v2+), consider:

- Replacing HttpListener with raw Socket + Pipelines
- Implementing multi-accept loops
- Adding object pooling for contexts

---

## 3. Multi-Core Architecture

### 3.1 Threading Model: Hybrid Multi-Accept + Single Callback Loop

SharpTS.Node uses a hybrid approach that combines multi-core I/O with single-threaded callback execution:

```
┌─────────────────────────────────────────────────────────────────┐
│                    Main Event Loop Thread                        │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ User callbacks execute here (single-threaded, SAFE)       │  │
│  │                                                           │  │
│  │ BlockingCollection<Action> queue                          │  │
│  │                                                           │  │
│  │ foreach (var action in queue.GetConsumingEnumerable())    │  │
│  │ {                                                         │  │
│  │     action();  // (req, res) => { res.end("Hello"); }     │  │
│  │ }                                                         │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              ↑ Enqueue callbacks
┌─────────────────────────────────────────────────────────────────┐
│         Multiple Accept Loops (Parallel I/O on ThreadPool)       │
│                                                                  │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐        │
│  │ Accept 1 │  │ Accept 2 │  │ Accept 3 │  │ Accept N │        │
│  │ (Core 0) │  │ (Core 1) │  │ (Core 2) │  │ (Core N) │        │
│  │          │  │          │  │          │  │          │        │
│  │ await    │  │ await    │  │ await    │  │ await    │        │
│  │ Accept() │  │ Accept() │  │ Accept() │  │ Accept() │        │
│  │    ↓     │  │    ↓     │  │    ↓     │  │    ↓     │        │
│  │ Wrap req │  │ Wrap req │  │ Wrap req │  │ Wrap req │        │
│  │ Queue →  │  │ Queue →  │  │ Queue →  │  │ Queue →  │        │
│  └──────────┘  └──────────┘  └──────────┘  └──────────┘        │
│                                                                  │
│  N = Environment.ProcessorCount (or configurable)               │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 Why This Model?

| Benefit                    | Explanation                                       |
| -------------------------- | ------------------------------------------------- |
| **Multi-Core I/O**         | Accept loops run in parallel across all CPU cores |
| **High Throughput**        | Can accept thousands of connections/second        |
| **Single-Threaded Safety** | User callbacks never race - no locks needed       |
| **Node.js Semantics**      | Matches expected JavaScript execution model       |
| **Simple Mental Model**    | Developers don't worry about thread safety        |

### 3.3 Data Flow

```
1. Client connects
        ↓
2. One of N accept loops picks it up (parallel)
        ↓
3. Accept loop wraps request/response objects
        ↓
4. Accept loop enqueues callback to main queue
        ↓
5. Main event loop dequeues and executes callback (serial)
        ↓
6. User code runs: (req, res) => { ... }
        ↓
7. Response written back to client
```

### 3.4 Comparison with Alternatives

| Model                      | Throughput | Safety      | Complexity | Node.js Compatible |
| -------------------------- | ---------- | ----------- | ---------- | ------------------ |
| Single-threaded everything | Low        | ✅ Safe     | Low        | ✅ Yes             |
| Thread-per-request         | Medium     | ❌ Unsafe   | Medium     | ❌ No              |
| **Hybrid (our choice)**    | **High**   | **✅ Safe** | **Medium** | **✅ Yes**         |
| Multi-event-loop           | Highest    | ❌ Unsafe   | High       | ❌ No              |
| Worker processes           | Highest    | ✅ Safe     | High       | ✅ Yes (future)    |

---

## 4. Project Structure

### 3.1 Project Structure

```
SharpTS.Node/
├── SharpTS.Node.csproj
├── Http/
│   ├── Http.cs                    # Static factory (createServer)
│   ├── Server.cs                  # HTTP server with event loop
│   ├── IncomingMessage.cs         # Request wrapper (req)
│   └── ServerResponse.cs          # Response wrapper (res)
├── Events/
│   ├── EventEmitter.cs            # Base event emitter
│   └── EventEmitterExtensions.cs
├── EventLoop/
│   ├── NodeEventLoop.cs           # Main event loop
│   ├── NodeSynchronizationContext.cs
│   └── IEventLoopAware.cs
└── Streams/                       # Future: Readable/Writable
    ├── Readable.cs
    └── Writable.cs
```

### 3.2 Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                              SharpTS.Node                                │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ NodeEventLoop                                                    │    │
│  │  - BlockingCollection<Action> queue                             │    │
│  │  - Custom SynchronizationContext                                │    │
│  │  - Run() / Pump() / Stop()                                      │    │
│  └──────────────────────────┬──────────────────────────────────────┘    │
│                             │                                            │
│              ┌──────────────┴──────────────┐                            │
│              │                             │                            │
│  ┌───────────▼──────────┐      ┌──────────▼───────────┐                 │
│  │ Http.Server          │      │ Timers (Future)      │                 │
│  │  - HttpListener      │      │  - setTimeout        │                 │
│  │  - Accept loop       │      │  - setInterval       │                 │
│  │  - Queue requests    │      │                      │                 │
│  └───────────┬──────────┘      └──────────────────────┘                 │
│              │                                                           │
│  ┌───────────▼──────────────────────────────────────────────────────┐   │
│  │ Request/Response Processing                                       │   │
│  │  - IncomingMessage (req)                                         │   │
│  │  - ServerResponse (res)                                          │   │
│  │  - Callbacks execute on event loop thread                        │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 3.3 Threading Model

```
┌─────────────────────────┐
│    HttpListener         │  Background Task
│    Accept Loop          │  (async I/O)
│                         │
│  await GetContextAsync()│
│         │               │
│         ▼               │
│  Queue to Event Loop    │
└─────────┬───────────────┘
          │
          │ ConcurrentQueue
          ▼
┌─────────────────────────┐
│    Event Loop Thread    │  Main Thread
│                         │
│  while (!stopped)       │
│    Dequeue action       │
│    Execute callback     │  ← Callbacks run here (single-threaded)
│                         │
└─────────────────────────┘
```

**Key Guarantee:** All user callbacks execute on the event loop thread, ensuring:

- No race conditions
- No locking needed in user code
- Same semantics as Node.js

---

## 5. Implementation Plan

### Phase 1: Event Loop Core (Multi-Core Ready)

**Goal:** Create a working event loop with multi-core I/O support.

**Files:**

- `EventLoop/NodeEventLoop.cs`
- `EventLoop/NodeSynchronizationContext.cs`

```csharp
using System.Collections.Concurrent;

/// <summary>
/// Node.js-style event loop with multi-core I/O support.
/// User callbacks execute on the main thread (single-threaded safety).
/// I/O operations run on the ThreadPool (multi-core throughput).
/// </summary>
public class NodeEventLoop : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly NodeSynchronizationContext _syncContext;
    private volatile bool _running;
    private int _pendingOperations;

    [ThreadStatic]
    private static NodeEventLoop? _current;

    /// <summary>
    /// Gets the event loop for the current thread (if running).
    /// </summary>
    public static NodeEventLoop? Current => _current;

    public NodeEventLoop()
    {
        _syncContext = new NodeSynchronizationContext(Enqueue);
    }

    /// <summary>
    /// Runs the event loop, executing the initializer then processing queued callbacks.
    /// Blocks until Stop() is called or all pending operations complete.
    /// </summary>
    public void Run(Action initializer)
    {
        _current = this;
        _running = true;

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_syncContext);

        try
        {
            initializer();
            ProcessQueue();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            _current = null;
        }
    }

    /// <summary>
    /// Enqueues an action to be executed on the main event loop thread.
    /// Thread-safe - can be called from any thread (accept loops, timers, etc).
    /// </summary>
    public void Enqueue(Action action)
    {
        if (!_queue.IsAddingCompleted)
            _queue.Add(action);
    }

    /// <summary>
    /// Tracks a pending async operation (e.g., active server).
    /// Prevents the event loop from exiting prematurely.
    /// </summary>
    public void Ref() => Interlocked.Increment(ref _pendingOperations);

    /// <summary>
    /// Signals completion of an async operation.
    /// When all operations complete and Stop() was called, the loop exits.
    /// </summary>
    public void Unref()
    {
        if (Interlocked.Decrement(ref _pendingOperations) == 0 && !_running)
        {
            _queue.CompleteAdding();
        }
    }

    /// <summary>
    /// Stops the event loop. Pending callbacks will still execute.
    /// </summary>
    public void Stop()
    {
        _running = false;
        if (_pendingOperations == 0)
            _queue.CompleteAdding();
    }

    private void ProcessQueue()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // In Node.js this would emit 'uncaughtException'
                Console.Error.WriteLine($"Uncaught exception in event loop: {ex}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _queue.Dispose();
    }
}
```

**SynchronizationContext for async/await:**

```csharp
/// <summary>
/// Custom SynchronizationContext that posts async continuations to the event loop.
/// Ensures async/await continuations run on the main event loop thread.
/// </summary>
public class NodeSynchronizationContext : SynchronizationContext
{
    private readonly Action<Action> _enqueue;

    public NodeSynchronizationContext(Action<Action> enqueue)
    {
        _enqueue = enqueue;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        // Post continuation to run on event loop thread
        _enqueue(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        // For simplicity, treat Send as Post
        // Full implementation would block until completion
        Post(d, state);
    }

    public override SynchronizationContext CreateCopy() => this;
}
```

### Phase 2: EventEmitter

**Goal:** Create a basic EventEmitter for Server events.

**File:** `Events/EventEmitter.cs`

```csharp
public class EventEmitter
{
    private readonly Dictionary<string, List<Delegate>> _listeners = new();

    public EventEmitter On(string eventName, Action callback)
    {
        if (!_listeners.ContainsKey(eventName))
            _listeners[eventName] = new();
        _listeners[eventName].Add(callback);
        return this;
    }

    public EventEmitter On<T>(string eventName, Action<T> callback) { ... }
    public EventEmitter On<T1, T2>(string eventName, Action<T1, T2> callback) { ... }

    public void Emit(string eventName, params object[] args)
    {
        if (!_listeners.TryGetValue(eventName, out var list)) return;
        foreach (var listener in list.ToList())  // ToList for safe iteration
        {
            listener.DynamicInvoke(args);
        }
    }
}
```

### Phase 3: HTTP Server Core (Multi-Accept)

**Goal:** Create HTTP server with multi-core accept loops.

**Files:**

- `Http/Http.cs`
- `Http/Server.cs`

```csharp
// Http.cs - Static factory (Node.js compatible API)
public static class Http
{
    public static Server CreateServer() => new Server();

    public static Server CreateServer(Action<IncomingMessage, ServerResponse> requestListener)
    {
        var server = new Server();
        server.On("request", requestListener);
        return server;
    }
}

// Server.cs - Multi-core HTTP server
public class Server : EventEmitter
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _acceptTasks = new();
    private NodeEventLoop? _eventLoop;

    /// <summary>
    /// Number of concurrent accept loops. Defaults to processor count.
    /// </summary>
    public int AcceptLoopCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// The port the server is listening on.
    /// </summary>
    public int Port { get; private set; }

    public Server Listen(int port, Action? callback = null)
    {
        return Listen(port, "localhost", callback);
    }

    public Server Listen(int port, string hostname, Action? callback = null)
    {
        Port = port;
        _eventLoop = NodeEventLoop.Current
            ?? throw new InvalidOperationException("Server.Listen must be called within an event loop");

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{hostname}:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();

        // Track this server as a pending operation (keeps event loop alive)
        _eventLoop.Ref();

        // Start multiple accept loops for multi-core throughput
        for (int i = 0; i < AcceptLoopCount; i++)
        {
            _acceptTasks.Add(Task.Run(() => AcceptLoopAsync(_cts.Token)));
        }

        // Fire 'listening' event and callback on event loop
        _eventLoop.Enqueue(() =>
        {
            Emit("listening");
            callback?.Invoke();
        });

        return this;
    }

    /// <summary>
    /// Accept loop - runs on ThreadPool. One per CPU core for max throughput.
    /// Each loop independently accepts connections and queues them to the event loop.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener!.IsListening)
        {
            try
            {
                // Await connection (non-blocking, uses I/O completion port)
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Capture event loop reference (in case it changes)
                var loop = _eventLoop!;

                // Queue callback to main event loop thread (thread-safe)
                loop.Enqueue(() =>
                {
                    try
                    {
                        var req = new IncomingMessage(context.Request);
                        var res = new ServerResponse(context.Response);
                        Emit("request", req, res);
                    }
                    catch (Exception ex)
                    {
                        Emit("error", ex);
                    }
                });
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                // Server stopped, exit gracefully
                break;
            }
            catch (ObjectDisposedException)
            {
                // Listener disposed, exit
                break;
            }
            catch (Exception ex)
            {
                // Log error but keep accepting
                _eventLoop?.Enqueue(() => Emit("error", ex));
                await Task.Delay(10, ct).ConfigureAwait(false); // Brief delay on error
            }
        }
    }

    public void Close(Action? callback = null)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();

        _eventLoop?.Enqueue(() =>
        {
            Emit("close");
            callback?.Invoke();

            // Release our ref on the event loop
            _eventLoop?.Unref();
        });
    }
}
```

**Multi-Accept Architecture:**

```
┌─────────────────────────────────────────────────────────────────┐
│  server.Listen(3000, () => console.log("ready"))                │
│                         ↓                                        │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Start N accept loops (N = Environment.ProcessorCount)     │  │
│  │                                                           │  │
│  │   Task.Run(AcceptLoopAsync)  x N                         │  │
│  └───────────────────────────────────────────────────────────┘  │
│                         ↓                                        │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐               │
│  │ Loop 0  │ │ Loop 1  │ │ Loop 2  │ │ Loop N  │  ThreadPool   │
│  │         │ │         │ │         │ │         │               │
│  │ await   │ │ await   │ │ await   │ │ await   │               │
│  │ Accept  │ │ Accept  │ │ Accept  │ │ Accept  │               │
│  │   ↓     │ │   ↓     │ │   ↓     │ │   ↓     │               │
│  │ Queue   │ │ Queue   │ │ Queue   │ │ Queue   │               │
│  └────┬────┘ └────┬────┘ └────┬────┘ └────┬────┘               │
│       │           │           │           │                     │
│       └───────────┴───────────┴───────────┘                     │
│                         ↓                                        │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Event Loop Thread (callbacks execute here, serially)      │  │
│  │                                                           │  │
│  │ (req, res) => { res.end("Hello"); }                       │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### Phase 4: Request/Response Objects

**Files:**

- `Http/IncomingMessage.cs`
- `Http/ServerResponse.cs`

```csharp
// IncomingMessage.cs
public class IncomingMessage
{
    private readonly HttpListenerRequest _request;

    public IncomingMessage(HttpListenerRequest request) => _request = request;

    public string Method => _request.HttpMethod;
    public string Url => _request.RawUrl ?? "/";
    public string HttpVersion => $"{_request.ProtocolVersion.Major}.{_request.ProtocolVersion.Minor}";

    public IReadOnlyDictionary<string, string> Headers
    {
        get
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in _request.Headers.AllKeys)
                dict[key] = _request.Headers[key] ?? "";
            return dict;
        }
    }
}

// ServerResponse.cs
public class ServerResponse
{
    private readonly HttpListenerResponse _response;
    private bool _headersSent;
    private bool _finished;

    public ServerResponse(HttpListenerResponse response) => _response = response;

    public int StatusCode
    {
        get => _response.StatusCode;
        set => _response.StatusCode = value;
    }

    public ServerResponse SetHeader(string name, string value)
    {
        _response.Headers[name] = value;
        return this;
    }

    public string? GetHeader(string name) => _response.Headers[name];

    public ServerResponse Write(string chunk)
    {
        if (_finished) throw new InvalidOperationException("Response already finished");
        var bytes = Encoding.UTF8.GetBytes(chunk);
        _response.OutputStream.Write(bytes);
        return this;
    }

    public void End(string? data = null)
    {
        if (_finished) return;
        if (data != null) Write(data);
        _response.Close();
        _finished = true;
    }

    public ServerResponse WriteHead(int statusCode, IDictionary<string, string>? headers = null)
    {
        StatusCode = statusCode;
        if (headers != null)
            foreach (var (key, value) in headers)
                SetHeader(key, value);
        return this;
    }
}
```

### Phase 5: Minimal Working Example

**Goal:** This should work:

```csharp
using SharpTS.Node.Http;
using SharpTS.Node.EventLoop;

// Create and run the event loop
using var eventLoop = new NodeEventLoop();

eventLoop.Run(() =>
{
    var server = Http.CreateServer((req, res) =>
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {req.Method} {req.Url}");
        res.StatusCode = 200;
        res.SetHeader("Content-Type", "text/plain");
        res.End("Hello from SharpTS.Node!\n");
    });

    // Optional: Configure accept loop count
    // server.AcceptLoopCount = 8;

    server.Listen(3000, () =>
    {
        Console.WriteLine($"Server running at http://localhost:3000/");
        Console.WriteLine($"Using {server.AcceptLoopCount} accept loops");
    });

    // Handle Ctrl+C
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        server.Close(() => Console.WriteLine("Server closed"));
    };
});
```

**Expected Output:**

```
Server running at http://localhost:3000/
Using 8 accept loops
[14:32:01] GET /
[14:32:01] GET /favicon.ico
[14:32:05] GET /api/users
```

**Benchmark Test:**

```bash
# Install wrk or hey for load testing
wrk -t12 -c400 -d10s http://localhost:3000/

# Expected: High requests/second due to multi-accept loops
# Running 10s test @ http://localhost:3000/
#   12 threads and 400 connections
#   Requests/sec: 50000+  (depending on hardware)
```

---

## 6. Integration with SharpTS

Once `SharpTS.Node` works standalone, create the interpreter wrapper:

### Files in SharpTS

```
Runtime/BuiltIns/Modules/Interpreter/HttpModuleInterpreter.cs
Runtime/Types/SharpTSHttpServer.cs
Runtime/Types/SharpTSIncomingMessage.cs
Runtime/Types/SharpTSServerResponse.cs
```

### Wrapper Pattern

```csharp
// HttpModuleInterpreter.cs
public static class HttpModuleInterpreter
{
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["createServer"] = new BuiltInMethod("createServer", 0, 1, CreateServer),
            ["Server"] = SharpTSHttpServerConstructor.Instance,
        };
    }

    private static object? CreateServer(Interpreter interp, object? recv, List<object?> args)
    {
        var handler = args.Count > 0 ? args[0] as ISharpTSCallable : null;
        return new SharpTSHttpServer(handler, interp);
    }
}
```

---

## 7. Testing Strategy

### Unit Tests

```csharp
[Fact]
public async Task Server_RespondsToRequest()
{
    var tcs = new TaskCompletionSource();

    NodeEventLoop.Run(() =>
    {
        var server = Http.CreateServer((req, res) =>
        {
            res.End("OK");
        });

        server.Listen(0, async () =>  // Port 0 = auto-assign
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync($"http://localhost:{server.Port}/");
            Assert.Equal("OK", response);
            server.Close(() => tcs.SetResult());
        });
    });

    await tcs.Task;
}
```

### Integration Tests (SharpTS)

```typescript
// test-http-server.ts
import http from "http";

const server = http.createServer((req, res) => {
  res.statusCode = 200;
  res.end("Hello");
});

server.listen(3000, () => {
  console.log("PASS: Server started");
  server.close();
});
```

---

## 8. Open Questions & Decisions

| Question                 | Decision                     | Rationale                                       |
| ------------------------ | ---------------------------- | ----------------------------------------------- |
| Underlying HTTP library  | `HttpListener`               | Zero dependencies, sufficient performance       |
| Threading model          | **Hybrid multi-accept**      | Multi-core I/O + single-threaded callbacks      |
| Accept loop count        | `Environment.ProcessorCount` | Configurable per-server                         |
| Async context for timers | Not preserved                | Simplifies implementation, matches stated goals |
| Keep-alive connections   | Initially no                 | Add in v2                                       |
| Request body streaming   | Initially buffer             | Proper streams in v2                            |
| Worker processes         | Future (v2+)                 | Like Node.js cluster module                     |

---

## 9. Getting Started

### Create the Project

```bash
cd SharpTS
dotnet new classlib -n SharpTS.Node -f net10.0
dotnet sln add SharpTS.Node/SharpTS.Node.csproj
```

### Project File

```xml
<!-- SharpTS.Node/SharpTS.Node.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### Implementation Order

**Core Event Loop:**

1. [ ] `EventLoop/NodeEventLoop.cs` - BlockingCollection queue, Ref/Unref tracking
2. [ ] `EventLoop/NodeSynchronizationContext.cs` - Async continuation posting

**Events:** 3. [ ] `Events/EventEmitter.cs` - On/Emit/Off pattern

**HTTP Module:** 4. [ ] `Http/IncomingMessage.cs` - Request wrapper (Method, Url, Headers) 5. [ ] `Http/ServerResponse.cs` - Response wrapper (StatusCode, SetHeader, End) 6. [ ] `Http/Server.cs` - **Multi-accept loops**, Listen/Close, events 7. [ ] `Http/Http.cs` - Static factory (CreateServer)

**Testing:** 8. [ ] Basic unit tests (event loop, server start/stop) 9. [ ] Load tests with `wrk` or `hey` to verify multi-core throughput

**Integration:** 10. [ ] SharpTS HttpModuleInterpreter wrapper 11. [ ] SharpTS runtime type wrappers

### File Structure

```
SharpTS.Node/
├── SharpTS.Node.csproj
├── EventLoop/
│   ├── NodeEventLoop.cs           # Main event loop with multi-core support
│   └── NodeSynchronizationContext.cs
├── Events/
│   └── EventEmitter.cs            # Node.js-style event emitter
├── Http/
│   ├── Http.cs                    # createServer factory
│   ├── Server.cs                  # Multi-accept HTTP server
│   ├── IncomingMessage.cs         # Request wrapper
│   └── ServerResponse.cs          # Response wrapper
└── Tests/
    └── ServerTests.cs             # Basic tests
```

---

## 10. References

- [Node.js http module](https://nodejs.org/api/http.html)
- [Node.js Event Loop](https://nodejs.org/en/docs/guides/event-loop-timers-and-nexttick)
- [.NET HttpListener](https://docs.microsoft.com/en-us/dotnet/api/system.net.httplistener)
- [.NET SynchronizationContext](https://docs.microsoft.com/en-us/dotnet/api/system.threading.synchronizationcontext)
- `ReferenceExamples/SharpEventLoop/` - Event loop reference
- `ReferenceExamples/ExpressSharp/` - API reference
- `ReferenceExamples/Wired.IO/` - Performance patterns (borrow selectively)
- `ReferenceExamples/eLoop/` - Scheduler abstractions
