using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the 'debug' npm package.
/// Provides a minimal implementation for compatibility with packages that use debug for logging.
/// </summary>
/// <remarks>
/// Usage: const debug = require('debug')('namespace');
///        debug('message');
/// 
/// When DEBUG environment variable contains the namespace (or '*'), messages are logged.
/// </remarks>
public static class DebugModuleInterpreter
{
    /// <summary>
    /// Gets the default export - the debug factory function.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        // The default export IS the factory function itself
        // require('debug') returns a function that creates debug loggers
        return new Dictionary<string, object?>
        {
            ["default"] = new DebugFactory(),
            // Also expose as the module itself for CommonJS: module.exports = debug
            ["__esModule"] = false
        };
    }

    /// <summary>
    /// Gets the debug factory as the default export for CommonJS require().
    /// </summary>
    public static object GetDefaultExport() => new DebugFactory();
}

/// <summary>
/// Factory function that creates namespaced debug loggers.
/// Called like: debug('express:application')
/// </summary>
internal class DebugFactory : ISharpTSCallable
{
    private static readonly HashSet<string> _enabledNamespaces = [];
    private static readonly bool _debugAll;

    static DebugFactory()
    {
        // Check DEBUG environment variable
        var debugEnv = Environment.GetEnvironmentVariable("DEBUG") ?? "";
        if (debugEnv == "*")
        {
            _debugAll = true;
        }
        else if (!string.IsNullOrEmpty(debugEnv))
        {
            foreach (var ns in debugEnv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                _enabledNamespaces.Add(ns.Trim());
            }
        }
    }

    public int Arity() => 1;

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        var namespaceName = arguments.Count > 0 ? arguments[0]?.ToString() ?? "" : "";
        return new DebugLogger(namespaceName, IsEnabled(namespaceName));
    }

    private static bool IsEnabled(string namespaceName)
    {
        if (_debugAll) return true;
        if (_enabledNamespaces.Contains(namespaceName)) return true;

        // Check wildcard patterns like 'express:*'
        foreach (var pattern in _enabledNamespaces)
        {
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (namespaceName.StartsWith(prefix)) return true;
            }
        }

        return false;
    }

    public override string ToString() => "<function debug>";
}

/// <summary>
/// A namespaced debug logger created by the debug factory.
/// </summary>
internal class DebugLogger : ISharpTSCallable
{
    private readonly string _namespace;
    private readonly bool _enabled;

    public DebugLogger(string namespaceName, bool enabled)
    {
        _namespace = namespaceName;
        _enabled = enabled;
    }

    public int Arity() => 0; // Variadic

    public object? Call(Interp interpreter, List<object?> arguments)
    {
        if (!_enabled) return null;

        // Format the message with namespace prefix
        var message = string.Join(" ", arguments.Select(FormatArg));
        Console.Error.WriteLine($"  {_namespace} {message}");
        return null;
    }

    private static string FormatArg(object? arg)
    {
        return arg switch
        {
            null => "null",
            string s => s,
            SharpTSObject obj => FormatObject(obj),
            _ => arg.ToString() ?? ""
        };
    }

    private static string FormatObject(SharpTSObject obj)
    {
        var parts = obj.Fields.Select(kv => $"{kv.Key}: {FormatArg(kv.Value)}");
        return $"{{ {string.Join(", ", parts)} }}";
    }

    public override string ToString() => $"<function debug:{_namespace}>";
}
