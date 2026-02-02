using SharpTS.Execution;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Runtime.Types;

/// <summary>
/// Wraps a built-in namespace (Number, String, Array, etc.) as a callable value.
/// In JavaScript, these are both constructors and namespaces for static methods.
/// </summary>
public class BuiltInNamespaceWrapper : ISharpTSCallable, ISharpTSPropertyAccessor
{
    private readonly string _name;
    private readonly Dictionary<string, object?> _cachedMethods = [];

    public BuiltInNamespaceWrapper(string name)
    {
        _name = name;
    }

    public int Arity() => 0; // Variadic

    public object? Call(Interpreter interpreter, List<object?> arguments)
    {
        // Act as a constructor: Number(), String(), Boolean(), etc.
        // These coerce values to the primitive type
        var arg = arguments.Count > 0 ? arguments[0] : null;

        return _name switch
        {
            "Number" => arg switch
            {
                null => 0.0,
                double d => d,
                bool b => b ? 1.0 : 0.0,
                string s => double.TryParse(s, out var n) ? n : double.NaN,
                _ => double.NaN
            },
            "String" => arg?.ToString() ?? "",
            "Boolean" => IsTruthy(arg),
            "Array" => arg switch
            {
                double d => new SharpTSArray(Enumerable.Repeat<object?>(null, (int)d).ToList()),
                _ => new SharpTSArray(arguments)
            },
            "Object" => arg switch
            {
                null or SharpTSUndefined => new SharpTSObject(new Dictionary<string, object?>()),
                _ => arg // Objects pass through
            },
            _ => null
        };
    }

    public object? GetProperty(string name)
    {
        // Check cache first
        if (_cachedMethods.TryGetValue(name, out var cached))
            return cached;

        // Get static method from BuiltInRegistry
        var method = BuiltInRegistry.Instance.GetStaticMethod(_name, name);
        if (method != null)
        {
            _cachedMethods[name] = method;
            return method;
        }

        return null;
    }

    public void SetProperty(string name, object? value)
    {
        // Built-in namespaces are not extensible
        throw new Exception($"Cannot add property '{name}' to built-in {_name}");
    }

    public bool HasProperty(string name)
    {
        return GetProperty(name) != null;
    }

    public IEnumerable<string> PropertyNames => _cachedMethods.Keys;

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            null => false,
            false => false,
            0.0 => false,
            "" => false,
            double d when double.IsNaN(d) => false,
            SharpTSUndefined => false,
            _ => true
        };
    }

    public override string ToString() => $"[Function: {_name}]";
}
