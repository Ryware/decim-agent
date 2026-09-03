using System.Text.Json;

namespace Decim.Agent;

public static class InvestigationTaskContract
{
    public const int MaximumResultBytes = 5 * 1024 * 1024;
    public const int SampleBytes = 2 * 1024;
    public const long InitialSampleStride = 2L * 1024 * 1024;
    public const string ResultKindHeaderName = "X-Decim-Result-Kind";
    public const string DirectoryListType = "directory.list";
    public const string FileReadType = "file.read";
    public const string EventLogReadType = "event-log.read";
    public const string DirectoryListingResultKind = "directory.list";
    public const string FileBytesResultKind = "file.bytes";
    public const string FilePreviewResultKind = "file.preview";
    public const string EventLogRecordsResultKind = "event-log.records";
}

public sealed record AgentDirectorySourceDto(string Name);

public sealed record AgentEventLogSourceDto(string Name, string Channel, string[] Levels);

public sealed record AgentPollRequest(
    string Hostname, string OperatingSystemVersion, string AgentVersion,
    AgentDirectorySourceDto[] DirectorySources, AgentEventLogSourceDto[] EventLogSources);

public sealed record AgentTask(Guid Id, Guid IncidentId, string Type, JsonElement Parameters, DateTimeOffset LeaseExpiresAtUtc);

public sealed record DirectoryListParameters(string Source, string? RelativeDirectory);

public sealed record FileReadParameters(string Source, string RelativePath, long? Offset, long? Length);

public sealed record EventLogReadParameters(string Source, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string[]? Levels);

public sealed record AgentTaskFailureRequest(string Code, string? Message);

public sealed record TaskExecutionResult(byte[] Payload, string ContentType, string Kind);

public sealed record DirectoryListingResult(string Source, string RelativeDirectory, DirectoryEntryResult[] Entries);

public sealed record DirectoryEntryResult(string RelativePath, string Kind, long? ByteLength, DateTimeOffset LastWriteUtc);

public sealed record FilePreviewResult(
    string Source, string RelativePath, long TotalFileLength, RequestedFileRange RequestedRange,
    int SampleSize, long Stride, FileSample[] Samples);

public sealed record RequestedFileRange(long Offset, long Length);

public sealed record FileSample(long Offset, string DataBase64);

public sealed record EventLogReadResult(
    string Source, DateTimeOffset FromUtc, DateTimeOffset ToUtc, EventLogRecordResult[] Records);

public sealed record EventLogRecordResult(
    long? RecordId, DateTimeOffset TimestampUtc, string Level, string Provider, int EventId, string Machine, string? Message);

internal static class AgentJson
{
    internal static JsonSerializerOptions Options => JsonSerializerOptions.Web;
}

public sealed class AgentAuthenticationException(string message) : Exception(message);

public sealed class AgentProtocolException(string message) : Exception(message);

public sealed class TaskExecutionException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
