using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PriceInsight;

/// <summary>
/// The single definition of "worth one more try" for Universalis requests. Kept free of Dalamud types so
/// the retry policy can be exercised offline against a fake handler or a socket that resets.
/// </summary>
internal static class UniversalisRetry {
    internal static readonly TimeSpan TransientStatusBackoff = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Statuses worth a single retry: 408/429 plus anything 5xx. Anything else is either a success
    /// or a permanent client-side error that retrying cannot fix.
    /// </summary>
    public static bool IsTransient(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    /// <summary>
    /// No HTTP status and an I/O or socket failure underneath: the connection was reset or cut, typically
    /// a pooled keep-alive connection the server had already closed. Every Universalis call is a GET, so
    /// sending it again on a fresh connection is safe.
    /// </summary>
    public static bool IsConnectionDropped(HttpRequestException ex) {
        if (ex.StatusCode != null)
            return false;
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException) {
            if (inner is IOException or SocketException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> and, if it throws a transient-status or dropped-connection
    /// <see cref="HttpRequestException"/>, runs it exactly once more. Only the transient-status case
    /// backs off first; a dropped connection is retried at once because the next request opens a new one.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> attempt, Action<HttpRequestException> onRetry, CancellationToken cancellationToken) {
        try {
            return await attempt();
        } catch (HttpRequestException ex) when (IsTransient(ex.StatusCode)) {
            onRetry(ex);
            await Task.Delay(TransientStatusBackoff, cancellationToken);
        } catch (HttpRequestException ex) when (IsConnectionDropped(ex)) {
            onRetry(ex);
        }

        return await attempt();
    }

    /// <summary>
    /// GET with the same one-retry policy, for callers that grade non-OK statuses themselves: a transient
    /// status is retried once and the second response is returned whatever its status.
    /// <paramref name="onRetry"/> gets the status, or the exception when the connection dropped.
    /// </summary>
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient httpClient, string requestUri, Action<HttpStatusCode?, Exception?> onRetry, CancellationToken cancellationToken) {
        HttpResponseMessage result;
        try {
            result = await httpClient.GetAsync(requestUri, cancellationToken);
        } catch (HttpRequestException ex) when (IsConnectionDropped(ex)) {
            onRetry(null, ex);
            return await httpClient.GetAsync(requestUri, cancellationToken);
        }

        if (!IsTransient(result.StatusCode))
            return result;

        onRetry(result.StatusCode, null);
        result.Dispose();
        await Task.Delay(TransientStatusBackoff, cancellationToken);
        return await httpClient.GetAsync(requestUri, cancellationToken);
    }
}
