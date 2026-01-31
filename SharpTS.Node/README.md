# SharpTS.Node

A Node.js-compatible HTTP server and event loop library for .NET.

## Features

- **Node.js-style API** - `Http.CreateServer((req, res) => { ... })`
- **Multi-core I/O** - Multiple accept loops for high throughput
- **Single-threaded callbacks** - User code executes safely on one thread
- **Event loop** - Node.js-compatible event loop with async/await support
- **EventEmitter** - Standard event emitter pattern

## Quick Start

```csharp
using SharpTS.Node.Http;
using SharpTS.Node.EventLoop;

using var eventLoop = new NodeEventLoop();

eventLoop.Run(() =>
{
    var server = Http.CreateServer((req, res) =>
    {
        res.StatusCode = 200;
        res.SetHeader("Content-Type", "text/plain");
        res.End("Hello from SharpTS.Node!\n");
    });

    server.Listen(3000, () =>
    {
        Console.WriteLine("Server running at http://localhost:3000/");
    });
});
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Accept Loop 0 ─┐                                               │
│  Accept Loop 1 ─┼─→ Enqueue callbacks ─→ Main Event Loop       │
│  Accept Loop N ─┘                        (single-threaded)      │
└─────────────────────────────────────────────────────────────────┘
```

- **Multi-core I/O**: Accept loops run in parallel across CPU cores
- **Single-threaded callbacks**: User code runs on the event loop thread
- **No race conditions**: Same semantics as Node.js

## License

MIT - See LICENSE file in the repository root.
