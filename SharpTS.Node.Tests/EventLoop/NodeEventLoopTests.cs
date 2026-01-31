using SharpTS.Node.EventLoop;
using Xunit;

namespace SharpTS.Node.Tests.EventLoop;

public class NodeEventLoopTests
{
    [Fact]
    public void Run_ExecutesInitializer()
    {
        using var eventLoop = new NodeEventLoop();
        var executed = false;

        eventLoop.Run(() =>
        {
            executed = true;
            eventLoop.Stop();
        });

        Assert.True(executed);
    }

    [Fact]
    public void Current_ReturnsLoopInsideRun()
    {
        using var eventLoop = new NodeEventLoop();
        NodeEventLoop? captured = null;

        eventLoop.Run(() =>
        {
            captured = NodeEventLoop.Current;
            eventLoop.Stop();
        });

        Assert.Same(eventLoop, captured);
    }

    [Fact]
    public void Current_ReturnsNullOutsideRun()
    {
        Assert.Null(NodeEventLoop.Current);
    }

    [Fact]
    public void Enqueue_ExecutesActionOnEventLoop()
    {
        using var eventLoop = new NodeEventLoop();
        var values = new List<int>();

        eventLoop.Run(() =>
        {
            values.Add(1);
            eventLoop.Enqueue(() =>
            {
                values.Add(2);
                eventLoop.Stop();
            });
        });

        Assert.Equal(new[] { 1, 2 }, values);
    }

    [Fact]
    public void Enqueue_FromBackgroundThread_ExecutesOnEventLoop()
    {
        using var eventLoop = new NodeEventLoop();
        var executedOnMainThread = false;
        var mainThreadId = -1;
        var callbackThreadId = -1;

        eventLoop.Run(() =>
        {
            mainThreadId = Environment.CurrentManagedThreadId;

            Task.Run(() =>
            {
                eventLoop.Enqueue(() =>
                {
                    callbackThreadId = Environment.CurrentManagedThreadId;
                    executedOnMainThread = (callbackThreadId == mainThreadId);
                    eventLoop.Stop();
                });
            });
        });

        Assert.True(executedOnMainThread);
    }

    [Fact]
    public void RefUnref_KeepsLoopAlive()
    {
        using var eventLoop = new NodeEventLoop();
        var completed = false;

        eventLoop.Run(() =>
        {
            eventLoop.Ref();

            Task.Run(async () =>
            {
                await Task.Delay(50);
                eventLoop.Enqueue(() =>
                {
                    completed = true;
                    eventLoop.Unref();
                });
            });
        });

        Assert.True(completed);
    }

    [Fact]
    public void Stop_ExitsLoop()
    {
        using var eventLoop = new NodeEventLoop();
        var afterStop = false;

        eventLoop.Run(() =>
        {
            eventLoop.Enqueue(() =>
            {
                eventLoop.Stop();
            });

            eventLoop.Enqueue(() =>
            {
                // This should still execute because Stop just signals
                afterStop = true;
            });
        });

        Assert.True(afterStop);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var eventLoop = new NodeEventLoop();
        eventLoop.Dispose();
        eventLoop.Dispose(); // Should not throw
    }

    [Fact]
    public void Run_ThrowsAfterDispose()
    {
        var eventLoop = new NodeEventLoop();
        eventLoop.Dispose();

        Assert.Throws<ObjectDisposedException>(() => eventLoop.Run(() => { }));
    }
}
