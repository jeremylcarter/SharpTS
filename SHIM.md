# SharpTS Shim System

This document describes the extensible shim system for SharpTS, allowing modular runtime APIs (like Node.js compatibility) to be distributed as separate packages.

## Motivation

SharpTS currently bundles Node.js-compatible modules (`fs`, `path`, `crypto`, etc.) directly in the core runtime. This approach has limitations:

1. **Versioning**: Node.js APIs evolve across versions (18.x, 20.x, 22.x) - users may need specific compatibility levels
2. **Bloat**: Not all users need Node.js APIs; some may want browser-like or custom environments
3. **Extensibility**: Third parties cannot easily add new runtime modules
4. **Separation of concerns**: Node.js compatibility is an optional runtime environment, not core TypeScript semantics

The shim system addresses these by extracting runtime-specific modules into separate, versioned packages.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                         SharpTS Core                            │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────────────┐  │
│  │    Lexer     │  │    Parser    │  │     TypeChecker       │  │
│  └──────────────┘  └──────────────┘  └───────────────────────┘  │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────────────┐  │
│  │  Interpreter │  │  IL Compiler │  │  Core Built-ins       │  │
│  │              │  │              │  │  (Math, Array, JSON,  │  │
│  │              │  │              │  │   Promise, Map, etc.) │  │
│  └──────────────┘  └──────────────┘  └───────────────────────┘  │
│                            │                                     │
│                    ┌───────┴───────┐                            │
│                    │ Shim Registry │                            │
│                    └───────┬───────┘                            │
└────────────────────────────┼────────────────────────────────────┘
                             │
         ┌───────────────────┼───────────────────┐
         │                   │                   │
         ▼                   ▼                   ▼
┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
│ SharpTS.Shim    │ │ SharpTS.Shim    │ │ Custom Shim     │
│ .Node@22.0.0    │ │ .Node@20.0.0    │ │ (user-created)  │
│                 │ │                 │ │                 │
│ fs, path, http, │ │ fs, path, http, │ │ custom modules  │
│ crypto, os, ... │ │ crypto, os, ... │ │                 │
└─────────────────┘ └─────────────────┘ └─────────────────┘
```

---

## Package Structure

### SharpTS.Shim.Sdk

A small package containing the contracts for shim authors:

```
SharpTS.Shim.Sdk/
├── SharpTS.Shim.Sdk.csproj
├── Attributes/
│   └── SharpTSShimAttribute.cs      # Assembly-level discovery attribute
├── Contracts/
│   ├── IShimProvider.cs             # Main entry point for a shim
│   ├── IModuleExporter.cs           # Individual module contract
│   ├── IShimContext.cs              # Runtime context for modules
│   └── IModuleTypeProvider.cs       # Optional: static type information
└── Helpers/
    └── MethodBuilder.cs             # Utilities for creating BuiltInMethod instances
```

### Shim Package (e.g., SharpTS.Shim.Node)

```
SharpTS.Shim.Node/
├── SharpTS.Shim.Node.csproj
├── NodeShimProvider.cs              # Implements IShimProvider
├── Modules/
│   ├── FsModule.cs                  # Implements IModuleExporter
│   ├── PathModule.cs
│   ├── CryptoModule.cs
│   ├── HttpModule.cs
│   ├── OsModule.cs
│   └── ...
├── Runtime/                         # Static methods for IL compilation
│   ├── FsRuntime.cs
│   ├── PathRuntime.cs
│   └── ...
├── Types/                           # Optional: TypeInfo declarations
│   └── FsTypes.cs
└── Interop/                         # Platform-specific native interop
    ├── LibC.cs
    └── Kernel32.cs
```

---

## Core Interfaces

### IShimProvider

The main entry point discovered via assembly attribute:

```csharp
[assembly: SharpTSShim(typeof(NodeShimProvider))]

namespace SharpTS.Shim.Node;

public class NodeShimProvider : IShimProvider
{
    public ShimMetadata Metadata => new()
    {
        Name = "SharpTS.Shim.Node",
        Version = "22.0.0",
        Description = "Node.js 22.x API compatibility shim",
        TargetRuntime = "node",
        TargetRuntimeVersion = "22.0.0"
    };

