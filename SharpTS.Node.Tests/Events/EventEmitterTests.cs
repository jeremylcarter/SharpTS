using SharpTS.Node.Events;
using Xunit;

namespace SharpTS.Node.Tests.Events;

public class EventEmitterTests
{
    [Fact]
    public void On_RegistersListener()
    {
        var emitter = new EventEmitter();
        var called = false;

        emitter.On("test", () => called = true);
        emitter.Emit("test");

        Assert.True(called);
    }

    [Fact]
    public void On_RegistersListenerWithArgument()
    {
        var emitter = new EventEmitter();
        string? received = null;

        emitter.On<string>("test", (arg) => received = arg);
        emitter.Emit("test", "hello");

        Assert.Equal("hello", received);
    }

    [Fact]
    public void On_RegistersListenerWithTwoArguments()
    {
        var emitter = new EventEmitter();
        (string?, int) received = (null, 0);

        emitter.On<string, int>("test", (a, b) => received = (a, b));
        emitter.Emit("test", "hello", 42);

        Assert.Equal(("hello", 42), received);
    }

    [Fact]
    public void On_AllowsMultipleListeners()
    {
        var emitter = new EventEmitter();
        var count = 0;

        emitter.On("test", () => count++);
        emitter.On("test", () => count++);
        emitter.Emit("test");

        Assert.Equal(2, count);
    }

    [Fact]
    public void Once_ListenerCalledOnce()
    {
        var emitter = new EventEmitter();
        var count = 0;

        emitter.Once("test", () => count++);
        emitter.Emit("test");
        emitter.Emit("test");

        Assert.Equal(1, count);
    }

    [Fact]
    public void Off_RemovesListener()
    {
        var emitter = new EventEmitter();
        var count = 0;
        Action listener = () => count++;

        emitter.On("test", listener);
        emitter.Emit("test");
        emitter.Off("test", listener);
        emitter.Emit("test");

        Assert.Equal(1, count);
    }

    [Fact]
    public void RemoveAllListeners_RemovesAll()
    {
        var emitter = new EventEmitter();
        var count = 0;

        emitter.On("test", () => count++);
        emitter.On("test", () => count++);
        emitter.RemoveAllListeners("test");
        emitter.Emit("test");

        Assert.Equal(0, count);
    }

    [Fact]
    public void RemoveAllListeners_WithoutName_RemovesAllEvents()
    {
        var emitter = new EventEmitter();
        var count = 0;

        emitter.On("test1", () => count++);
        emitter.On("test2", () => count++);
        emitter.RemoveAllListeners();
        emitter.Emit("test1");
        emitter.Emit("test2");

        Assert.Equal(0, count);
    }

    [Fact]
    public void Emit_ReturnsTrueWhenListenersExist()
    {
        var emitter = new EventEmitter();
        emitter.On("test", () => { });

        Assert.True(emitter.Emit("test"));
    }

    [Fact]
    public void Emit_ReturnsFalseWhenNoListeners()
    {
        var emitter = new EventEmitter();

        Assert.False(emitter.Emit("test"));
    }

    [Fact]
    public void ListenerCount_ReturnsCorrectCount()
    {
        var emitter = new EventEmitter();
        emitter.On("test", () => { });
        emitter.On("test", () => { });

        Assert.Equal(2, emitter.ListenerCount("test"));
    }

    [Fact]
    public void EventNames_ReturnsAllEventNames()
    {
        var emitter = new EventEmitter();
        emitter.On("event1", () => { });
        emitter.On("event2", () => { });

        var names = emitter.EventNames();

        Assert.Contains("event1", names);
        Assert.Contains("event2", names);
    }

    [Fact]
    public void Chaining_Works()
    {
        var emitter = new EventEmitter();
        var results = new List<int>();

        emitter
            .On("test", () => results.Add(1))
            .On("test", () => results.Add(2))
            .Once("test", () => results.Add(3));

        emitter.Emit("test");

        Assert.Equal(new[] { 1, 2, 3 }, results);
    }
}
