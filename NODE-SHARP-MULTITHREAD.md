# SharpTS Multi-Threading Architecture for Event Loop & HTTP

This document captures the analysis, design decisions, and implementation plan for adding proper event loop support to SharpTS's timers and HTTP server.

**Date:** 2026-01-31  
**Status:** Design Phase  
**Decision:** Single-threaded event loop first (Node.js compatible), using SharpEventLoop pattern

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Decision: Single-Threaded First](#decision-single-threaded-first)
3. [Recommended Pattern: SharpEventLoop](#recommended-pattern-sharpeventloop)
4. [Reference Examples Comparison](#reference-examples-comparison)
5. [Detailed Comparison: SharpEventLoop vs eLoop](#detailed-comparison-sharpeventloop-vs-eloop)
6. [Current State Analysis](#current-state-analysis)
7. [The Core Problems](#the-core-problems)
8. [Threading Layers](#threading-layers)
9. [The Interpreter Constraint](#the-interpreter-constraint)
10. [Key Insight: Shared State is Rare](#key-insight-shared-state-is-rare)
11. [Proposed Solution: Auto-Lock Captured Variables](#proposed-solution-auto-lock-captured-variables)
12. [Implementation Options](#implementation-options)
13. [SynchronizationContext Design](#synchronizationcontext-design)
14. [Efficient Waiting (No Polling)](#efficient-waiting-no-polling)
15. [Architecture Comparison](#architecture-comparison)
16. [Migration Path](#migration-path)
17. [Reference Patterns](#reference-patterns)
18. [Future: Partitioned Event Loops](#future-partitioned-event-loops)
19. [Open Questions](#open-questions)

---

## Executive Summary

SharpTS needs an efficient event loop for long-running HTTP servers (running for weeks). The current implementation has two problems:

1. **Inefficient polling** - `Thread.Sleep(10)` wastes CPU cycles
2. **Missing async context** - `await` continuations can run on wrong thread

### Decision

**Single-threaded event loop first, matching Node.js semantics.** Multi-threading can be added as an optimization later when the foundation is mature.

### Recommended Pattern

**Use SharpEventLoop** (MPL 2.0 licensed) as the model - just ~120 lines of code that solves both problems:

- `BlockingCollection` + `GetConsumingEnumerable()` for efficient waiting
- `SynchronizationContext` for proper async continuation routing

---

## Decision: Single-Threaded First

After analyzing multi-threading approaches, we decided to **prioritize Node.js compatibility over parallel performance**.

### Rationale

1. **Hide complexity from users** - SharpTS should work like Node.js, no threading surprises
2. **Singleton services are safe by default** - No race conditions for NestJS-style patterns
3. **Simpler implementation** - No locking infrastructure needed
4. **Correctness before speed** - Easy to validate behavior matches Node.js
5. **Future-proof** - Multi-threading can be added as opt-in optimization later

### What This Means

| Aspect             | Behavior                                       |
| ------------------ | ---------------------------------------------- |
| I/O operations     | Multi-threaded (HttpListener, async file I/O)  |
| User callbacks     | Single-threaded (all run on event loop thread) |
| Shared state       | Safe by default (no race conditions)           |
| Singleton services | Work exactly like Node.js                      |
| Performance        | Good for I/O-bound workloads (most servers)    |

### Deferred for Future

- Thread-safe RuntimeEnvironment
- Per-variable locking analysis
- Parallel callback execution
- Multi-threading as opt-in mode

---

## Recommended Pattern: SharpEventLoop

After comparing all reference examples, **SharpEventLoop** is the recommended pattern.

### Source

```
Location: ReferenceExamples/SharpEventLoop/
License: Mozilla Public License 2.0
Size: ~120 lines (just 2 classes!)
```

### Why SharpEventLoop

| Criteria                   | SharpEventLoop | eLoop              | EventLoopSchedulerSlim |
| -------------------------- | -------------- | ------------------ | ---------------------- |
| Solves polling problem     | ✅ Yes         | ✅ Yes             | ❌ No                  |
| Has SynchronizationContext | ✅ Yes         | ⚠️ Optional        | ❌ No                  |
| Tracks active tasks        | ✅ Yes         | ✅ Yes             | ❌ No                  |
| Simple to understand       | ✅ ~120 lines  | ❌ 2000+ lines     | ✅ ~100 lines          |
| Node.js-style API          | ✅ Exactly     | ⚠️ Different       | ❌ Different           |
| Designed for our use case  | ✅ Yes         | ⚠️ Over-engineered | ❌ No async context    |

### Core Pattern

```csharp
// From SharpEventLoop - the key elements:

internal sealed class EventLoopInternal : IDisposable
{
    // Efficient queue with blocking wait
    private readonly BlockingCollection<Action> _actions;

    // Track active async operations (like Ref/Unref)
    private int _numberOfConcurrentTasks;

    public void Enter()
    {
        // Set up async context - THIS IS THE KEY
        var currentContext = new EventLoopSynchronizationContext(Enqueue);
        SynchronizationContext.SetSynchronizationContext(currentContext);

        // Efficient loop - blocks until work available
        foreach (var action in _actions.GetConsumingEnumerable())
        {
            action();  // Execute on this thread
        }
    }

    private bool Enqueue(Action action)
    {
        _actions.Add(action);  // Thread-safe enqueue
        return true;
    }
}

// SynchronizationContext routes async continuations back to the loop
internal sealed class EventLoopSynchronizationContext : SynchronizationContext
{
    private readonly Func<Action, bool> _enqueue;

    public override void Post(SendOrPostCallback callback, object state)
    {
        _enqueue(() => callback(state));  // Route to event loop
    }
}
```

### License: MPL 2.0

Mozilla Public License 2.0 allows us to:

- ✅ Use the code
- ✅ Modify the code
- ✅ Include in our project
- ⚠️ Must keep MPL license for modified files (file-level copyleft)

**Options:**

1. **Copy and modify** - Keep MPL header on the copied files
2. **Reimplement pattern** - Use as inspiration, write our own (no license requirement)

Given it's only ~120 lines and we'll integrate with our timer system, **reimplementing the pattern** is probably cleaner.

### Integration with SharpTS

We'll adapt SharpEventLoop's pattern to work with our existing infrastructure:

```csharp
// In Interpreter.cs - adapted from SharpEventLoop pattern

private readonly BlockingCollection<Action> _callbackQueue = new();
private InterpreterSynchronizationContext? _syncContext;

public void RunEventLoop()
{
    _syncContext = new InterpreterSynchronizationContext(EnqueueCallback);
    var previous = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(_syncContext);

    try
    {
        while (HasActiveHandles && !_isDisposed)
        {
            // Wait for callback OR timer, whichever first
            var timeout = GetNextTimerTimeout();

            if (_callbackQueue.TryTake(out var action, timeout))
            {
                action();
            }

            ProcessDueTimers();
        }
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(previous);
    }
}

private void EnqueueCallback(Action action)
{
    if (!_callbackQueue.IsAddingCompleted)
        _callbackQueue.Add(action);
}
```

**This gives us:**

- ✅ Efficient waiting (no polling)
- ✅ Proper async context (await works correctly)
- ✅ Single-threaded semantics (Node.js compatible)
- ✅ Timer support (integrated with our existing system)
- ✅ Minimal code change to Interpreter

---

## Reference Examples Comparison

### 1. SharpEventLoop ⭐ RECOMMENDED

```
Location: ReferenceExamples/SharpEventLoop/
License: MPL 2.0
```

**Description:** Node.js-inspired event loop for .NET. Exactly what we need.

**Key Files:**

- `EventLoopInternal.cs` - Core loop with BlockingCollection
- `EventLoopSynchronizationContext.cs` - Routes async continuations

**Pros:**

- Simple (~120 lines total)
- Built-in SynchronizationContext
- Active task tracking (`_numberOfConcurrentTasks`)
- Efficient waiting via `GetConsumingEnumerable()`

**Cons:**

- No built-in timer support (we'll add this)
- Older codebase (.NET 4.5 era, but patterns still valid)

### 2. eLoop (Netty-style)

```
Location: ReferenceExamples/eLoop/
License: (check project)
```

**Description:** Netty-inspired scheduler system with multiple scheduler types.

**Key Files:**

- `SingleSyncQueueScheduler.cs` - Single thread + SynchronizationContext
- `ThreadPoolScheduler.cs` - Routes to ThreadPool
- `ASingleScheduler.cs` - Base with Ref/Unref

**Pros:**

- Very flexible
- Ref/Unref implemented
- High-performance patterns from Netty

**Cons:**

- Over-engineered for our needs (56 files, 2000+ lines)
- More complex than necessary
- Uses ThreadPool-based execution (not dedicated blocking thread)

---

## Detailed Comparison: SharpEventLoop vs eLoop

Since both have SynchronizationContext support, here's a deeper analysis.

### Code Size & Complexity

| Metric         | SharpEventLoop | eLoop       |
| -------------- | -------------- | ----------- |
| Total files    | 3              | 56          |
| Lines of code  | ~120           | ~2000+      |
| Dependencies   | None           | None        |
| Learning curve | 5 minutes      | 30+ minutes |

### Core Pattern Comparison

**SharpEventLoop** - Simple, focused:

```csharp
// The entire loop in ~30 lines:
private readonly BlockingCollection<Action> _actions;

public void Enter()
{
    SynchronizationContext.SetSynchronizationContext(
        new EventLoopSynchronizationContext(Enqueue));

    foreach (var action in _actions.GetConsumingEnumerable())
    {
        action();  // Execute on this thread
    }
}
```

**eLoop** - Flexible, complex:

```csharp
// Spread across: ASingleScheduler → SingleUnsyncQueueScheduler
//                → SingleSyncQueueScheduler + ThreadSyncContext

private ConcurrentQueue<Work<object>> requests;

protected void DeliverWorks()
{
    while (TryGet(out var item))
    {
        switch (item.callback1)
        {
            case Action<object> callback0: callback0(item.state); break;
            case WaitCallback callback1: callback1(item.state); break;
            case SendOrPostCallback callback2: callback2(item.state); break;
            case IRunnable runnable: runnable.Execute(item.state); break;
            // ... more cases
        }
    }
}
```

### Feature Comparison

| Feature                    | SharpEventLoop                                   | eLoop                                    |
| -------------------------- | ------------------------------------------------ | ---------------------------------------- |
| **Single event loop**      | ✅ Yes                                           | ✅ Yes                                   |
| **SynchronizationContext** | ✅ Built-in                                      | ✅ Optional (`SingleSyncQueueScheduler`) |
| **Efficient waiting**      | ✅ `BlockingCollection.GetConsumingEnumerable()` | ⚠️ `ConcurrentQueue` + ThreadPool        |
| **Active task tracking**   | ✅ `_numberOfConcurrentTasks`                    | ✅ `Ref()`/`Unref()`                     |
| **Partitioned loops**      | ❌ No                                            | ✅ Yes (`ISchedulerAllotter`)            |
| **Timer scheduling**       | ❌ No (needs adding)                             | ⚠️ Via Netty classes                     |

### Waiting Mechanism (Critical Difference)

**SharpEventLoop** - Truly blocks, zero CPU when idle:

```csharp
foreach (var action in _actions.GetConsumingEnumerable())
    action();
```

**eLoop** - Uses ThreadPool, no dedicated thread:

```csharp
protected override void ExecuteLoop()
{
    if (Interlocked.CompareExchange(ref _doingWork, 1, 0) == 0)
    {
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }
}
```

### For SharpTS Requirements

| Requirement             | SharpEventLoop     | eLoop                       |
| ----------------------- | ------------------ | --------------------------- |
| **Fix polling problem** | ✅ Perfect         | ⚠️ Uses ThreadPool model    |
| **Fix async context**   | ✅ Built-in        | ✅ Has `ThreadSyncContext`  |
| **Node.js semantics**   | ✅ Matches exactly | ⚠️ Different model          |
| **Integration effort**  | ~50 lines          | ~200+ lines to extract      |
| **Timer integration**   | Easy to add        | Complex (Netty scheduler)   |
| **Future partitioning** | Need to build      | ✅ Has `ISchedulerAllotter` |

### Recommendation

**Phase 1 (Now): SharpEventLoop**

- Simpler integration
- Exactly matches Node.js-compatible goal
- Truly efficient (blocking wait, no ThreadPool polling)
- Can be implemented in a day

**Future (Multi-threaded mode): Borrow from eLoop**

- `ISchedulerAllotter` pattern for partitioned loops
- `Ref()`/`Unref()` is similar to what we already have
- Can add when needed without rewriting Phase 1

### Hybrid Architecture (Future)

We can start with SharpEventLoop and add eLoop-style partitioning later:

```csharp
// Future architecture for multi-threaded mode:
interface IEventLoopGroup
{
    IEventLoop Next();  // Round-robin selection
}

class SingleEventLoopGroup : IEventLoopGroup
{
    private readonly EventLoop _loop;  // SharpEventLoop-style
    public IEventLoop Next() => _loop; // Always same loop (current behavior)
}

class PartitionedEventLoopGroup : IEventLoopGroup
{
    private readonly EventLoop[] _loops;  // N loops (one per core)
    private int _index;
    public IEventLoop Next() =>
        _loops[Interlocked.Increment(ref _index) % _loops.Length];
}
```

This gives us:

- SharpEventLoop's simplicity now
- Clear upgrade path to eLoop-style partitioning later
- Same API surface for both modes

---

### 3. EventLoopSchedulerSlim (External - Rx-based)

```
Source: https://github.com/GeorgeTsiokos/corlib
```

**Description:** Lightweight scheduler using Rx patterns.

**Key Pattern:**

```csharp
readonly ConcurrentQueue<Action> _queue;
readonly Gate _gate;  // Ensures one Loop() at a time

if (_gate.TryOpen())
    _scheduler.Schedule(Loop);
```

**Pros:**

- Very lightweight
- Doesn't monopolize a thread

**Cons:**

- No SynchronizationContext (doesn't solve async problem)
- No active task tracking

### 4. SharpTS.Node (Already in project)

```
Location: SharpTS.Node/EventLoop/
```

**Description:** Our own Node.js-style event loop implementation.

**Similar to SharpEventLoop** - we could also use this, but it's in a separate library.

### 5. ExpressSharp

```
Location: ReferenceExamples/ExpressSharp/
```

**Description:** Express.js-style HTTP framework. Not an event loop - useful for HTTP API patterns only.

### 6. Wired.IO

```
Location: ReferenceExamples/Wired.IO/
```

**Description:** High-performance HTTP server. Useful for HTTP optimization patterns, not event loop.

---

## Current State Analysis

### Timer System (Interpreter Mode)

```
Location: Runtime/BuiltIns/TimerBuiltIns.cs
          Execution/Interpreter.cs (lines 78-241)
```

**Current Flow:**

```
┌─────────────────────────────────────────────────────────────┐
│  setTimeout(callback, 1000)                                 │
│      ↓                                                      │
│  Creates VirtualTimer { FireTimeMs: now+1000, Callback }    │
│      ↓                                                      │
│  Adds to _virtualTimers list (with lock)                    │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│  RunEventLoop()                                             │
│      while (HasActiveHandles && !_isDisposed)               │
│      {                                                      │
│          ProcessPendingCallbacks();  // Check timers        │
│          Thread.Sleep(10);           // ← PROBLEM: Polling  │
│      }                                                      │
└─────────────────────────────────────────────────────────────┘
```

**Key Data Structures:**

```csharp
// In Interpreter.cs
private readonly List<VirtualTimer> _virtualTimers = new();
private readonly object _virtualTimersLock = new();
private int _activeHandles;

internal class VirtualTimer
{
    public long FireTimeMs { get; set; }
    public int IntervalMs { get; }
    public Action Callback { get; }
    public bool IsCancelled { get; set; }
    public bool IsInterval { get; }
}
```

### HTTP Server (Interpreter Mode)

```
Location: Runtime/BuiltIns/Modules/Interpreter/HttpModuleInterpreter.cs
```

**Current Flow:**

```
┌─────────────────────────────────────────────────────────────┐
│  http.createServer(handler).listen(3000)                    │
│      ↓                                                      │
│  HttpListener.Start()                                       │
│  interpreter.Ref()  // Keep event loop alive                │
│      ↓                                                      │
│  AcceptRequestsAsync() runs in background                   │
│      ↓                                                      │
│  For each request:                                          │
│      interpreter.ScheduleTimer(0, 0, () => {                │
│          emit("request", req, res);  // ← Queued to main    │
│      });                                                    │
└─────────────────────────────────────────────────────────────┘
```

### Compiled Mode (Timers)

```
Location: Compilation/RuntimeEmitter.Timer.cs
```

**Current Behavior:**

- Uses `Task.Delay` + `ContinueWith` for timers
- Callbacks run on **ThreadPool threads**
- **No event loop** - process exits when Main() returns
- Documented as intentionally different from Node.js

---

## The Core Problems

### Problem 1: Inefficient Polling

```csharp
public void RunEventLoop()
{
    while (HasActiveHandles && !_isDisposed)
    {
        ProcessPendingCallbacks();
        Thread.Sleep(10);  // 100 wakeups/second even when idle!
    }
}
```

**Impact for long-running servers:**

- 100 unnecessary wakeups per second
- CPU never truly sleeps
- Wastes power over weeks of operation

**Solution:** Use `BlockingCollection.TryTake(timeout)` which blocks efficiently.

### Problem 2: Missing SynchronizationContext

Consider this TypeScript:

```typescript
setTimeout(async () => {
  console.log("A - main thread");
  await someAsyncOperation();
  console.log("B - which thread?"); // ← THE PROBLEM
}, 1000);
```

**What happens currently:**

1. "A" executes on main thread ✓
2. `await` suspends, `someAsyncOperation()` runs
3. When operation completes, .NET needs to resume "B"
4. **Without SynchronizationContext:** .NET resumes on ThreadPool thread ✗
5. **With SynchronizationContext:** .NET calls `Post()`, which queues back to main ✓

**Why ThreadPool is problematic for interpreter:**

- `RuntimeEnvironment` is not thread-safe
- Multiple threads modifying variables = race conditions
- Interpreter internal state corruption

---

## Threading Layers

```
┌─────────────────────────────────────────────────────────────┐
│ Layer 4: CPU-Bound Work                                     │
│ (crypto, parsing, computation)                              │
│ → Should use ThreadPool / parallel loops                    │
├─────────────────────────────────────────────────────────────┤
│ Layer 3: Request/Callback Handling                          │
│ → THE QUESTION: Single thread or parallel?                  │
├─────────────────────────────────────────────────────────────┤
│ Layer 2: I/O Operations                                     │
│ (HttpListener.GetContextAsync, file I/O)                    │
│ → Already multi-threaded (ThreadPool)  ✓                    │
├─────────────────────────────────────────────────────────────┤
│ Layer 1: Accept Loop                                        │
│ → Already async (HttpListener uses IOCP/epoll)  ✓           │
└─────────────────────────────────────────────────────────────┘
```

**Layers 1 & 2 are already multi-threaded.** The question is Layer 3.

---

## The Interpreter Constraint

The interpreter has shared mutable state that is NOT thread-safe:

### RuntimeEnvironment

```csharp
public class RuntimeEnvironment
{
    private readonly Dictionary<string, object?> _values = new();
    private readonly RuntimeEnvironment? _enclosing;

    public object? Get(string name) { /* Not locked */ }
    public void Assign(string name, object? value) { /* Not locked */ }
}
```

If two threads access the same variable:

```
Thread A: Get("counter")  → reads 0
Thread B: Get("counter")  → reads 0
Thread A: Assign("counter", 1)
Thread B: Assign("counter", 1)  ← Lost update! Should be 2
```

### Interpreter Internal State

```csharp
public class Interpreter
{
    private RuntimeEnvironment _environment;        // Current scope
    private readonly List<VirtualTimer> _virtualTimers;  // Timer list
    private readonly Dictionary<string, ModuleInstance> _loadedModules;
    private ParsedModule? _currentModule;
    private ModuleInstance? _currentModuleInstance;
    // ... more internal state
}
```

These are accessed during every expression evaluation.

---

## Key Insight: Shared State is Rare

**Observation:** In practice, callbacks rarely share mutable state.

### Case 1: Request-Local Data (Common - No Sharing)

```typescript
server.on("request", (req, res) => {
  const data = parseRequest(req); // Local
  const result = processData(data); // Local
  res.end(JSON.stringify(result)); // Output only
});
```

These callbacks are completely independent and COULD run in parallel.

### Case 2: Simple Counter (Rare - Small Sharing)

```typescript
let requestCount = 0;

server.on("request", (req, res) => {
  requestCount++; // ← Only this accesses shared state
  // ... rest is local
});
```

Only `requestCount++` needs protection. The rest could parallelize.

### Case 3: Read-Only Config (Safe)

```typescript
const config = { port: 3000, debug: true };

server.on("request", (req, res) => {
  if (config.debug) {
    /* ... */
  } // Read-only, safe
});
```

Multiple threads reading is always safe.

### The Insight

**If we could detect and protect just the shared state accesses, we could parallelize everything else.**

---

## Proposed Solution: Auto-Lock Captured Variables

### Concept

1. **Analyze** each callback function at parse/schedule time
2. **Detect** which variables are captured from outer scopes
3. **Wrap** accesses to those variables with locks
4. **Allow** callbacks to run on ThreadPool

### Example

```typescript
let counter = 0; // Outer scope

setTimeout(() => {
  counter++; // Captured - needs lock
  const x = 1 + 2; // Local - no lock needed
  console.log(x);
}, 1000);
```

Under the hood:

```csharp
// counter++ becomes:
lock (GetLockFor("counter"))
{
    var temp = environment.Get("counter");
    environment.Assign("counter", (double)temp + 1);
}

// const x = 1 + 2 runs without lock
var x = 1.0 + 2.0;
```

### Why This Works

| Scenario                              | Behavior                                    |
| ------------------------------------- | ------------------------------------------- |
| Callback with no captures             | Full ThreadPool parallelism                 |
| Callback reading shared state         | Concurrent reads (if using read-write lock) |
| Callback writing shared state         | Serialized writes                           |
| Multiple callbacks modifying same var | Safe (locked)                               |

**Overhead is paid only where needed.**

---

## Implementation Options

### Option A: Global Lock on RuntimeEnvironment

**Simplest approach** - lock all variable access:

```csharp
public class RuntimeEnvironment
{
    private readonly ReaderWriterLockSlim _lock = new();

    public object? Get(string name)
    {
        _lock.EnterReadLock();
        try
        {
            if (_values.TryGetValue(name, out var value))
                return value;
            return _enclosing?.Get(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Assign(string name, object? value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_values.ContainsKey(name))
                _values[name] = value;
            else if (_enclosing != null)
                _enclosing.Assign(name, value);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
```

**Pros:**

- Simple to implement
- Comprehensive protection
- Uses ReaderWriterLockSlim for read parallelism

**Cons:**

- Overhead on ALL variable access
- Lock acquisition even for purely local operations

### Option B: Per-Variable Locking (Smart)

**Only lock variables that are actually captured:**

```csharp
// During closure analysis, mark captured variables
public class ClosureAnalyzer
{
    public HashSet<string> GetCapturedVariables(Expr.ArrowFunction fn)
    {
        var freeVars = new HashSet<string>();
        var localVars = new HashSet<string>();
        WalkExpression(fn.Body, freeVars, localVars);
        return freeVars;
    }
}

// At callback schedule time
var captured = ClosureAnalyzer.GetCapturedVariables(callback);
foreach (var name in captured)
{
    environment.MarkAsShared(name);
}

// RuntimeEnvironment checks before accessing
public object? Get(string name)
{
    if (_sharedMarkers.Contains(name))
    {
        var lockObj = GetLockFor(name);
        lock (lockObj)
        {
            return _values[name];
        }
    }
    // Fast path - no lock
    return _values.TryGetValue(name, out var v) ? v : _enclosing?.Get(name);
}
```

**Pros:**

- Only pay for what you use
- Fast path for local variables
- Granular locking

**Cons:**

- More complex implementation
- Need to track which variables are shared
- Potential for deadlock if callback accesses A then B while another does B then A

### Option C: ConcurrentDictionary + Scope Isolation

**Use thread-safe collections:**

```csharp
public class RuntimeEnvironment
{
    private readonly ConcurrentDictionary<string, object?> _values = new();

    public object? Get(string name)
    {
        if (_values.TryGetValue(name, out var value))
            return value;
        return _enclosing?.Get(name);
    }

    public void Assign(string name, object? value)
    {
        // ConcurrentDictionary handles thread safety
        if (_values.ContainsKey(name))
            _values[name] = value;
        else
            _enclosing?.Assign(name, value);
    }
}
```

**Pros:**

- Simple change
- Built-in thread safety

**Cons:**

- Doesn't handle compound operations (`counter++`)
- Still need wrapper for read-modify-write

### Option D: Hybrid - Lock per Scope

**Lock at scope level, not variable level:**

```csharp
public class RuntimeEnvironment
{
    private readonly object _scopeLock = new();

    public object? GetWithLock(string name)
    {
        lock (_scopeLock)
        {
            return Get(name);
        }
    }

    public void AssignWithLock(string name, object? value)
    {
        lock (_scopeLock)
        {
            Assign(name, value);
        }
    }
}
```

**Pros:**

- Simple
- Avoids deadlock (one lock per scope chain)
- Atomic compound operations when using same scope

**Cons:**

- Coarser granularity than per-variable

---

## SynchronizationContext Design

Regardless of locking strategy, we need proper async context.

### InterpreterSynchronizationContext

```csharp
public class InterpreterSynchronizationContext : SynchronizationContext
{
    private readonly Interpreter _interpreter;
    private readonly BlockingCollection<Action> _queue;

    public InterpreterSynchronizationContext(Interpreter interpreter,
                                              BlockingCollection<Action> queue)
    {
        _interpreter = interpreter;
        _queue = queue;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        // Route async continuations back to the event loop
        _queue.Add(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        // Synchronous - run inline if on correct thread, else queue and wait
        if (SynchronizationContext.Current == this)
        {
            d(state);
        }
        else
        {
            using var done = new ManualResetEventSlim();
            _queue.Add(() => { d(state); done.Set(); });
            done.Wait();
        }
    }

    public override SynchronizationContext CreateCopy() => this;
}
```

### Usage in RunEventLoop

```csharp
public void RunEventLoop()
{
    var queue = new BlockingCollection<Action>();
    var syncContext = new InterpreterSynchronizationContext(this, queue);

    var previous = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(syncContext);

    try
    {
        while (HasActiveHandles && !_isDisposed)
        {
            // Wait for callback OR timer, whichever comes first
            var timeout = GetNextTimerTimeout();

            if (queue.TryTake(out var action, timeout))
            {
                action();  // Execute queued callback
            }

            ProcessDueTimers();  // Check and execute due timers
        }
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(previous);
    }
}
```

---

## Efficient Waiting (No Polling)

### Current: Polling with Thread.Sleep

```csharp
while (HasActiveHandles)
{
    ProcessPendingCallbacks();
    Thread.Sleep(10);  // 100 wakeups/second
}
```

### Proposed: BlockingCollection with Timeout

```csharp
while (HasActiveHandles && !_isDisposed)
{
    // Calculate how long until next timer fires
    var nextTimerMs = GetNextTimerFireTime();
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var waitMs = Math.Max(0, Math.Min(nextTimerMs - now, 60000));

    // Wait for callback OR timeout (whichever first)
    if (_callbackQueue.TryTake(out var action, TimeSpan.FromMilliseconds(waitMs)))
    {
        // A callback was enqueued - execute it
        action();
    }

    // Process any timers that are now due
    ProcessDueTimers();
}
```

**Benefits:**

- Thread truly sleeps when nothing to do
- Wakes immediately when callback enqueued
- Wakes when timer is due
- No busy-waiting

---

## Architecture Comparison

### Interpreted Mode

```
┌─────────────────────────────────────────────────────────────┐
│ Main Event Loop Thread                                      │
│                                                             │
│ SynchronizationContext.Current = InterpreterSyncContext    │
│                                                             │
│ BlockingCollection.TryTake(timeout) ← efficient wait        │
│     ↓                                                       │
│ Execute callback (with locks on captured vars)              │
│     ↓                                                       │
│ Any await → Post() → back to queue → back to this thread   │
└─────────────────────────────────────────────────────────────┘
         ↑ Post()         ↑ Post()         ↑ Post()
         │                │                │
┌────────┴───┐  ┌─────────┴─────┐  ┌───────┴───────┐
│ ThreadPool │  │ HttpListener  │  │ Task.Delay    │
│ (I/O work) │  │ (accepts)     │  │ (timers)      │
└────────────┘  └───────────────┘  └───────────────┘
```

**Characteristics:**

- I/O operations use multi-core (ThreadPool)
- Callbacks route to single thread (via SyncContext)
- Captured variable access is locked
- await continuations come back correctly

### Compiled Mode (Future)

```
┌─────────────────────────────────────────────────────────────┐
│ ThreadPool Threads (multiple)                               │
│                                                             │
│ Each request runs on a pool thread                          │
│ No interpreter state - regular .NET locals                  │
│ User responsible for synchronization if sharing state       │
│                                                             │
│ Optional: Use event loop for Node.js compatibility          │
└─────────────────────────────────────────────────────────────┘
```

**Characteristics:**

- Full ThreadPool parallelism by default
- No interpreter overhead
- User handles thread safety (like any .NET code)
- Optional event loop mode for compatibility

---

## Migration Path

### Overview

Now that we've decided on single-threaded mode using the SharpEventLoop pattern, the migration is simplified:

| Phase  | Goal                                            | Risk   | Effort   |
| ------ | ----------------------------------------------- | ------ | -------- |
| 1      | Add SynchronizationContext + BlockingCollection | Medium | 1-2 days |
| 2      | Integrate timers with new event loop            | Low    | 1 day    |
| 3      | Validate HTTP server                            | Low    | 1 day    |
| Future | Multi-threaded opt-in mode                      | TBD    | TBD      |

### Phase 1: Event Loop Rewrite (SharpEventLoop Pattern)

**Goal:** Replace polling with efficient waiting + add SynchronizationContext

**Changes to `Execution/Interpreter.cs`:**

```csharp
// NEW: Add these fields
private readonly BlockingCollection<Action> _callbackQueue = new();
private InterpreterSynchronizationContext? _syncContext;

// NEW: Add this class (or separate file)
private class InterpreterSynchronizationContext : SynchronizationContext
{
    private readonly Action<Action> _enqueue;

    public InterpreterSynchronizationContext(Action<Action> enqueue)
        => _enqueue = enqueue;

    public override void Post(SendOrPostCallback d, object? state)
        => _enqueue(() => d(state));

    public override SynchronizationContext CreateCopy() => this;
}

// MODIFY: RunEventLoop()
public void RunEventLoop()
{
    _syncContext = new InterpreterSynchronizationContext(EnqueueCallback);
    var previous = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(_syncContext);

    try
    {
        while (HasActiveHandles && !_isDisposed)
        {
            // Calculate timeout until next timer
            var timeout = GetNextTimerTimeout();

            // Wait for callback OR timeout (efficient, no polling!)
            if (_callbackQueue.TryTake(out var action, timeout))
            {
                action();
            }

            // Process any due timers
            ProcessDueTimers();
        }
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(previous);
        _callbackQueue.CompleteAdding();
    }
}

// NEW: Helper to enqueue callbacks
private void EnqueueCallback(Action action)
{
    if (!_isDisposed && !_callbackQueue.IsAddingCompleted)
    {
        try { _callbackQueue.Add(action); }
        catch (InvalidOperationException) { /* completed */ }
    }
}

// NEW: Calculate next timer timeout
private TimeSpan GetNextTimerTimeout()
{
    lock (_virtualTimersLock)
    {
        if (_virtualTimers.Count == 0)
            return TimeSpan.FromSeconds(60); // Max wait

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var next = _virtualTimers.Min(t => t.FireTimeMs);
        var ms = Math.Max(0, next - now);
        return TimeSpan.FromMilliseconds(Math.Min(ms, 60000));
    }
}
```

**Risk:** Medium - changes core event loop  
**Benefit:**

- No more CPU polling (efficient for weeks-long servers)
- Async/await works correctly
- Node.js compatible single-threaded semantics

### Phase 2: Timer Integration

**Goal:** Ensure timers work with new event loop

**Changes:**

- `ScheduleTimer()` should wake the event loop when adding immediate timer
- `ProcessDueTimers()` extracted from `ProcessPendingCallbacks()`
- Keep existing timer API for backward compatibility

```csharp
// MODIFY: ScheduleTimer to wake the loop
internal VirtualTimer ScheduleTimer(int delayMs, int intervalMs, Action callback, bool isInterval)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var timer = new VirtualTimer(now + delayMs, intervalMs, callback, isInterval);

    lock (_virtualTimersLock)
    {
        _virtualTimers.Add(timer);
    }

    // Wake the event loop if timer is soon
    if (delayMs <= 10)
    {
        EnqueueCallback(() => { }); // Dummy action to wake loop
    }

    return timer;
}
```

**Risk:** Low  
**Benefit:** Timers fire promptly without polling delay

### Phase 3: Validate HTTP Server

**Goal:** Verify everything works together

**Tests:**

- [ ] HTTP server starts and accepts requests
- [ ] Async request handlers work correctly
- [ ] `await` in handlers resumes on event loop thread
- [ ] Server runs for extended period without CPU spin
- [ ] Multiple concurrent requests handled correctly
- [ ] `setTimeout`/`setInterval` work alongside HTTP

**Validation Script:**

```bash
# Start server
sharpts examples/http.ts &

# Monitor CPU (should be near 0% when idle)
top -p $!

# Send test requests
curl http://localhost:3000/
curl http://localhost:3000/api/info

# Run for 10 minutes, check CPU stays low
sleep 600
```

### Future: Multi-Threaded Opt-In Mode

**Deferred until single-threaded mode is mature.**

When needed:

- Add thread-safe RuntimeEnvironment (Option A or D from earlier)
- Optionally expose multi-threaded mode via config
- Document threading behavior

---

## Reference Patterns

### SharpEventLoop ⭐ (Primary Reference)

```
Location: ReferenceExamples/SharpEventLoop/
License: MPL 2.0
```

Key patterns we're adopting:

- `BlockingCollection<Action>` for efficient waiting
- `GetConsumingEnumerable()` for the main loop
- `EventLoopSynchronizationContext` for async routing
- `_numberOfConcurrentTasks` for active task tracking (like our Ref/Unref)

### eLoop (Secondary Reference)

```
Location: ReferenceExamples/eLoop/src/eLoop/
```

Useful patterns:

- `ThreadSyncContext` - routes Post() back to scheduler
- `ASingleScheduler` - Ref/Unref counting pattern
- `SingleSyncQueueScheduler` - SynchronizationContext integration

### SharpTS.Node (In Project)

```
Location: SharpTS.Node/EventLoop/
```

Similar to SharpEventLoop, already in our codebase. Could be used as alternative.

### Node.js (Behavior Reference)

- Single-threaded JavaScript execution
- libuv uses thread pool for I/O
- All callbacks on single thread
- No locking needed in user code

**This is the behavior we're matching.**

### .NET Kestrel/ASP.NET Core (Future Reference)

- Full ThreadPool parallelism
- Async/await for I/O
- User handles synchronization
- Very high throughput

**Potential model for future multi-threaded mode.**

---

## Future: Partitioned Event Loops

For future high-performance mode, we can add partitioned event loops (Netty-style).

### The Concept

```
┌─────────────────────────────────────────────────────────────────┐
│                    HTTP Accept Loop                              │
│                         ↓                                        │
│          New connection arrives                                  │
│                         ↓                                        │
│         Allotter.Next() → picks EventLoop N                     │
└─────────────────────────────────────────────────────────────────┘
         ↓                    ↓                    ↓
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│ EventLoop 0 │     │ EventLoop 1 │     │ EventLoop 2 │    ... (N loops)
│ (Thread 0)  │     │ (Thread 1)  │     │ (Thread 2)  │
├─────────────┤     ├─────────────┤     ├─────────────┤
│ Conn A      │     │ Conn B      │     │ Conn C      │
│ Conn D      │     │ Conn E      │     │ Conn F      │
│ Conn G      │     │ ...         │     │ ...         │
└─────────────┘     └─────────────┘     └─────────────┘
```

### Key Properties

- **N event loops** (typically `Environment.ProcessorCount * 2`)
- Each connection is **pinned** to one loop forever
- All callbacks for that connection run on the same thread
- Different connections can process in parallel (on different loops)

### Benefits

| Benefit                             | Explanation                             |
| ----------------------------------- | --------------------------------------- |
| **Thread affinity**                 | Each connection always on same thread   |
| **No locking for connection state** | Only one thread touches each connection |
| **Multi-core utilization**          | N loops = up to N cores busy            |
| **Cache efficiency**                | Connection data stays on same CPU cache |
| **Predictable latency**             | No thread switching within a connection |

### eLoop's Implementation

From `DefaultEventSchedulerAllotter.cs`:

```csharp
public sealed class DefaultEventSchedulerAllotter : ADefaultAllotter
{
    public DefaultEventSchedulerAllotter() : this(Environment.ProcessorCount * 2) { }

    public DefaultEventSchedulerAllotter(int count)
    {
        this.Schedulers = new ITaskScheduler[count];
        for (int i = 0; i < this.Schedulers.Length; i++)
        {
            this.Schedulers[i] = new SingleEventScheduler();
        }
    }
}
```

Usage:

```csharp
var allotter = new DefaultEventSchedulerAllotter();

// When accepting connection:
var connectionLoop = allotter.Next();  // Round-robin picks a loop

// All work for this connection goes to its assigned loop:
connectionLoop.Schedule(() => HandleRequest(req, res));
```

### Single Loop vs Partitioned Comparison

| Aspect                       | Single Loop (Phase 1) | Partitioned Loops (Future)        |
| ---------------------------- | --------------------- | --------------------------------- |
| Threads                      | 1                     | N (cores × 2)                     |
| Request parallelism          | No                    | Yes (different requests)          |
| Same-request safety          | ✅ Single-threaded    | ✅ Single-threaded per connection |
| Shared state across requests | ✅ Safe               | ⚠️ Needs locking                  |
| Max throughput               | Limited by 1 core     | Scales with cores                 |
| Complexity                   | Simple                | Medium                            |

### When to Add This

Add partitioned loops when:

- Single-threaded mode is mature and stable
- Performance profiling shows event loop is the bottleneck
- Use cases need higher throughput than single core provides
- Users explicitly request multi-threaded mode

---

## Open Questions

### ~~Q1: Lock Granularity~~ (Deferred)

~~Should we lock per-variable, per-scope, or global?~~

**Decision:** Deferred. Single-threaded mode doesn't need locking.

### Q1 (Revised): Timer Integration

How should timers interact with the new BlockingCollection-based loop?

**Current thinking:**

- Calculate timeout until next timer fire
- Use `TryTake(timeout)` to wake when timer is due
- Process timers after each callback or timeout

### Q2: Dispose Handling

What happens when interpreter is disposed while event loop is running?

**Current thinking:**

- Set `_isDisposed` flag
- Call `_callbackQueue.CompleteAdding()`
- Loop will exit on next iteration

### Q3: Error Handling in Callbacks

How should exceptions in callbacks be handled?

**Options:**

- Swallow and log (like Node.js uncaughtException)
- Propagate up (current behavior)
- Emit error event (Node.js style)

### ~~Q4: Compiled Mode~~ (Deferred)

~~How should compiled mode handle HTTP?~~

**Decision:** Deferred. Compiled mode keeps current behavior (stub that throws). Can revisit when needed.

### Q4 (Revised): SharpTS.Node Consolidation

Should we consolidate SharpTS.Node's EventLoop with the Interpreter's event loop?

**Options:**

- Keep separate (current plan)
- Merge into shared `SharpTS.Runtime.EventLoop`
- Have Interpreter use SharpTS.Node directly

**Current thinking:** Keep separate for now. Both are simple enough that duplication is acceptable. Can consolidate later if maintenance becomes an issue.

### Q5: Lock Granularity (Deferred - Future Multi-Threading)

When we eventually add multi-threaded mode, should we lock:

- Per-variable? (max parallelism, deadlock risk)
- Per-scope? (simpler, coarser)
- Global? (simplest, least parallelism)

**Recommendation:** Start with per-scope (Option D), measure, then optimize if needed.

### Q2: Deadlock Prevention

If using per-variable locks, how do we prevent:

```
Callback A: lock(x) → lock(y)
Callback B: lock(y) → lock(x)  // Deadlock!
```

**Options:**

- Always acquire locks in consistent order (by variable name)
- Use try-lock with backoff
- Use single lock per scope (avoids issue)

### Q3: Interpreter Internals

Besides RuntimeEnvironment, the Interpreter has:

- `_currentModule`
- `_currentModuleInstance`
- `_loadedModules`

Should these be:

- Thread-local?
- Locked?
- Immutable/Copy-on-write?

**Recommendation:** Thread-local where possible, locked where shared.

### Q4: Compiled Mode Integration

Should compiled mode:

- Ignore this and use full ThreadPool? (simplest)
- Have optional event loop? (compatibility)
- Share the same event loop code? (reuse)

**Recommendation:** For now, compiled mode stays as-is (ThreadPool). Add event loop option later if needed.

---

## Conclusion

The proposed architecture enables:

1. **Multi-core I/O** - Already present via ThreadPool
2. **Efficient waiting** - BlockingCollection instead of polling
3. **Correct async behavior** - SynchronizationContext routes continuations
4. **Safe parallelism** - Auto-locking of captured variables
5. **Minimal overhead** - Only lock where needed

This approach respects the user's insight: shared state access is rare in practice, so we can parallelize most callbacks while protecting the rare shared access with locks.

---

## Appendix: Code Locations

| Component              | Location                                                        |
| ---------------------- | --------------------------------------------------------------- |
| Timer builtins         | `Runtime/BuiltIns/TimerBuiltIns.cs`                             |
| Interpreter event loop | `Execution/Interpreter.cs` (lines 78-241)                       |
| HTTP module            | `Runtime/BuiltIns/Modules/Interpreter/HttpModuleInterpreter.cs` |
| Closure analyzer       | `Compilation/ClosureAnalyzer.cs`                                |
| Compiled timer runtime | `Compilation/RuntimeEmitter.Timer.cs`                           |
| RuntimeEnvironment     | `Runtime/RuntimeEnvironment.cs`                                 |
| eLoop reference        | `ReferenceExamples/eLoop/src/eLoop/`                            |
| SharpTS.Node EventLoop | `SharpTS.Node/EventLoop/`                                       |