    public IEnumerable<IModuleExporter> GetModules()
    {
        yield return new FsModule();
        yield return new PathModule();
        yield return new CryptoModule();
        yield return new HttpModule();
        yield return new OsModule();
        yield return new UrlModule();
        yield return new QuerystringModule();
        yield return new AssertModule();
        yield return new UtilModule();
        yield return new EventsModule();
        yield return new StreamModule();
        yield return new BufferModule();
        yield return new ChildProcessModule();
        yield return new ZlibModule();
        yield return new ReadlineModule();
        yield return new TimersModule();
        yield return new StringDecoderModule();
        yield return new PerfHooksModule();
    }
}
```

### IModuleExporter

Each module implements this interface:

```csharp
public interface IModuleExporter
{
    /// <summary>
    /// The primary import specifier (e.g., "fs", "path").
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// Alternative import specifiers (e.g., "node:fs" for "fs").
    /// </summary>
    IEnumerable<string> Aliases => [];

    /// <summary>
    /// Get all exports for interpreter mode.
    /// Returns a dictionary of export names to runtime values (BuiltInMethod, constants, objects).
    /// </summary>
    IReadOnlyDictionary<string, object?> GetExports(IShimContext context);

    /// <summary>
    /// The type containing static methods for compiled code to call.
    /// If null, compilation will use interpreter fallback.
    /// </summary>
    Type? RuntimeType => null;

    /// <summary>
    /// Maps TypeScript export names to C# method names in RuntimeType.
    /// e.g., "readFileSync" → "ReadFileSync"
    /// </summary>
    IReadOnlyDictionary<string, string> MethodMappings => new Dictionary<string, string>();

    /// <summary>
    /// Optional: Provides static type information for the type checker.
    /// If null, exports are typed as 'any'.
    /// </summary>
    IModuleTypeProvider? TypeProvider => null;
}
```

### IShimContext

Provides runtime context to modules:

```csharp
public interface IShimContext
{
    /// <summary>
    /// The working directory for the script.
    /// </summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// Environment variables.
    /// </summary>
    IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>
    /// Command-line arguments passed to the script.
    /// </summary>
    IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Creates a BuiltInMethod with the given implementation.
    /// </summary>
    BuiltInMethod CreateMethod(
        string name,
        int minArity,
        int maxArity,
        Func<object?, List<object?>, object?> implementation);

    /// <summary>
    /// Creates an async BuiltInMethod.
    /// </summary>
    BuiltInAsyncMethod CreateAsyncMethod(
        string name,
        int minArity,
        int maxArity,
        Func<object?, List<object?>, Task<object?>> implementation);
}
```

---

## CLI Integration

### Basic Usage

```bash
# Interpret with a shim
sharpts --shim SharpTS.Shim.Node script.ts

# Compile with a shim
sharpts --shim SharpTS.Shim.Node --compile script.ts

# Specify shim version
sharpts --shim SharpTS.Shim.Node@22.0.0 script.ts

# Use local shim DLL (for development)
sharpts --shim ./path/to/MyShim.dll script.ts

# Multiple shims
sharpts --shim SharpTS.Shim.Node --shim MyCompany.CustomShim script.ts
```

### Shim Resolution

1. If the shim path ends with `.dll` → load directly as assembly
2. Otherwise, treat as NuGet package reference:
   - Check global NuGet cache
   - If not found, restore from configured feeds
   - Load the assembly

### Conflict Resolution

If multiple shims export the same module name, the later shim wins (or error):

```bash
# Error: both shims export "fs"
sharpts --shim SharpTS.Shim.Node --shim AnotherNodeShim script.ts

# Explicit: only use specific modules from second shim
sharpts --shim SharpTS.Shim.Node --shim AnotherShim:custom-module script.ts
```

---

## IL Compilation

### Strategy: Assembly Reference (Default)

The compiled output references the shim assembly:

```
script.ts  ──compile──►  script.dll
                              │
                              ├── references SharpTS.Shim.Node.dll
                              └── references SharpTS.Runtime.dll
```

**Emitting calls to shim methods:**

When the IL compiler encounters `fs.readFileSync(path, encoding)`:

```csharp
// Get the shim's runtime type
var fsModule = shimRegistry.GetModule("fs");
var runtimeType = fsModule.RuntimeType;  // typeof(FsRuntime)
var methodName = fsModule.MethodMappings["readFileSync"];  // "ReadFileSync"
var method = runtimeType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);

// Emit the call
il.Emit(OpCodes.Ldarg, pathLocal);
il.Emit(OpCodes.Ldarg, encodingLocal);
il.Emit(OpCodes.Call, method);
```

**Runtime type example:**

```csharp
// In SharpTS.Shim.Node/Runtime/FsRuntime.cs
namespace SharpTS.Shim.Node.Runtime;

