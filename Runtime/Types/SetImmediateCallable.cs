using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Node.js setImmediate() function - schedules a callback to run on the next event loop iteration.
/// </summary>
public class SetImmediateCallable(Interpreter interpreter) : ISharpTSCallable
{
    public int Arity() => 1; // At least callback, optional args

    public object? Call(Interpreter _, List<object?> arguments)
    {
        if (arguments.Count < 1 || arguments[0] is not ISharpTSCallable callback)
        {
            throw new Exception("Runtime Error: setImmediate() requires a callback function.");
        }

        // Collect additional arguments
        var args = arguments.Count > 1 ? arguments.Skip(1).ToList() : [];

        // setImmediate is like setTimeout(callback, 0) - schedules for next tick
        return TimerBuiltIns.SetTimeout(interpreter, callback, 0, args);
    }

    public override string ToString() => "<function setImmediate>";
}

/// <summary>
/// Node.js clearImmediate() function - cancels a setImmediate callback.
/// </summary>
public class ClearImmediateCallable : ISharpTSCallable
{
    public int Arity() => 1;

    public object? Call(Interpreter _, List<object?> arguments)
    {
        // clearImmediate is just clearTimeout
        if (arguments.Count > 0)
        {
            TimerBuiltIns.ClearTimeout(arguments[0]);
        }
        return null;
    }

    public override string ToString() => "<function clearImmediate>";
}
