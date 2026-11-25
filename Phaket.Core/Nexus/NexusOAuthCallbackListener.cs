using System.Net;
using System.Text;

namespace Phaket.Core.Nexus;

public sealed record NexusOAuthCallback(string Code, string State)
{
    // Constant-time comparison is unnecessary for a random public state, but keeps the shape strict.
    public bool Matches(string expectedState) => string.Equals(State, expectedState, StringComparison.Ordinal);
}

// Loopback receiver for the OAuth redirect: bound to 127.0.0.1:8642 only for the
// duration of one sign-in, closed as soon as the code (or an error) arrives.
public static class NexusOAuthCallbackListener
{
    public const string Prefix = "http://127.0.0.1:8642/";

    public static async Task<NexusOAuthCallback> WaitAsync(string expectedState, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(Prefix);
            listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or UnauthorizedAccessException)
        {
            listener.Close();
            throw new NexusApiException("NEXUS_OAUTH_UNAVAILABLE", ex);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(timeoutSource.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NexusApiException("NEXUS_OAUTH_TIMEOUT");
            }

            var query = context.Request.QueryString;
            string? state = query["state"];
            string? code = query["code"];
            string? error = query["error"];
            string? description = query["error_description"];

            bool ok = string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code) && state == expectedState;
            await RespondAsync(context, ok,
                ok ? "Phaket: możesz wrócić do aplikacji. / You can return to the app."
                   : "Phaket: logowanie nie powiodło się. / Sign-in failed.").ConfigureAwait(false);

            if (!string.IsNullOrEmpty(error))
                throw new NexusApiException("NEXUS_OAUTH_FAILED",
                    new InvalidOperationException(string.IsNullOrEmpty(description) ? error : description));
            if (!string.IsNullOrEmpty(expectedState) && state != expectedState)
                throw new NexusApiException("NEXUS_OAUTH_STATE");
            if (string.IsNullOrEmpty(code)) throw new NexusApiException("NEXUS_OAUTH_FAILED");
            return new(code, state!);
        }
        finally { listener.Close(); }
    }

    private static async Task RespondAsync(HttpListenerContext context, bool ok, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html lang=\"pl\"><head><meta charset=\"utf-8\"><title>Phaket</title></head><body><p>{message}</p></body></html>");
        context.Response.StatusCode = ok ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        try { await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false); }
        catch (IOException) { }
        finally { context.Response.Close(); }
    }
}