public static class FsRuntime
{
    public static bool ExistsSync(string path)
        => File.Exists(path) || Directory.Exists(path);

    public static object ReadFileSync(string path, string? encoding)
    {
        if (encoding != null)
            return File.ReadAllText(path);
        return new SharpTSBuffer(File.ReadAllBytes(path));
    }

    public static void WriteFileSync(string path, object data, object? options)
    {
        var text = data?.ToString() ?? "";
        File.WriteAllText(path, text);
    }

    // ... other fs methods
}
```

### Running Compiled Output

The shim DLL must be available at runtime:

```bash
# Option 1: DLL in same directory
cp SharpTS.Shim.Node.dll ./output/
dotnet ./output/script.dll

# Option 2: NuGet package reference in output project
dotnet ./output/script.dll  # Resolves from NuGet cache

# Option 3: Publish as self-contained
dotnet publish -c Release -r win-x64 --self-contained
```

---

## AOT Compilation

### Compatible Approach

The assembly reference strategy works with .NET Native AOT **if** the shim is AOT-compatible:

```bash
sharpts --shim SharpTS.Shim.Node --compile --aot script.ts
# Produces: script.exe (native binary)
```

**Requirements for AOT-compatible shims:**

1. Avoid reflection-heavy patterns without proper trimming annotations
2. No `System.Reflection.Emit` for runtime code generation
3. Use `[DynamicallyAccessedMembers]` attributes where needed
4. Avoid dynamic assembly loading

**Shim csproj for AOT compatibility:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsAotCompatible>true</IsAotCompatible>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  </PropertyGroup>
</Project>
```

### Self-Contained Strategy (Future Enhancement)

For scenarios where assembly references won't work, IL inlining could be added:

```bash
sharpts --shim SharpTS.Shim.Node --compile --self-contained script.ts
# Embeds shim code directly into output assembly
```

This would require shims to provide IL emission logic, not just runtime methods.

---

## Type System Integration

### Option 1: Dynamic Typing (Simple)

Module exports are typed as `any`:

```typescript
import * as fs from "fs";
fs.readFileSync("test.txt"); // No type errors, but no IntelliSense
```

### Option 2: Type Provider (Full Support)

Shims can provide type information:

```csharp
public interface IModuleTypeProvider
{
    /// <summary>
    /// Get the type for a module export.
    /// </summary>
    TypeInfo? GetExportType(string exportName);

    /// <summary>
    /// Get the complete module type (for import * as x).
    /// </summary>
    TypeInfo GetModuleType();
}
```

Implementation example:

```csharp
public class FsTypeProvider : IModuleTypeProvider
{
    private static readonly TypeInfo StringType = new TypeInfo.String();
    private static readonly TypeInfo BufferType = new TypeInfo.Buffer();
    private static readonly TypeInfo BoolType = new TypeInfo.Primitive(TokenType.TYPE_BOOLEAN);

    public TypeInfo? GetExportType(string exportName)
    {
        return exportName switch
        {
            "existsSync" => new TypeInfo.Function([StringType], BoolType),
            "readFileSync" => new TypeInfo.Function(
                [StringType, StringType],
                new TypeInfo.Union([StringType, BufferType]),
                RequiredParams: 1),
            "writeFileSync" => new TypeInfo.Function(
                [StringType, StringType],
                new TypeInfo.Void(),
                RequiredParams: 2),
            _ => null
        };
    }

    public TypeInfo GetModuleType()
    {
        // Return a Record type with all exports
        return new TypeInfo.Record(new Dictionary<string, TypeInfo>
        {
            ["existsSync"] = GetExportType("existsSync")!,
            ["readFileSync"] = GetExportType("readFileSync")!,
            ["writeFileSync"] = GetExportType("writeFileSync")!,
            // ... etc
        });
    }
}
```

---

## Versioning Strategy

For Node.js compatibility shims:

| Package Version            | Target Node.js   | Notes               |
| -------------------------- | ---------------- | ------------------- |
| `SharpTS.Shim.Node@18.0.0` | Node.js 18.x LTS | Stable API baseline |
| `SharpTS.Shim.Node@20.0.0` | Node.js 20.x LTS | Added features      |
| `SharpTS.Shim.Node@22.0.0` | Node.js 22.x     | Latest APIs         |

**Versioning scheme:**

