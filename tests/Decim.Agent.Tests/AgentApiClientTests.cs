using System.Net;

namespace Decim.Agent.Tests;

public sealed class AgentApiClientTests
{
    [Test]
    public async Task PollAddsAuthenticationHeadersAndRetriesTransientResponses()
    {
        var directory = TestSupport.CreateTemporaryDirectory();
        try
        {
            var handler = new StubHttpHandler(
                _ => new(HttpStatusCode.ServiceUnavailable),
                _ => new(HttpStatusCode.NoContent));
            using var httpClient = new HttpClient(handler);
            var delays = new List<TimeSpan>();
            var configuration = TestSupport.Configuration(directory);
            var client = new AgentApiClient(httpClient, configuration, (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

            var task = await client.PollAsync(Heartbeat(), CancellationToken.None);

            await Assert.That(task).IsNull();
            await Assert.That(handler.Requests).Count().IsEqualTo(2);
            await Assert.That(handler.Requests.All(request => request.Headers["X-Api-Key"].Single() == configuration.ApiKey)).IsTrue();
            await Assert.That(handler.Requests.All(request => request.Headers["X-Tenant-ID"].Single() == configuration.TenantId.ToString("D"))).IsTrue();
            await Assert.That(delays).IsEquivalentTo([TimeSpan.FromSeconds(1)]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ResultUploadsOneRawBodyWithItsKindAndContentType()
    {
        var directory = TestSupport.CreateTemporaryDirectory();
        try
        {
            var handler = new StubHttpHandler(_ => new(HttpStatusCode.OK));
            using var httpClient = new HttpClient(handler);
            var client = new AgentApiClient(httpClient, TestSupport.Configuration(directory));
            var taskId = Guid.NewGuid();
            var bytes = new byte[] { 0, 1, 2, 255 };

            await client.SubmitResultAsync(
                taskId, new TaskExecutionResult(bytes, "application/octet-stream", InvestigationTaskContract.FileBytesResultKind), CancellationToken.None);

            var request = handler.Requests.Single();
            await Assert.That(request.Uri.AbsolutePath).IsEqualTo($"/api/v1/agent/tasks/{taskId:D}/result");
            await Assert.That(request.ContentType).IsEqualTo("application/octet-stream");
            await Assert.That(request.Headers[InvestigationTaskContract.ResultKindHeaderName].Single())
                .IsEqualTo(InvestigationTaskContract.FileBytesResultKind);
            await Assert.That(request.Body).IsEquivalentTo(bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AuthenticationRejectionIsFatalAndNotRetried()
    {
        var directory = TestSupport.CreateTemporaryDirectory();
        try
        {
            var handler = new StubHttpHandler(_ => new(HttpStatusCode.Unauthorized));
            using var httpClient = new HttpClient(handler);
            var client = new AgentApiClient(httpClient, TestSupport.Configuration(directory), (_, _) => Task.CompletedTask);

            await Assert.That(async () => await client.PollAsync(Heartbeat(), CancellationToken.None)).Throws<AgentAuthenticationException>();
            await Assert.That(handler.Requests).Count().IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AgentPollRequest Heartbeat() => new("host", "Windows", "1.0.0", [new("logs")], [new("app", "Application", ["error"])]);
}
