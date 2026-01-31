using System.Net;
using SharpTS.Runtime.Types;
using Interp = SharpTS.Execution.Interpreter;

namespace SharpTS.Runtime.BuiltIns.Modules.Interpreter;

/// <summary>
/// Interpreter-mode implementation of the Node.js 'http' module.
/// </summary>
/// <remarks>
/// Provides HTTP server functionality compatible with Node.js http module.
/// Uses HttpListener under the hood and integrates with the interpreter's
/// callback system for request handling.
/// </remarks>
public static class HttpModuleInterpreter
{
    /// <summary>
    /// Gets all exported values for the http module.
    /// </summary>
    public static Dictionary<string, object?> GetExports()
    {
        return new Dictionary<string, object?>
        {
            ["createServer"] = new BuiltInMethod("createServer", 0, 1, CreateServer),
            ["METHODS"] = CreateMethodsArray(),
            ["STATUS_CODES"] = CreateStatusCodesObject()
        };
    }

    /// <summary>
    /// http.createServer([requestListener]) - creates a new HTTP server.
    /// </summary>
    private static object? CreateServer(Interp interpreter, object? receiver, List<object?> args)
    {
        ISharpTSCallable? requestListener = null;
        if (args.Count > 0 && args[0] is ISharpTSCallable callable)
        {
            requestListener = callable;
        }

        return new HttpServerInstance(interpreter, requestListener);
    }

    private static SharpTSArray CreateMethodsArray()
    {
        var methods = new List<object?> { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE", "CONNECT" };
        return new SharpTSArray(methods);
    }

    private static SharpTSObject CreateStatusCodesObject()
    {
        var codes = new Dictionary<string, object?>
        {
            ["100"] = "Continue",
            ["101"] = "Switching Protocols",
            ["200"] = "OK",
            ["201"] = "Created",
            ["204"] = "No Content",
            ["301"] = "Moved Permanently",
            ["302"] = "Found",
            ["304"] = "Not Modified",
            ["400"] = "Bad Request",
            ["401"] = "Unauthorized",
            ["403"] = "Forbidden",
            ["404"] = "Not Found",
            ["500"] = "Internal Server Error",
            ["502"] = "Bad Gateway",
            ["503"] = "Service Unavailable"
        };
        return new SharpTSObject(codes);
    }
}

/// <summary>
/// Represents an HTTP server instance (http.Server).
/// Extends EventEmitter with server-specific methods.
/// </summary>
public class HttpServerInstance : SharpTSEventEmitter
{
    private readonly Interp _interpreter;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _listening;
    private int _port;
    private string _hostname = "localhost";

    public HttpServerInstance(Interp interpreter, ISharpTSCallable? requestListener)
    {
        _interpreter = interpreter;
        if (requestListener != null)
        {
            AddListenerInternal("request", requestListener, once: false, prepend: false);
        }
    }

    /// <summary>
    /// Gets a property by name. Extends EventEmitter with server-specific methods.
    /// </summary>
    public override object? GetProperty(string name)
    {
        // First check server-specific methods
        return name switch
        {
            "listen" => new BuiltInMethod("listen", 1, 3, ListenMethod),
            "close" => new BuiltInMethod("close", 0, 1, CloseMethod),
            "address" => new BuiltInMethod("address", 0, 0, AddressMethod),
            "listening" => _listening,
            _ => base.GetProperty(name) // Fall back to EventEmitter methods
        };
    }

    /// <summary>
    /// Checks if a property exists.
    /// </summary>
    public override bool HasProperty(string name)
    {
        return name is "listen" or "close" or "address" or "listening" || base.HasProperty(name);
    }

    /// <summary>
    /// Gets all property names for iteration.
    /// </summary>
    public override IEnumerable<string> PropertyNames =>
        new[] { "listen", "close", "address", "listening" }.Concat(base.PropertyNames);

