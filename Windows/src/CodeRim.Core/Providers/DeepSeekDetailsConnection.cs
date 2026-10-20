using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Providers;

public sealed partial class HttpProviders
{
    private sealed class DeepSeekOptionalHttpException(HttpStatusCode status) : Exception
    { internal HttpStatusCode Status { get; } = status; }

    private async Task<ProviderReading> WithDeepSeekDetailsAsync(ProviderReading balance, string selectedToken, CancellationToken token)
    {
        if (balance.State != ReadingState.Ready) return balance;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        async Task<T> Bounded<T>(Task<T> operation)
        {
            try { return await operation.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _ = operation.ContinueWith(done => {
                    if (done.IsCompletedSuccessfully && done.Result is IDisposable resource) resource.Dispose();
                    else _ = done.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        async Task<JsonElement> Request(string suffix)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://platform.deepseek.com/api/v0/usage/" + suffix);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", selectedToken);
            request.Headers.Add("x-client-platform", "web");
            using var response = await Bounded(client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new DeepSeekOptionalHttpException(response.StatusCode);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
            using var stream = await Bounded(response.Content.ReadAsStreamAsync(deadline.Token)).ConfigureAwait(false);
            using var content = new MemoryStream(); var buffer = new byte[16384];
            while (true)
            {
                var count = await Bounded(stream.ReadAsync(buffer, deadline.Token).AsTask()).ConfigureAwait(false);
                if (count == 0) break;
                if (content.Length + count > 2 * 1024 * 1024) throw new InvalidDataException();
                content.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            return json.RootElement.Clone();
        }
        async Task<(JsonElement Amount, JsonElement Cost)> Pair(string prefix, string query)
        {
            var amount = Request(prefix + "amount?" + query); var cost = Request(prefix + "cost?" + query);
            // Every request has the same independent deadline, including a transport that
            // ignores cancellation. A failed optional request never updates primary cooldown.
            try { await Task.WhenAll(amount, cost).ConfigureAwait(false); }
            catch
            {
                // A parse/network failure in one response cannot hide the other
                // endpoint's authentication or rate-limit refusal.
                foreach (var task in new[] { amount, cost })
                {
                    if (task.IsCompletedSuccessfully) DeepSeekUsageDetails.CheckAuthentication(task.Result);
                    if (task.Exception?.Flatten().InnerExceptions.OfType<DeepSeekOptionalHttpException>()
                        .FirstOrDefault(error => error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) is { } refused)
                        throw refused;
                }
                throw;
            }
            return (await amount.ConfigureAwait(false), await cost.ConfigureAwait(false));
        }
        try
        {
            var now = DateTimeOffset.Now; var range = DeepSeekUsageDetails.Window(now);
            var query = "start=" + range.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                + "&end=" + range.End.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                + "&tz=" + ((int)now.Offset.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            IReadOnlyList<LimitWindow> details;
            try
            {
                var pair = await Pair("by_api_key/", query).ConfigureAwait(false);
                details = DeepSeekUsageDetails.ParseByKey(pair.Amount, pair.Cost, now);
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidDataException or JsonException or OverflowException
                || failure is DeepSeekOptionalHttpException http && http.Status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
            {
                deadline.Token.ThrowIfCancellationRequested();
                var utc = now.ToUniversalTime();
                var pair = await Pair("", "month=" + utc.Month.ToString(CultureInfo.InvariantCulture) + "&year=" + utc.Year.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                details = DeepSeekUsageDetails.ParseMonthly(pair.Amount, pair.Cost, utc);
            }
            token.ThrowIfCancellationRequested();
            return balance with { Windows = balance.Windows.Concat(details).ToArray() };
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidDataException or JsonException
            or OverflowException or UnauthorizedAccessException or DeepSeekOptionalHttpException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return balance with { State = ReadingState.Partial, Message = "Balance is current. Detailed Web usage was unavailable; refresh or reconnect the selected session." };
        }
    }
}
