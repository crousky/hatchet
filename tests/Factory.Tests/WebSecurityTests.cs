using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Factory.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Factory.Tests;

/// <summary>Hatchet's web surface: the MCP token, the Origin check and the response headers, against the real app with a temporary data folder.</summary>
public sealed class WebSecurityTests : IDisposable
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "hatchet-web-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public WebSecurityTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host.UseSetting("Hatchet:DataDirectory", _data));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    private string Token => File.ReadAllText(Path.Combine(_data, McpToken.FileName)).Trim();

    private static HttpRequestMessage Post(string? token = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Mcp_without_a_token_is_unauthorized()
    {
        var response = await _factory.CreateClient().SendAsync(Post());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Mcp_with_a_wrong_token_is_unauthorized()
    {
        var response = await _factory.CreateClient().SendAsync(Post("not-the-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_with_the_token_answers()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/"); // starts the app, which makes the token
        var response = await client.SendAsync(Post(Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hatchet", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_foreign_origin_is_forbidden_everywhere()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/");
        foreach (var request in new[] { Post(Token, "http://evil.test"), new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { { "Origin", "http://evil.test" } } } })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_page_needs_no_token_and_carries_security_headers()
    {
        var response = await _factory.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public void Token_is_kept_between_starts_and_compared_exactly()
    {
        var first = new McpToken(_data);
        var second = new McpToken(_data);
        Assert.Equal(first.Value, second.Value);
        Assert.True(first.Matches("Bearer " + first.Value));
        Assert.False(first.Matches("Bearer " + first.Value + "x"));
        Assert.False(first.Matches(first.Value));
        Assert.False(first.Matches(null));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Only_loopback_addresses_count_as_local(string address, bool expected) =>
        Assert.Equal(expected, Program.IsLoopback(IPAddress.Parse(address)));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("evil.example", false)]
    [InlineData("localhost.evil.example", false)]
    [InlineData("", false)]
    public void Only_this_machines_names_are_accepted_as_the_host(string host, bool expected) =>
        Assert.Equal(expected, Program.IsLocalHost(host));

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData("//server/share", true)]
    [InlineData(@"C:\projects\x", false)]
    [InlineData("/home/me/x", false)]
    public void Network_paths_are_recognised(string path, bool expected) => Assert.Equal(expected, PathGuard.IsNetworkPath(path));
}
