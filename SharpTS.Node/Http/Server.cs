using System.Net;
using SharpTS.Node.Events;
using SharpTS.Node.EventLoop;

namespace SharpTS.Node.Http;

/// <summary>
/// HTTP server with multi-core accept loops.
/// Extends EventEmitter for Node.js-compatible event handling.
/// </summary>
public class Server : EventEmitter
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _acceptTasks = new();
    private NodeEventLoop? _eventLoop;
    private bool _listening;

    /// <summary>
    /// Number of concurrent accept loops. Defaults to processor count.
    /// Set before calling Listen().
    /// </summary>
    public int AcceptLoopCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// The port the server is listening on.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// The hostname the server is listening on.
    /// </summary>
    public string Hostname { get; private set; } = "localhost";

    /// <summary>
    /// Whether the server is currently listening for connections.
    /// </summary>
    public bool Listening => _listening;

    /// <summary>
    /// Starts the server listening on the specified port.
    /// </summary>
    /// <param name="port">The port to listen on.</param>
    /// <param name="callback">Optional callback to invoke when listening starts.</param>
    /// <returns>This Server instance for chaining.</returns>
    public Server Listen(int port, Action? callback = null)
    {
        return Listen(port, "localhost", callback);
    }

    /// <summary>
    /// Starts the server listening on the specified port and hostname.
    /// </summary>
    /// <param name="port">The port to listen on.</param>
    /// <param name="hostname">The hostname to bind to (e.g., "localhost", "+", "*").</param>
    /// <param name="callback">Optional callback to invoke when listening starts.</param>
    /// <returns>This Server instance for chaining.</returns>
    public Server Listen(int port, string hostname, Action? callback = null)
    {
        if (_listening)
            throw new InvalidOperationException("Server is already listening");

        Port = port;
        Hostname = hostname;
        _eventLoop = NodeEventLoop.Current
            ?? throw new InvalidOperationException("Server.Listen must be called within an event loop. Use NodeEventLoop.Run().");

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{hostname}:{port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"Failed to start HTTP listener on {hostname}:{port}. {ex.Message}", ex);
        }

        _cts = new CancellationTokenSource();
        _listening = true;

        // Track this server as a pending operation (keeps event loop alive)
        _eventLoop.Ref();

        // Start multiple accept loops for multi-core throughput
        for (int i = 0; i < AcceptLoopCount; i++)
        {
            _acceptTasks.Add(Task.Run(() => AcceptLoopAsync(_cts.Token)));
        }

        // Fire 'listening' event and callback on event loop
        _eventLoop.Enqueue(() =>
        {
            Emit("listening");
            callback?.Invoke();
        });

        return this;
    }

    /// <summary>
    /// Accept loop - runs on ThreadPool. One per CPU core for max throughput.
    /// Each loop independently accepts connections and queues them to the event loop.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener!.IsListening)
        {
            try
            {
                // Await connection (non-blocking, uses I/O completion port)
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Capture event loop reference
                var loop = _eventLoop!;

                // Queue callback to main event loop thread (thread-safe)
                loop.Enqueue(() =>
                {
                    try
                    {
                        var req = new IncomingMessage(context.Request);
                        var res = new ServerResponse(context.Response, req);
                        Emit("request", req, res);
                    }
                    catch (Exception ex)
                    {
                        EmitError(ex);
                    }
                });
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                // Server stopped, exit gracefully
                break;
            }
            catch (ObjectDisposedException)
            {
                // Listener disposed, exit
                break;
            }
            catch (Exception ex)
            {
                // Log error but keep accepting
                _eventLoop?.Enqueue(() => EmitError(ex));
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

    /// <summary>
    /// Stops the server and closes all connections.
    /// </summary>
    /// <param name="callback">Optional callback to invoke when the server is closed.</param>
    public void Close(Action? callback = null)
    {
        if (!_listening)
        {
            callback?.Invoke();
            return;
        }

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

        _eventLoop?.Enqueue(() =>
        {
            Emit("close");
            callback?.Invoke();

            // Release our ref on the event loop
            _eventLoop?.Unref();
        });
    }

    /// <summary>
    /// Gets the server address information.
    /// </summary>
    /// <returns>An object containing port, family, and address.</returns>
    public ServerAddress? Address()
    {
        if (!_listening)
            return null;

        return new ServerAddress
        {
            Port = Port,
            Family = "IPv4",
            Address = Hostname == "+" || Hostname == "*" ? "0.0.0.0" : Hostname
        };
    }

    private void EmitError(Exception ex)
    {
        if (HasListeners("error"))
        {
            Emit("error", ex);
        }
        else
        {
            Console.Error.WriteLine($"Server error: {ex}");
        }
    }
}

/// <summary>
/// Server address information.
/// </summary>
public class ServerAddress
{
    /// <summary>The port the server is listening on.</summary>
    public int Port { get; init; }

    /// <summary>The address family (IPv4 or IPv6).</summary>
    public string Family { get; init; } = "IPv4";

    /// <summary>The address the server is bound to.</summary>
    public string Address { get; init; } = "localhost";
}
