using SharpTS.Execution;

namespace SharpTS.Runtime.Types;

/// <summary>
/// CommonJS require() function implementation.
/// </summary>
/// <remarks>
/// Provides Node.js-compatible require() for loading CommonJS modules.
/// Returns the module's exports (either ESM exports or CJS module.exports).
/// </remarks>
public class CommonJSRequire(Interpreter interpreterInstance, string? currentPath) : ISharpTSCallable
{
    public int Arity() => 1;

    public object? Call(Interpreter _, List<object?> arguments)
    {
        if (arguments.Count < 1 || arguments[0] is not string path)
        {
            throw new Exception("Runtime Error: require() expects a string path argument.");
        }

        return interpreterInstance.RequireModule(path, currentPath);
    }

    public override string ToString() => "<function require>";
}