    private object? ListenMethod(Interp interpreter, object? receiver, List<object?> args)
    {
        if (args.Count == 0)
            throw new Exception("Runtime Error: listen() requires at least a port number");

        _port = Convert.ToInt32(args[0]);

        ISharpTSCallable? callback = null;

        if (args.Count >= 2)
        {
            if (args[1] is string hostname)
            {
                _hostname = hostname;
                if (args.Count >= 3 && args[2] is ISharpTSCallable cb)
                    callback = cb;
            }
            else if (args[1] is ISharpTSCallable cb)
            {
                callback = cb;
            }
        }

        // Start the HTTP listener
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{_hostname}:{_port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new Exception($"Runtime Error: Failed to start HTTP server on {_hostname}:{_port}. {ex.Message}");
        }

        _listening = true;
        _cts = new CancellationTokenSource();

        // Register this server as an active handle to keep the event loop alive
        interpreter.Ref();

        // Start accepting requests in background
        _ = AcceptRequestsAsync(_cts.Token);

        // Call the listening callback if provided
        if (callback != null)
        {
            callback.Call(interpreter, new List<object?>());
        }

        // Emit 'listening' event
        EmitWithInterpreter(interpreter, "listening", new List<object?>());

        return this;
    }

    private async Task AcceptRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener!.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Queue the request handling to the interpreter's callback system
                _interpreter.ScheduleTimer(0, 0, () =>
                {
                    try
                    {
                        var req = new HttpIncomingMessage(context.Request);
                        var res = new HttpServerResponse(context.Response, req);
                        EmitWithInterpreter(_interpreter, "request", new List<object?> { req, res });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error handling request: {ex.Message}");
                    }
                }, false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error accepting request: {ex.Message}");
                try
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }

    private object? CloseMethod(Interp interpreter, object? receiver, List<object?> args)
    {
        ISharpTSCallable? callback = null;
        if (args.Count > 0 && args[0] is ISharpTSCallable cb)
            callback = cb;

        if (_listening)
        {
            _listening = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }

            // Unregister this server from active handles
            interpreter.Unref();
        }

        // Emit 'close' event and call callback
        EmitWithInterpreter(interpreter, "close", new List<object?>());
        callback?.Call(interpreter, new List<object?>());

        return this;
    }

    private object? AddressMethod(Interp interpreter, object? receiver, List<object?> args)
    {
        if (!_listening)
            return null;

        return new SharpTSObject(new Dictionary<string, object?>
        {
            ["port"] = (double)_port,
            ["family"] = "IPv4",
            ["address"] = _hostname == "+" || _hostname == "*" ? "0.0.0.0" : _hostname
        });
    }
}

/// <summary>
/// Represents an incoming HTTP request (http.IncomingMessage).
/// </summary>
public class HttpIncomingMessage : SharpTSObject
{
    public HttpIncomingMessage(HttpListenerRequest request)
        : base(CreateFields(request))
    {
    }

    private static Dictionary<string, object?> CreateFields(HttpListenerRequest request)
    {
        return new Dictionary<string, object?>
        {
            ["method"] = request.HttpMethod,
            ["url"] = request.RawUrl ?? "/",
            ["httpVersion"] = $"{request.ProtocolVersion.Major}.{request.ProtocolVersion.Minor}",
            ["headers"] = CreateHeadersObject(request)
        };
    }

    private static SharpTSObject CreateHeadersObject(HttpListenerRequest request)
    {
        var headers = new Dictionary<string, object?>();
        foreach (string? key in request.Headers.AllKeys)
        {
            if (key != null)
            {
                headers[key.ToLowerInvariant()] = request.Headers[key] ?? "";
            }
        }
        return new SharpTSObject(headers);
    }
}

/// <summary>
/// Represents an HTTP server response (http.ServerResponse).
/// </summary>
public class HttpServerResponse : SharpTSObject
{
    private readonly HttpListenerResponse _response;
    private readonly HttpIncomingMessage _request;
    private bool _headersSent;
    private bool _finished;
    private readonly Dictionary<string, string> _pendingHeaders = new(StringComparer.OrdinalIgnoreCase);

