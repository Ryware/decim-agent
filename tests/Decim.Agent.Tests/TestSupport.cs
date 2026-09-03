using System.Net;
using System.Text;
using System.Text.Json;

namespace Decim.Agent.Tests;

internal static class TestSupport
{
    internal static AgentConfiguration Configuration(string root, EventLogSource[]? eventLogs = null) => new(
        new Uri("https://decim.test/"),
        "decim_agent_test_key_not_a_secret",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TimeSpan.FromSeconds(5),
        [new LogDirectorySource("logs", root)],
        eventLogs ?? [new EventLogSource("application", "Application", ["error", "warning"])]);

    internal static AgentTask Task(string type, object parameters) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        type,
        JsonSerializer.SerializeToElement(parameters, JsonSerializerOptions.Web),
        DateTimeOffset.UtcNow.AddMinutes(10));

    internal static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"decim-agent-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed record RequestSnapshot(HttpMethod Method, Uri Uri, Dictionary<string, string[]> Headers, string? ContentType, byte[] Body);

internal sealed class StubHttpHandler(params Func<RequestSnapshot, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _index;

    internal List<RequestSnapshot> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentHeaders = request.Content is null ? [] : request.Content.Headers.AsEnumerable();
        var headers = request.Headers.Concat(contentHeaders)
            .ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        var snapshot = new RequestSnapshot(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("Request URI missing."),
            headers,
            request.Content?.Headers.ContentType?.MediaType,
            body);
        Requests.Add(snapshot);
        var index = Interlocked.Increment(ref _index) - 1;
        return responses[Math.Min(index, responses.Length - 1)](snapshot);
    }
}

internal sealed class FakeEventLogSourceReader(EventLogRecordResult[] records) : IEventLogSourceReader
{
    public EventLogSource? Source { get; private set; }
    public string[]? Levels { get; private set; }

    public Task<EventLogRecordResult[]> ReadAsync(
        EventLogSource source, DateTimeOffset fromUtc, DateTimeOffset toUtc, string[] levels, CancellationToken cancellationToken)
    {
        Source = source;
        Levels = levels;
        return Task.FromResult(records);
    }
}
