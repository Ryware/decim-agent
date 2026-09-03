using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decim.Agent;

public sealed record LogDirectorySource(string Name, string Path);

public sealed record EventLogSource(string Name, string Channel, string[] AllowedLevels);

public sealed record AgentConfiguration(
    Uri ApiBaseUrl, string ApiKey, Guid TenantId, TimeSpan PollInterval,
    LogDirectorySource[] LogDirectories, EventLogSource[] EventLogs)
{
    private static readonly string[] SupportedLevels = ["critical", "error", "warning", "information", "verbose"];

    public static AgentConfiguration Load(string path)
    {
        ConfigurationDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ConfigurationDocument>(File.ReadAllBytes(path), AgentJson.Options)
                ?? throw new AgentConfigurationException("The configuration file is empty.");
        }
        catch (FileNotFoundException exception)
        {
            throw new AgentConfigurationException($"Configuration file '{path}' was not found.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentConfigurationException("The configuration file cannot be read with the current Windows account.", exception);
        }
        catch (JsonException exception)
        {
            throw new AgentConfigurationException("The configuration file is not valid JSON.", exception);
        }

        if (!Uri.TryCreate(document.ApiBaseUrl, UriKind.Absolute, out var apiBaseUrl)
            || string.IsNullOrEmpty(apiBaseUrl.Host)
            || (apiBaseUrl.Scheme != Uri.UriSchemeHttps && !(apiBaseUrl.Scheme == Uri.UriSchemeHttp && apiBaseUrl.IsLoopback))
            || !string.IsNullOrEmpty(apiBaseUrl.UserInfo)
            || !string.IsNullOrEmpty(apiBaseUrl.Query)
            || !string.IsNullOrEmpty(apiBaseUrl.Fragment))
        {
            throw new AgentConfigurationException("apiBaseUrl must be HTTPS, except that HTTP loopback URLs are allowed for development.");
        }

        if (string.IsNullOrWhiteSpace(document.ApiKey) || document.ApiKey.Length < 16)
        {
            throw new AgentConfigurationException("apiKey must contain the agent key issued by Decim.");
        }

        if (!Guid.TryParse(document.TenantId, out var tenantId) || tenantId == Guid.Empty)
        {
            throw new AgentConfigurationException("tenantId must be a non-empty GUID.");
        }

        var pollIntervalSeconds = document.PollIntervalSeconds ?? 5;
        if (pollIntervalSeconds is < 1 or > 300)
        {
            throw new AgentConfigurationException("pollIntervalSeconds must be between 1 and 300.");
        }

        var directories = (document.LogDirectories ?? []).Select(ValidateDirectory).ToArray();
        EnsureUniqueNames(directories.Select(directory => directory.Name), "log directory");
        var eventLogs = (document.EventLogs ?? []).Select(ValidateEventLog).ToArray();
        EnsureUniqueNames(eventLogs.Select(eventLog => eventLog.Name), "Event Log");
        return new(NormalizeBaseUrl(apiBaseUrl), document.ApiKey, tenantId, TimeSpan.FromSeconds(pollIntervalSeconds), directories, eventLogs);
    }

    private static LogDirectorySource ValidateDirectory(LogDirectoryDocument? directory)
    {
        if (directory is null)
        {
            throw new AgentConfigurationException("Log directory entries must be JSON objects.");
        }

        var name = ValidateName(directory.Name, "Log directory names");
        if (string.IsNullOrWhiteSpace(directory.Path) || !Path.IsPathFullyQualified(directory.Path))
        {
            throw new AgentConfigurationException($"Log directory '{name}' must have an absolute path.");
        }

        try
        {
            return new(name, Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Path)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentConfigurationException($"Log directory '{name}' has an invalid path.", exception);
        }
    }

    private static EventLogSource ValidateEventLog(EventLogDocument? eventLog)
    {
        if (eventLog is null)
        {
            throw new AgentConfigurationException("Event Log entries must be JSON objects.");
        }

        var name = ValidateName(eventLog.Name, "Event Log source names");
        if (string.IsNullOrWhiteSpace(eventLog.Channel) || eventLog.Channel != eventLog.Channel.Trim() || eventLog.Channel.Length > 255)
        {
            throw new AgentConfigurationException($"Event Log source '{name}' must have a channel name containing at most 255 characters.");
        }

        if (eventLog.AllowedLevels is not { Length: > 0 })
        {
            throw new AgentConfigurationException($"Event Log source '{name}' must allow at least one level.");
        }

        var levels = eventLog.AllowedLevels.Select(level => level?.Trim().ToLowerInvariant() ?? string.Empty).ToArray();
        if (levels.Any(level => !SupportedLevels.Contains(level, StringComparer.Ordinal))
            || levels.Distinct(StringComparer.Ordinal).Count() != levels.Length)
        {
            throw new AgentConfigurationException($"Event Log source '{name}' has unsupported or duplicate allowed levels.");
        }

        return new(name, eventLog.Channel, levels);
    }

    private static string ValidateName(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 100)
        {
            throw new AgentConfigurationException($"{description} must be non-empty, trimmed, and at most 100 characters.");
        }

        return value;
    }

    private static void EnsureUniqueNames(IEnumerable<string> names, string description)
    {
        var values = names.ToArray();
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            throw new AgentConfigurationException($"Configured {description} names must be unique.");
        }
    }

    private static Uri NormalizeBaseUrl(Uri value)
    {
        var builder = new UriBuilder(value);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ConfigurationDocument
    {
        public string? ApiBaseUrl { get; init; }
        public string? ApiKey { get; init; }
        public string? TenantId { get; init; }
        public int? PollIntervalSeconds { get; init; }
        public LogDirectoryDocument[]? LogDirectories { get; init; }
        public EventLogDocument[]? EventLogs { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class LogDirectoryDocument
    {
        public string? Name { get; init; }
        public string? Path { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class EventLogDocument
    {
        public string? Name { get; init; }
        public string? Channel { get; init; }
        public string?[]? AllowedLevels { get; init; }
    }
}

public sealed class AgentConfigurationException : Exception
{
    public AgentConfigurationException(string message) : base(message)
    {
    }

    public AgentConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
