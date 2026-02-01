# SharpTS Multi-Threading Architecture for Event Loop & HTTP

This document captures the analysis, design decisions, and implementation plan for adding proper multi-threading support to SharpTS's event loop, timers, and HTTP server.

**Date:** 2026-01-31  
**Status:** Design Phase

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Current State Analysis](#current-state-analysis)
3. [The Core Problems](#the-core-problems)
4. [Threading Layers](#threading-layers)
5. [The Interpreter Constraint](#the-interpreter-constraint)
6. [Key Insight: Shared State is Rare](#key-insight-shared-state-is-rare)
7. [Proposed Solution: Auto-Lock Captured Variables](#proposed-solution-auto-lock-captured-variables)
8. [Implementation Options](#implementation-options)
9. [SynchronizationContext Design](#synchronizationcontext-design)
10. [Efficient Waiting (No Polling)](#efficient-waiting-no-polling)
11. [Architecture Comparison](#architecture-comparison)
12. [Migration Path](#migration-path)
13. [Reference Patterns](#reference-patterns)
14. [Open Questions](#open-questions)

---

## Executive Summary

SharpTS needs an efficient, multi-core-capable event loop for long-running HTTP servers (running for weeks). The current implementation has two problems:

1. **Inefficient polling** - `Thread.Sleep(10)` wastes CPU cycles
2. **Missing async context** - `await` continuations can run on wrong thread

The proposed solution:

- **Multi-threaded I/O and callbacks** where safe
- **Automatic locking** for captured variables (rare case)
- **Efficient waiting** via `BlockingCollection` instead of polling
- **Proper SynchronizationContext** for async continuations

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

### Phase 1: Add SynchronizationContext (Minimal Change)

**Goal:** Fix async continuation routing

**Changes:**

- Add `InterpreterSynchronizationContext` class
- Set it in `RunEventLoop()` before processing
- Route `Post()` to `ScheduleTimer(0, ...)`

**Risk:** Low - only affects async continuations  
**Benefit:** Async handlers work correctly

### Phase 2: Replace Polling with Efficient Waiting

**Goal:** Eliminate CPU waste

**Changes:**

- Add `BlockingCollection<Action>` to Interpreter
- Modify `RunEventLoop()` to use `TryTake(timeout)`
- Modify `ScheduleTimer()` to also enqueue to collection (for immediate wakeup)

**Risk:** Medium - changes core event loop  
**Benefit:** CPU-efficient long-running servers

### Phase 3: Thread-Safe RuntimeEnvironment

**Goal:** Enable parallel callback execution

**Changes:**

- Add `ReaderWriterLockSlim` to RuntimeEnvironment
- Wrap Get/Assign with appropriate locks
- Optionally: Per-variable or per-scope locking

**Risk:** Medium - affects all variable access  
**Benefit:** Multi-core utilization

### Phase 4: Closure Analysis for Smart Locking

**Goal:** Minimize locking overhead

**Changes:**

- Extend `ClosureAnalyzer` to identify captured variables at schedule time
- Mark only captured variables as needing locks
- Fast path for purely local callbacks

**Risk:** Medium - needs careful analysis  
**Benefit:** Best of both worlds (parallelism + safety)

### Phase 5: Compiled Mode Event Loop (Future)

**Goal:** HTTP support in compiled mode

**Options:**

- Reference `SharpTS.Runtime.EventLoop` types
- Or emit equivalent IL inline
- Or simple blocking pattern (WaitUntilClosed)

---

## Reference Patterns

### eLoop (Netty-style)

```
Location: ReferenceExamples/eLoop/src/eLoop/
```

Key patterns:

- `ThreadPoolScheduler` - routes to .NET ThreadPool
- `SingleSyncQueueScheduler` - single thread with SynchronizationContext
- `ThreadSyncContext` - routes Post() back to scheduler
- `ASingleScheduler` - base with Ref/Unref counting

### SharpTS.Node (Existing)

```
Location: SharpTS.Node/EventLoop/
```

Key patterns:

- `NodeEventLoop` - BlockingCollection-based loop
- `NodeSynchronizationContext` - routes to Enqueue()
- `Ref()`/`Unref()` for active handle tracking

### Node.js

- Single-threaded JavaScript execution
- libuv uses thread pool for I/O
- All callbacks on single thread
- No locking needed in user code

### .NET Kestrel/ASP.NET Core

- Full ThreadPool parallelism
- Async/await for I/O
- User handles synchronization
- Very high throughput

---

## Open Questions

### Q1: Lock Granularity

Should we lock:

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
