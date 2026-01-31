using System.Collections.Concurrent;

namespace SharpTS.Node.EventLoop;

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
    private bool _disposed;

    [ThreadStatic]
    private static NodeEventLoop? _current;

    /// <summary>
    /// Gets the event loop for the current thread (if running).
    /// </summary>
    public static NodeEventLoop? Current => _current;

    /// <summary>
    /// Creates a new NodeEventLoop instance.
    /// </summary>
    public NodeEventLoop()
    {
        _syncContext = new NodeSynchronizationContext(Enqueue);
    }

    /// <summary>
    /// Runs the event loop, executing the initializer then processing queued callbacks.
    /// Blocks until Stop() is called or all pending operations complete.
    /// </summary>
    /// <param name="initializer">The initialization action to run before processing the queue.</param>
    public void Run(Action initializer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NodeEventLoop));

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
    /// <param name="action">The action to enqueue.</param>
    public void Enqueue(Action action)
    {
        if (!_queue.IsAddingCompleted)
        {
            try
            {
                _queue.Add(action);
            }
            catch (InvalidOperationException)
            {
                // Queue was completed, ignore
            }
        }
    }

    /// <summary>
    /// Tracks a pending async operation (e.g., active server).
    /// Prevents the event loop from exiting prematurely.
    /// Call Unref() when the operation completes.
    /// </summary>
    public void Ref() => Interlocked.Increment(ref _pendingOperations);

    /// <summary>
    /// Signals completion of an async operation.
    /// When all operations complete, the loop exits.
    /// </summary>
    public void Unref()
    {
        if (Interlocked.Decrement(ref _pendingOperations) == 0)
        {
            TryCompleteAdding();
        }
    }

    /// <summary>
    /// Gets the number of pending operations (servers, timers, etc).
    /// </summary>
    public int PendingOperations => _pendingOperations;

    /// <summary>
    /// Gets whether the event loop is currently running.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Stops the event loop. Pending callbacks will still execute.
    /// </summary>
    public void Stop()
    {
        _running = false;
        if (_pendingOperations == 0)
        {
            TryCompleteAdding();
        }
    }

    private void TryCompleteAdding()
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
    }

    private void ProcessQueue()
    {
        try
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
                    OnUncaughtException(ex);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Queue was disposed during enumeration
        }
    }

    /// <summary>
    /// Called when an exception is thrown during callback execution.
    /// Override to customize error handling.
    /// </summary>
    /// <param name="ex">The exception that was thrown.</param>
    protected virtual void OnUncaughtException(Exception ex)
    {
        Console.Error.WriteLine($"Uncaught exception in event loop: {ex}");
    }

    /// <summary>
    /// Disposes the event loop, stopping it and releasing resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _queue.Dispose();
    }
}
