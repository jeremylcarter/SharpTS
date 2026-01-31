using System.Net.Http;
using SharpTS.Node.EventLoop;
using NodeHttp = SharpTS.Node.Http;
using Xunit;

namespace SharpTS.Node.Tests.Http;

public class ServerTests
{
    [Fact]
    public void CreateServer_ReturnsServer()
    {
        var server = NodeHttp.Http.CreateServer();
        Assert.NotNull(server);
    }

    [Fact]
    public void CreateServer_WithListener_RegistersRequestHandler()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.End();
        });

        Assert.True(server.HasListeners("request"));
    }

    [Fact]
    public void Listen_ThrowsOutsideEventLoop()
    {
        var server = NodeHttp.Http.CreateServer();

        Assert.Throws<InvalidOperationException>(() => server.Listen(0));
    }

    [Fact]
    public async Task Server_RespondsToRequest()
    {
        var tcs = new TaskCompletionSource<string>();
        var port = GetAvailablePort();

        var thread = new Thread(() =>
        {
            using var eventLoop = new NodeEventLoop();
            eventLoop.Run(() =>
            {
                var server = NodeHttp.Http.CreateServer((req, res) =>
                {
                    res.StatusCode = 200;
                    res.SetHeader("Content-Type", "text/plain");
                    res.End("Hello, World!");
                });

                server.Listen(port, async () =>
                {
                    try
                    {
                        using var client = new HttpClient();
                        var response = await client.GetStringAsync($"http://localhost:{port}/");
                        tcs.SetResult(response);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                    finally
                    {
                        server.Close();
                    }
                });
            });
        });

        thread.Start();

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Hello, World!", result);

        thread.Join(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Server_ReceivesRequestDetails()
    {
        var tcs = new TaskCompletionSource<(string method, string url)>();
        var port = GetAvailablePort();

        NodeHttp.Server? server = null;
        var thread = new Thread(() =>
        {
            using var eventLoop = new NodeEventLoop();
            eventLoop.Run(() =>
            {
                server = NodeHttp.Http.CreateServer((req, res) =>
                {
                    tcs.SetResult((req.Method, req.Url));
                    res.End();
                    server?.Close();
                });

                server.Listen(port, async () =>
                {
                    using var client = new HttpClient();
                    await client.GetAsync($"http://localhost:{port}/test?foo=bar");
                });
            });
        });

        thread.Start();

        var (method, url) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("GET", method);
        Assert.Equal("/test?foo=bar", url);

        thread.Join(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Server_SetsResponseHeaders()
    {
        var tcs = new TaskCompletionSource<string?>();
        var port = GetAvailablePort();

        var thread = new Thread(() =>
        {
            using var eventLoop = new NodeEventLoop();
            eventLoop.Run(() =>
            {
                var server = NodeHttp.Http.CreateServer((req, res) =>
                {
                    res.SetHeader("X-Custom-Header", "custom-value");
                    res.End("OK");
                });

                server.Listen(port, async () =>
                {
                    try
                    {
                        using var client = new HttpClient();
                        var response = await client.GetAsync($"http://localhost:{port}/");
                        var headerValue = response.Headers.TryGetValues("X-Custom-Header", out var values)
                            ? values.FirstOrDefault()
                            : null;
                        tcs.SetResult(headerValue);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                    finally
                    {
                        server.Close();
                    }
                });
            });
        });

        thread.Start();

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("custom-value", result);

        thread.Join(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Server_Address_ReturnsCorrectInfo()
    {
        var port = GetAvailablePort();

        using var eventLoop = new NodeEventLoop();
        NodeHttp.Server? capturedServer = null;
        NodeHttp.ServerAddress? address = null;

        var thread = new Thread(() =>
        {
            eventLoop.Run(() =>
            {
                capturedServer = NodeHttp.Http.CreateServer((req, res) => res.End());
                capturedServer.Listen(port, () =>
                {
                    address = capturedServer.Address();
                    capturedServer.Close();
                });
            });
        });

        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));

        Assert.NotNull(address);
        Assert.Equal(port, address!.Port);
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
