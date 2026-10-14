using System.Net;
using VersePreflight.Core.Game;
using VersePreflight.Core.Platforms;

namespace VersePreflight.Core.Tests;

/// <summary>A unique temporary folder, deleted (without following links) on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VersePreflightTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>A fake game channel folder (e.g. LIVE) with the usual "user" folder.</summary>
    public GameEnvironment CreateEnvironment(string name = "LIVE")
    {
        var path = Combine("game", name);
        Directory.CreateDirectory(System.IO.Path.Combine(path, "user", "client", "0"));
        return new GameEnvironment(name, path, null);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FakePlatform(params string[] shaderCacheDirectories) : IPlatform
{
    public string Name => "Fake";
    public string ConfigDirectory => throw new NotSupportedException();
    public string DataDirectory => throw new NotSupportedException();
    public string? PreviousConfigDirectory => null;
    public IReadOnlyList<string> FindGameBaseCandidates() => [];
    public IReadOnlyList<string> GetShaderCacheDirectories() => shaderCacheDirectories;
    public IReadOnlyList<string> GetRunningGameProcesses() => [];
}

/// <summary>Returns canned responses keyed by request URL.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttpHandler Respond(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[url] = () => new HttpResponseMessage(status) { Content = new StringContent(body) };
        return this;
    }

    public FakeHttpHandler Respond(string url, byte[] body)
    {
        _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_responses.TryGetValue(request.RequestUri!.ToString(), out var make)
            ? make()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
