using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'http' module.
/// </summary>
/// <remarks>
/// NOTE: HTTP server compilation is not fully supported yet.
/// The http module currently works in interpreter mode only.
/// This emitter provides basic stubs to allow compilation without errors,
/// but the compiled code will throw at runtime for server functionality.
/// </remarks>
public sealed class HttpModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "http";

    private static readonly string[] _exportedMembers = ["createServer", "METHODS", "STATUS_CODES"];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        if (methodName != "createServer")
            return false;

        var ctx = emitter.Context;
        var il = ctx.IL;

        // HTTP server compilation is not fully supported yet.
        // Emit code that throws a NotSupportedException at runtime.
        // For full HTTP support, use interpreter mode: sharpts http.ts
        il.Emit(OpCodes.Ldstr, "HTTP server is not yet supported in compiled mode. Use interpreter mode: sharpts http.ts");
        var notSupportedCtor = typeof(NotSupportedException).GetConstructor([typeof(string)])!;
        il.Emit(OpCodes.Newobj, notSupportedCtor);
        il.Emit(OpCodes.Throw);

        return true;
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        switch (propertyName)
        {
            case "METHODS":
                // Emit a string array of HTTP methods
                il.Emit(OpCodes.Ldc_I4, 9);
                il.Emit(OpCodes.Newarr, typeof(string));
                string[] methods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE", "CONNECT"];
                for (int i = 0; i < methods.Length; i++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldstr, methods[i]);
                    il.Emit(OpCodes.Stelem_Ref);
                }
                return true;

            case "STATUS_CODES":
                // Emit a dictionary of status codes
                var dictType = typeof(Dictionary<string, string>);
                var ctor = dictType.GetConstructor([])!;
                il.Emit(OpCodes.Newobj, ctor);
                return true;

            case "createServer":
                // Placeholder - actual method call is handled in TryEmitMethodCall
                il.Emit(OpCodes.Ldstr, "[http.createServer]");
                return true;

            default:
                return false;
        }
    }
}
