using System.Net;

namespace SharpTS.Node.Http;

/// <summary>
/// Represents an incoming HTTP request.
/// Wraps HttpListenerRequest with a Node.js-compatible API.
/// </summary>
public class IncomingMessage
{
    private readonly HttpListenerRequest _request;
    private Dictionary<string, string>? _headers;

    /// <summary>
    /// Creates a new IncomingMessage from an HttpListenerRequest.
    /// </summary>
    internal IncomingMessage(HttpListenerRequest request)
    {
        _request = request;
    }

    /// <summary>
    /// The HTTP method (GET, POST, etc.).
    /// </summary>
    public string Method => _request.HttpMethod;

    /// <summary>
    /// The request URL path and query string.
    /// </summary>
    public string Url => _request.RawUrl ?? "/";

    /// <summary>
    /// The HTTP version string (e.g., "1.1").
    /// </summary>
    public string HttpVersion => $"{_request.ProtocolVersion.Major}.{_request.ProtocolVersion.Minor}";

    /// <summary>
    /// The request headers as a dictionary.
    /// Header names are lowercased for consistency with Node.js.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get
        {
            if (_headers == null)
            {
                _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string? key in _request.Headers.AllKeys)
                {
                    if (key != null)
                    {
                        _headers[key.ToLowerInvariant()] = _request.Headers[key] ?? "";
                    }
                }
            }
            return _headers;
        }
    }

    /// <summary>
    /// The Host header value.
    /// </summary>
    public string? Host => _request.Headers["Host"];

    /// <summary>
    /// The Content-Type header value.
    /// </summary>
    public string? ContentType => _request.ContentType;

    /// <summary>
    /// The Content-Length header value, or -1 if not specified.
    /// </summary>
    public long ContentLength => _request.ContentLength64;

    /// <summary>
    /// The User-Agent header value.
    /// </summary>
    public string? UserAgent => _request.UserAgent;

    /// <summary>
    /// The client's IP address.
    /// </summary>
    public string? RemoteAddress => _request.RemoteEndPoint?.Address.ToString();

    /// <summary>
    /// The client's port.
    /// </summary>
    public int? RemotePort => _request.RemoteEndPoint?.Port;

    /// <summary>
    /// Whether the request was made over HTTPS.
    /// </summary>
    public bool Secure => _request.IsSecureConnection;

    /// <summary>
    /// Whether the request has a body.
    /// </summary>
    public bool HasBody => _request.HasEntityBody;

    /// <summary>
    /// Gets the request body as a stream.
    /// </summary>
    public Stream GetBodyStream() => _request.InputStream;

    /// <summary>
    /// Reads the entire request body as a string.
    /// </summary>
    public async Task<string> ReadBodyAsync()
    {
        if (!HasBody)
            return "";

        using var reader = new StreamReader(_request.InputStream, _request.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Reads the entire request body as a string (synchronous).
    /// </summary>
    public string ReadBody()
    {
        if (!HasBody)
            return "";

        using var reader = new StreamReader(_request.InputStream, _request.ContentEncoding);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads the entire request body as bytes.
    /// </summary>
    public async Task<byte[]> ReadBodyBytesAsync()
    {
        if (!HasBody)
            return Array.Empty<byte>();

        using var ms = new MemoryStream();
        await _request.InputStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Gets the underlying HttpListenerRequest.
    /// </summary>
    public HttpListenerRequest Raw => _request;
}
