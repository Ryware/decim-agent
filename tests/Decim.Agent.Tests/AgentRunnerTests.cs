using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Decim.Agent.Tests;

public sealed class AgentRunnerTests
{
    [Test]
    public async Task ReportsTaskFailuresAndStopsWhenPollingDelayIsCancelled()
    {
        var root = TestSupport.CreateTemporaryDirectory();
        try
        {
            var task = TestSupport.Task("unsupported.type", new { });
            var handler = new StubHttpHandler(
                _ => new(HttpStatusCode.OK) { Content = JsonContent.Create(task, options: JsonSerializerOptions.Web) },
                _ => new(HttpStatusCode.OK));
            using var httpClient = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var configuration = TestSupport.Configuration(root);
            var runner = new AgentRunner(
                configuration,
                new AgentApiClient(httpClient, configuration),
                new TaskExecutor(configuration, new FakeEventLogSourceReader([])),
                TextWriter.Null,
                (delay, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                });

            await Assert.That(async () => await runner.RunAsync(cancellation.Token)).Throws<OperationCanceledException>();

            await Assert.That(handler.Requests).Count().IsEqualTo(2);
            await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/api/v1/agent/tasks/poll");
            await Assert.That(handler.Requests[1].Uri.AbsolutePath).IsEqualTo($"/api/v1/agent/tasks/{task.Id:D}/fail");
            var failure = JsonSerializer.Deserialize<AgentTaskFailureRequest>(handler.Requests[1].Body, JsonSerializerOptions.Web);
            await Assert.That(failure?.Code).IsEqualTo("unsupported_task_type");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
