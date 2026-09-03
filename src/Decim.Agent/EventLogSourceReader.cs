using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace Decim.Agent;

public interface IEventLogSourceReader
{
    Task<EventLogRecordResult[]> ReadAsync(
        EventLogSource source, DateTimeOffset fromUtc, DateTimeOffset toUtc, string[] levels, CancellationToken cancellationToken);
}

public sealed class WindowsEventLogSourceReader : IEventLogSourceReader
{
    public Task<EventLogRecordResult[]> ReadAsync(
        EventLogSource source, DateTimeOffset fromUtc, DateTimeOffset toUtc, string[] levels, CancellationToken cancellationToken)
    {
        try
        {
            var query = new EventLogQuery(source.Channel, PathType.LogName, CreateXPath(fromUtc, toUtc, levels))
            {
                ReverseDirection = false,
                TolerateQueryErrors = false
            };
            using var reader = new EventLogReader(query);
            var records = new List<EventLogRecordResult>();
            while (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record.TimeCreated is not { } created)
                    {
                        continue;
                    }

                    var timestampUtc = new DateTimeOffset(created).ToUniversalTime();
                    if (timestampUtc < fromUtc || timestampUtc >= toUtc || !TryMapLevel(record.Level, out var level)
                        || !levels.Contains(level, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    records.Add(new(
                        record.RecordId,
                        timestampUtc,
                        level,
                        record.ProviderName ?? string.Empty,
                        record.Id,
                        record.MachineName ?? string.Empty,
                        RenderMessage(record)));
                }
            }

            return Task.FromResult(records.OrderBy(record => record.TimestampUtc).ThenBy(record => record.RecordId).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EventLogException exception)
        {
            throw new TaskExecutionException("event_log_unavailable", "The configured Windows Event Log channel cannot be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new TaskExecutionException("event_log_access_denied", "The Windows account cannot read the configured Event Log channel.", exception);
        }
    }

    private static string CreateXPath(DateTimeOffset fromUtc, DateTimeOffset toUtc, IEnumerable<string> levels)
    {
        var levelExpression = string.Join(" or ", levels.Select(level => $"Level={ToNativeLevel(level)}"));
        return FormattableString.Invariant(
            $"*[System[TimeCreated[@SystemTime >= '{FormatUtc(fromUtc)}' and @SystemTime < '{FormatUtc(toUtc)}'] and ({levelExpression})]]");
    }

    private static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static int ToNativeLevel(string level) => level.ToLowerInvariant() switch
    {
        "critical" => 1,
        "error" => 2,
        "warning" => 3,
        "information" => 4,
        "verbose" => 5,
        _ => throw new TaskExecutionException("invalid_levels", "The requested Event Log level is not supported.")
    };

    private static bool TryMapLevel(byte? level, out string value)
    {
        value = level switch
        {
            1 => "critical",
            2 => "error",
            3 => "warning",
            4 => "information",
            5 => "verbose",
            _ => string.Empty
        };
        return value.Length > 0;
    }

    private static string? RenderMessage(EventRecord record)
    {
        try
        {
            return record.FormatDescription();
        }
        catch (EventLogException)
        {
            return null;
        }
    }
}