- **Major**: Target Node.js major version
- **Minor**: Shim feature additions (new modules, new methods)
- **Patch**: Bug fixes, compatibility improvements

---

## Migration Path

### Current State

Node.js modules are in SharpTS core:

```
Runtime/BuiltIns/Modules/
├── BuiltInModuleRegistry.cs         # Hardcoded module list
├── Interpreter/
│   ├── BuiltInModuleValues.cs       # Hardcoded routing
│   ├── FsModuleInterpreter.cs
│   ├── PathModuleInterpreter.cs
│   └── ...
└── Interop/
    └── ...
```

### Migration Steps

1. **Create SharpTS.Shim.Sdk**
   - Define `IShimProvider`, `IModuleExporter`, `IShimContext`
   - Add `[SharpTSShim]` attribute

2. **Create SharpTS.Shim.Node**
   - Move `Modules/Interpreter/*.cs` to new package
   - Move `Modules/Interop/*.cs` to new package
   - Implement `IShimProvider` and `IModuleExporter` for each module
   - Add `Runtime/` classes for IL compilation support

3. **Update SharpTS Core**
   - Make `BuiltInModuleRegistry` use shim registry
   - Update `Interpreter.cs` to query shim registry
   - Update `ILCompiler` to emit calls to shim runtime types

4. **Update CLI**
   - Add `--shim` argument parsing
   - Add shim loading/discovery logic
   - Add NuGet package resolution

5. **Backwards Compatibility**
   - Optionally: auto-load `SharpTS.Shim.Node` if no shim specified and Node imports detected

---

## Example: Creating a Custom Shim

### 1. Create the Project

```bash
dotnet new classlib -n MyCompany.Shim.Redis
cd MyCompany.Shim.Redis
dotnet add package SharpTS.Shim.Sdk
```

### 2. Implement the Provider

```csharp
using SharpTS.Shim.Sdk;

[assembly: SharpTSShim(typeof(MyCompany.Shim.Redis.RedisShimProvider))]

namespace MyCompany.Shim.Redis;

public class RedisShimProvider : IShimProvider
{
    public ShimMetadata Metadata => new()
    {
        Name = "MyCompany.Shim.Redis",
        Version = "1.0.0",
        Description = "Redis client for SharpTS"
    };

    public IEnumerable<IModuleExporter> GetModules()
    {
        yield return new RedisModule();
    }
}
```

### 3. Implement the Module

```csharp
public class RedisModule : IModuleExporter
{
    public string ModuleName => "redis";

    public IReadOnlyDictionary<string, object?> GetExports(IShimContext context)
    {
        return new Dictionary<string, object?>
        {
            ["createClient"] = context.CreateMethod("createClient", 0, 1, CreateClient),
            ["VERSION"] = "1.0.0"
        };
    }

    private object? CreateClient(object? receiver, List<object?> args)
    {
        var options = args.Count > 0 ? args[0] as SharpTSObject : null;
        var host = options?.GetProperty("host")?.ToString() ?? "localhost";
        var port = options?.GetProperty("port") is double p ? (int)p : 6379;

        return new RedisClient(host, port);
    }

    public Type? RuntimeType => typeof(RedisRuntime);

    public IReadOnlyDictionary<string, string> MethodMappings => new Dictionary<string, string>
    {
        ["createClient"] = "CreateClient"
    };
}
```

### 4. Use the Shim

```typescript
// script.ts
import { createClient } from "redis";

const client = createClient({ host: "localhost", port: 6379 });
```

```bash
sharpts --shim MyCompany.Shim.Redis script.ts
```

---

## Open Questions

1. **Default shim behavior**: Should SharpTS auto-detect Node imports and suggest/load the Node shim?

2. **Shim composition**: Can shims extend other shims (e.g., a shim that adds methods to `fs`)?

3. **Type declaration files**: Should shims be able to provide `.d.ts` files for editor integration?

4. **Async module initialization**: Some modules may need async setup - how to handle?

5. **Shim configuration**: Should shims be configurable via `tsconfig.json` or a separate config?

---

## Related Files

- `Runtime/BuiltIns/BuiltInRegistry.cs` - Current built-in registration
- `Runtime/BuiltIns/Modules/BuiltInModuleRegistry.cs` - Current module registry
- `Runtime/BuiltIns/Modules/Interpreter/BuiltInModuleValues.cs` - Current module routing
- `Execution/Interpreter.cs` - Module loading in interpreter
- `Compilation/ILEmitter.cs` - IL emission for built-ins
