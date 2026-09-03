using System.Reflection;

namespace Decim.Agent;

public sealed class AgentRunner(
    AgentConfiguration configuration, AgentApiClient apiClient, TaskExecutor taskExecutor,
    TextWriter output, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var heartbeat = CreateHeartbeat(configuration);
        while (!cancellationToken.IsCancellationRequested)
        {
            var task = await apiClient.PollAsync(heartbeat, cancellationToken);
            if (task is not null)
            {
                await ProcessAsync(task, cancellationToken);
            }

            await _delay(configuration.PollInterval, cancellationToken);
        }
    }

    private async Task ProcessAsync(AgentTask task, CancellationToken cancellationToken)
    {
        try
        {
            var result = await taskExecutor.ExecuteAsync(task, cancellationToken);
            await apiClient.SubmitResultAsync(task.Id, result, cancellationToken);
            await output.WriteLineAsync($"{DateTimeOffset.UtcNow:O} completed task {task.Id:D}");
        }
        catch (TaskExecutionException exception)
        {
            await ReportFailureAsync(task.Id, exception.Code, exception.Message, cancellationToken);
        }
        catch (AgentProtocolException exception)
        {
            await output.WriteLineAsync($"{DateTimeOffset.UtcNow:O} task {task.Id:D} was rejected by the API: {exception.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(task.Id, "execution_failed", $"Task execution failed with {exception.GetType().Name}.", cancellationToken);
        }
    }

    private async Task ReportFailureAsync(Guid taskId, string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            await apiClient.ReportFailureAsync(taskId, code, message, cancellationToken);
            await output.WriteLineAsync($"{DateTimeOffset.UtcNow:O} failed task {taskId:D} with {code}");
        }
        catch (AgentProtocolException exception)
        {
            await output.WriteLineAsync($"{DateTimeOffset.UtcNow:O} failure for task {taskId:D} was rejected by the API: {exception.Message}");
        }
    }

    private static AgentPollRequest CreateHeartbeat(AgentConfiguration value) => new(
        Environment.MachineName,
        Environment.OSVersion.VersionString,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        value.LogDirectories.Select(source => new AgentDirectorySourceDto(source.Name)).ToArray(),
        value.EventLogs.Select(source => new AgentEventLogSourceDto(source.Name, source.Channel, source.AllowedLevels)).ToArray());
}
