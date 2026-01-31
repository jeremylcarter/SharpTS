namespace SharpTS.Node.EventLoop;

/// <summary>
/// Custom SynchronizationContext that posts async continuations to the event loop.
/// Ensures async/await continuations run on the main event loop thread.
/// </summary>
public class NodeSynchronizationContext : SynchronizationContext
{
    private readonly Action<Action> _enqueue;

    /// <summary>
    /// Creates a new NodeSynchronizationContext.
    /// </summary>
    /// <param name="enqueue">Action to enqueue callbacks to the event loop.</param>
    public NodeSynchronizationContext(Action<Action> enqueue)
    {
        _enqueue = enqueue;
    }

    /// <summary>
    /// Posts a callback to be executed asynchronously on the event loop thread.
    /// </summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        _enqueue(() => d(state));
    }

    /// <summary>
    /// Sends a callback to be executed synchronously.
    /// For simplicity, this implementation treats Send as Post.
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state)
    {
        // For simplicity, treat Send as Post
        // A full implementation would block until completion
        Post(d, state);
    }

    /// <summary>
    /// Creates a copy of this SynchronizationContext.
    /// </summary>
    public override SynchronizationContext CreateCopy() => this;
}