    public HttpServerResponse(HttpListenerResponse response, HttpIncomingMessage request)
        : base(new Dictionary<string, object?>())
    {
        _response = response;
        _request = request;

        // Add methods to fields so they're accessible via GetProperty
        SetProperty("writeHead", new BuiltInMethod("writeHead", 1, 3, WriteHead));
        SetProperty("setHeader", new BuiltInMethod("setHeader", 2, 2, SetHeader));
        SetProperty("getHeader", new BuiltInMethod("getHeader", 1, 1, GetHeader));
        SetProperty("hasHeader", new BuiltInMethod("hasHeader", 1, 1, HasHeader));
        SetProperty("removeHeader", new BuiltInMethod("removeHeader", 1, 1, RemoveHeader));
        SetProperty("write", new BuiltInMethod("write", 1, 2, Write));
        SetProperty("end", new BuiltInMethod("end", 0, 2, End));

        // Add properties
        SetProperty("statusCode", 200.0);
        SetProperty("statusMessage", "OK");
        SetProperty("headersSent", false);
        SetProperty("finished", false);
        SetProperty("req", request);
    }

    private object? WriteHead(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot call writeHead after headers have been sent");

        var statusCode = Convert.ToInt32(args[0]);
        _response.StatusCode = statusCode;

        // Handle optional statusMessage and headers
        int headerArgIndex = 1;
        if (args.Count > 1 && args[1] is string statusMsg)
        {
            _response.StatusDescription = statusMsg;
            headerArgIndex = 2;
        }

        if (args.Count > headerArgIndex && args[headerArgIndex] is SharpTSObject headers)
        {
            foreach (var kvp in headers.Fields)
            {
                if (kvp.Value != null)
                {
                    SetHeaderInternal(kvp.Key, kvp.Value.ToString()!);
                }
            }
        }

        return this;
    }

    private object? SetHeader(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot set headers after they have been sent");

        var name = args[0]?.ToString() ?? "";
        var value = args[1]?.ToString() ?? "";
        SetHeaderInternal(name, value);
        return this;
    }

    private void SetHeaderInternal(string name, string value)
    {
        _pendingHeaders[name] = value;

        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            _response.ContentType = value;
        }
        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(value, out var length))
                _response.ContentLength64 = length;
        }
        else
        {
            _response.Headers[name] = value;
        }
    }

    private object? GetHeader(Interp interpreter, object? receiver, List<object?> args)
    {
        var name = args[0]?.ToString() ?? "";
        if (_pendingHeaders.TryGetValue(name, out var value))
            return value;
        return null;
    }

    private object? HasHeader(Interp interpreter, object? receiver, List<object?> args)
    {
        var name = args[0]?.ToString() ?? "";
        return _pendingHeaders.ContainsKey(name);
    }

    private object? RemoveHeader(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_headersSent)
            throw new Exception("Runtime Error: Cannot remove headers after they have been sent");

        var name = args[0]?.ToString() ?? "";
        _pendingHeaders.Remove(name);
        _response.Headers.Remove(name);
        return this;
    }

    private object? Write(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_finished)
            throw new Exception("Runtime Error: Cannot write after response has ended");

        _headersSent = true;

        var data = args[0]?.ToString() ?? "";
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        _response.OutputStream.Write(bytes, 0, bytes.Length);

        return true;
    }

    private object? End(Interp interpreter, object? receiver, List<object?> args)
    {
        if (_finished)
            return this;

        if (args.Count > 0 && args[0] != null)
        {
            Write(interpreter, receiver, args);
        }

        _finished = true;
        _headersSent = true;

        try
        {
            _response.OutputStream.Close();
            _response.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed
        }

        return this;
    }
}
