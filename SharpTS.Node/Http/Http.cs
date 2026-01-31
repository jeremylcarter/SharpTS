namespace SharpTS.Node.Http;

/// <summary>
/// Node.js-compatible HTTP module.
/// Provides factory methods for creating HTTP servers.
/// </summary>
public static class Http
{
    /// <summary>
    /// Creates an HTTP server without a request listener.
    /// Use server.On("request", ...) to add a handler.
    /// </summary>
    /// <returns>A new Server instance.</returns>
    public static Server CreateServer() => new Server();

    /// <summary>
    /// Creates an HTTP server with a request listener.
    /// </summary>
    /// <param name="requestListener">The callback to invoke for each request.</param>
    /// <returns>A new Server instance.</returns>
    public static Server CreateServer(Action<IncomingMessage, ServerResponse> requestListener)
    {
        var server = new Server();
        server.On("request", requestListener);
        return server;
    }

    /// <summary>
    /// HTTP methods supported by the server.
    /// </summary>
    public static readonly string[] Methods = new[]
    {
        "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE", "CONNECT"
    };

    /// <summary>
    /// HTTP status codes and their descriptions.
    /// </summary>
    public static readonly Dictionary<int, string> StatusCodes = new()
    {
        [100] = "Continue",
        [101] = "Switching Protocols",
        [200] = "OK",
        [201] = "Created",
        [202] = "Accepted",
        [204] = "No Content",
        [301] = "Moved Permanently",
        [302] = "Found",
        [304] = "Not Modified",
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [403] = "Forbidden",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [408] = "Request Timeout",
        [409] = "Conflict",
        [410] = "Gone",
        [413] = "Payload Too Large",
        [414] = "URI Too Long",
        [415] = "Unsupported Media Type",
        [429] = "Too Many Requests",
        [500] = "Internal Server Error",
        [501] = "Not Implemented",
        [502] = "Bad Gateway",
        [503] = "Service Unavailable",
        [504] = "Gateway Timeout",
    };
}
