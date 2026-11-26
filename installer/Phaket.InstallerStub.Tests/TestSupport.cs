using System.Net;
using System.Text;

namespace Phaket.InstallerStub.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(respond(request));
    }
}

internal static class Bytes
{
    public static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static string Manifest(params (string Version, string File, string Sha)[] releases) =>
        $$"""
        {
          "success": true,
          "latestVersion": "{{releases.FirstOrDefault().Version ?? "0.0.0"}}",
          "downloadBaseUrl": "https://cdn.example/phaket/",
          "releases": [{{string.Join(',', releases.Select(r => $$"""{"Version":"{{r.Version}}","File":"{{r.File.Replace("\\", "\\\\")}}","SHA256":"{{r.Sha}}"}"""))}}]
        }
        """;

    public static string Nupkg(string version) => $$"""
        {"Version":"{{version}}","File":"Phaket-{{version}}-full.nupkg"}
        """;
}