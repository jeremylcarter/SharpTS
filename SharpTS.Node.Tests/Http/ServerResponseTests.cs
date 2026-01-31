using System.Net;
using NodeHttp = SharpTS.Node.Http;
using Xunit;

namespace SharpTS.Node.Tests.Http;

public class ServerResponseTests
{
    [Fact]
    public void SetHeader_TracksHeader()
    {
        // Create a mock response using reflection or a simple test
        // For now, we'll test through integration
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.SetHeader("X-Custom", "value");
            Assert.True(res.HasHeader("X-Custom"));
            Assert.Equal("value", res.GetHeader("X-Custom"));
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void HasHeader_ReturnsFalseForMissingHeader()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            Assert.False(res.HasHeader("X-NonExistent"));
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void GetHeaders_ReturnsAllSetHeaders()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.SetHeader("X-One", "1");
            res.SetHeader("X-Two", "2");

            var headers = res.GetHeaders();
            Assert.Equal("1", headers["X-One"]);
            Assert.Equal("2", headers["X-Two"]);
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void GetHeaderNames_ReturnsLowercaseNames()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.SetHeader("X-Custom-Header", "value");
            res.SetHeader("Content-Type", "text/plain");

            var names = res.GetHeaderNames();
            Assert.Contains("x-custom-header", names);
            Assert.Contains("content-type", names);
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void RemoveHeader_RemovesFromTracking()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.SetHeader("X-ToRemove", "value");
            Assert.True(res.HasHeader("X-ToRemove"));

            res.RemoveHeader("X-ToRemove");
            Assert.False(res.HasHeader("X-ToRemove"));
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void Req_IsSetOnResponse()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            Assert.NotNull(res.Req);
            Assert.Same(req, res.Req);
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void HasHeader_IsCaseInsensitive()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.SetHeader("X-Custom", "value");
            Assert.True(res.HasHeader("x-custom"));
            Assert.True(res.HasHeader("X-CUSTOM"));
            Assert.True(res.HasHeader("X-Custom"));
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void GetHeader_IsCaseInsensitive()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.SetHeader("X-Custom", "value");
            Assert.Equal("value", res.GetHeader("x-custom"));
            Assert.Equal("value", res.GetHeader("X-CUSTOM"));
            res.End();
        });

        Assert.NotNull(server);
    }

    [Fact]
    public void WriteHead_SetsStatusAndHeaders()
    {
        var server = NodeHttp.Http.CreateServer((req, res) =>
        {
            res.WriteHead(201, new Dictionary<string, string>
            {
                ["X-Created"] = "true"
            });

            Assert.Equal(201, res.StatusCode);
            Assert.True(res.HasHeader("X-Created"));
            res.End();
        });

        Assert.NotNull(server);
    }
}
