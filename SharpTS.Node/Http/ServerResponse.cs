using System.Net;
using System.Text;

namespace SharpTS.Node.Http;

/// <summary>
/// Represents an HTTP response.
/// Wraps HttpListenerResponse with a Node.js-compatible API.
/// </summary>
public class ServerResponse
{
    private readonly HttpListenerResponse _response;
    private bool _headersSent;
    private bool _finished;
    private readonly Dictionary<string, string> _customHeaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new ServerResponse from an HttpListenerResponse.
    /// </summary>
    internal ServerResponse(HttpListenerResponse response, IncomingMessage? request = null)
    {
        _response = response;
        Req = request;
    }

    /// <summary>
    /// Reference to the IncomingMessage object (request).
    /// </summary>
    public IncomingMessage? Req { get; }

    /// <summary>
    /// Gets or sets the HTTP status code.
    /// </summary>
    public int StatusCode
    {
        get => _response.StatusCode;
        set
        {
            CheckHeadersSent();
            _response.StatusCode = value;
        }
    }

    /// <summary>
    /// Gets or sets the HTTP status message.
    /// </summary>
    public string StatusMessage
    {
        get => _response.StatusDescription;
        set
        {
            CheckHeadersSent();
            _response.StatusDescription = value;
        }
    }

    /// <summary>
    /// Whether headers have been sent to the client.
    /// </summary>
    public bool HeadersSent => _headersSent;

    /// <summary>
    /// Whether the response has been finished.
    /// </summary>
    public bool Finished => _finished;

    /// <summary>
    /// Sets a response header.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    /// <returns>This ServerResponse for chaining.</returns>
    public ServerResponse SetHeader(string name, string value)
    {
        CheckHeadersSent();

        // Track in our dictionary for GetHeaders/HasHeader
        _customHeaders[name] = value;

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

        return this;
    }

    /// <summary>
    /// Gets a response header value.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>The header value, or null if not set.</returns>
    public string? GetHeader(string name)
    {
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            return _response.ContentType;

        if (_customHeaders.TryGetValue(name, out var value))
            return value;

        return _response.Headers[name];
    }

    /// <summary>
    /// Returns true if the header identified by name is currently set in the outgoing headers.
    /// </summary>
    /// <param name="name">The header name (case-insensitive).</param>
    /// <returns>True if the header is set.</returns>
    public bool HasHeader(string name)
    {
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrEmpty(_response.ContentType);

        return _customHeaders.ContainsKey(name) || _response.Headers[name] != null;
    }

    /// <summary>
    /// Returns a shallow copy of the current outgoing headers.
    /// </summary>
    /// <returns>Dictionary of header names to values.</returns>
    public IDictionary<string, string> GetHeaders()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Add custom headers
        foreach (var kvp in _customHeaders)
        {
            result[kvp.Key] = kvp.Value;
        }

        // Add Content-Type if set
        if (!string.IsNullOrEmpty(_response.ContentType))
        {
            result["Content-Type"] = _response.ContentType;
        }

        return result;
    }

    /// <summary>
    /// Returns an array containing the unique names of the current outgoing headers.
    /// All header names are lowercase.
    /// </summary>
    /// <returns>Array of lowercase header names.</returns>
    public string[] GetHeaderNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _customHeaders.Keys)
        {
            names.Add(key.ToLowerInvariant());
        }

        if (!string.IsNullOrEmpty(_response.ContentType))
        {
            names.Add("content-type");
        }

        return names.ToArray();
    }

    /// <summary>
    /// Removes a response header.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>This ServerResponse for chaining.</returns>
    public ServerResponse RemoveHeader(string name)
    {
        CheckHeadersSent();
        _customHeaders.Remove(name);
        _response.Headers.Remove(name);
        return this;
    }

    /// <summary>
    /// Writes the status code and headers.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="headers">Optional headers to set.</param>
    /// <returns>This ServerResponse for chaining.</returns>
    public ServerResponse WriteHead(int statusCode, IDictionary<string, string>? headers = null)
    {
        CheckHeadersSent();

        _response.StatusCode = statusCode;

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                SetHeader(key, value);
            }
        }

        return this;
    }

    /// <summary>
    /// Writes data to the response body.
    /// </summary>
    /// <param name="chunk">The string data to write.</param>
    /// <param name="encoding">The encoding to use (defaults to UTF-8).</param>
    /// <returns>This ServerResponse for chaining.</returns>
    public ServerResponse Write(string chunk, Encoding? encoding = null)
    {
        CheckFinished();
        _headersSent = true;

        var bytes = (encoding ?? Encoding.UTF8).GetBytes(chunk);
        _response.OutputStream.Write(bytes, 0, bytes.Length);

        return this;
    }

    /// <summary>
    /// Writes data to the response body.
    /// </summary>
    /// <param name="chunk">The byte data to write.</param>
    /// <returns>This ServerResponse for chaining.</returns>
    public ServerResponse Write(byte[] chunk)
    {
        CheckFinished();
        _headersSent = true;

        _response.OutputStream.Write(chunk, 0, chunk.Length);

        return this;
    }

    /// <summary>
    /// Ends the response, optionally writing final data.
    /// </summary>
    /// <param name="data">Optional final data to write.</param>
    /// <param name="encoding">The encoding to use (defaults to UTF-8).</param>
    public void End(string? data = null, Encoding? encoding = null)
    {
        if (_finished)
            return;

        if (data != null)
        {
            Write(data, encoding);
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
        catch (InvalidOperationException)
        {
            // Already closed
        }
    }

    /// <summary>
    /// Ends the response with byte data.
    /// </summary>
    /// <param name="data">The byte data to write.</param>
    public void End(byte[] data)
    {
        if (_finished)
            return;

        Write(data);

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
    }

    /// <summary>
    /// Sends a JSON response.
    /// </summary>
    /// <param name="json">The JSON string to send.</param>
    /// <param name="statusCode">The HTTP status code (defaults to 200).</param>
    public void Json(string json, int statusCode = 200)
    {
        StatusCode = statusCode;
        SetHeader("Content-Type", "application/json; charset=utf-8");
        End(json);
    }

    /// <summary>
    /// Redirects the client to another URL.
    /// </summary>
    /// <param name="url">The URL to redirect to.</param>
    /// <param name="statusCode">The redirect status code (defaults to 302).</param>
    public void Redirect(string url, int statusCode = 302)
    {
        StatusCode = statusCode;
        _response.RedirectLocation = url;
        End();
    }

    /// <summary>
    /// Gets the underlying HttpListenerResponse.
    /// </summary>
    public HttpListenerResponse Raw => _response;

    private void CheckHeadersSent()
    {
        if (_headersSent)
            throw new InvalidOperationException("Cannot modify headers after they have been sent");
    }

    private void CheckFinished()
    {
        if (_finished)
            throw new InvalidOperationException("Cannot write to response after it has been ended");
    }
}
