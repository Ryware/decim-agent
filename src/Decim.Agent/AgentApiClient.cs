using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Decim.Agent;

public sealed class AgentApiClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AgentApiClient(HttpClient httpClient, AgentConfiguration configuration, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = configuration.ApiBaseUrl;
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", configuration.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("X-Tenant-ID", configuration.TenantId.ToString("D"));
        _delay = delay ?? Task.Delay;
    }

    public async Task<AgentTask?> PollAsync(AgentPollRequest heartbeat, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/v1/agent/tasks/poll") { Content = JsonContent.Create(heartbeat, options: AgentJson.Options) },
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        ThrowForPermanentError(response, badRequestIsConfigurationError: true);
        try
        {
            return await response.Content.ReadFromJsonAsync<AgentTask>(AgentJson.Options, cancellationToken)
                ?? throw new AgentProtocolException("The poll response did not contain a task.");
        }
        catch (JsonException exception)
        {
            throw new AgentProtocolException($"The poll response was not valid task JSON: {exception.Message}");
        }
    }

    public async Task SubmitResultAsync(Guid taskId, TaskExecutionResult result, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var content = new ByteArrayContent(result.Payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(result.ContentType);
            var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/agent/tasks/{taskId:D}/result") { Content = content };
            request.Headers.Add(InvestigationTaskContract.ResultKindHeaderName, result.Kind);
            return request;
        }, cancellationToken);
        ThrowForPermanentError(response, badRequestIsConfigurationError: false);
    }

    public async Task ReportFailureAsync(Guid taskId, string code, string? message, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"api/v1/agent/tasks/{taskId:D}/fail")
            {
                Content = JsonContent.Create(new AgentTaskFailureRequest(code, message), options: AgentJson.Options)
            },
            cancellationToken);
        ThrowForPermanentError(response, badRequestIsConfigurationError: false);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!IsTransient(response.StatusCode))
                {
                    return response;
                }
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            var retryDelay = GetRetryDelay(response, backoff);
            response?.Dispose();
            await _delay(retryDelay, cancellationToken);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage? response, TimeSpan fallback)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : fallback);
        return requested <= TimeSpan.Zero ? fallback : TimeSpan.FromSeconds(Math.Min(requested.TotalSeconds, 60));
    }

    private static void ThrowForPermanentError(HttpResponseMessage response, bool badRequestIsConfigurationError)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AgentAuthenticationException("The API rejected the configured agent credentials.");
        }

        if (badRequestIsConfigurationError && response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new AgentConfigurationException("The API rejected the agent heartbeat or advertised capabilities.");
        }

        throw new AgentProtocolException($"The API rejected the request with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
    }
}
