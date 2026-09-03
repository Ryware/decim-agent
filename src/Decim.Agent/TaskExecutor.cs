using System.Text.Json;

namespace Decim.Agent;

public sealed class TaskExecutor(AgentConfiguration configuration, IEventLogSourceReader eventLogReader)
{
    public async Task<TaskExecutionResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken) => task.Type switch
    {
        InvestigationTaskContract.DirectoryListType => ExecuteDirectoryList(Deserialize<DirectoryListParameters>(task.Parameters)),
        InvestigationTaskContract.FileReadType => await ExecuteFileReadAsync(Deserialize<FileReadParameters>(task.Parameters), cancellationToken),
        InvestigationTaskContract.EventLogReadType => await ExecuteEventLogReadAsync(Deserialize<EventLogReadParameters>(task.Parameters), cancellationToken),
        _ => throw new TaskExecutionException("unsupported_task_type", "The task type is not supported by this agent version.")
    };

    private TaskExecutionResult ExecuteDirectoryList(DirectoryListParameters parameters)
    {
        var source = FindDirectory(parameters.Source);
        var directory = SecurePathResolver.ResolveDirectory(source, parameters.RelativeDirectory);
        var entries = new List<DirectoryEntryResult>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add(new(
                    NormalizeRelativePath(Path.GetRelativePath(source.Path, path)),
                    isDirectory ? "directory" : "file",
                    isDirectory ? null : new FileInfo(path).Length,
                    new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TaskExecutionException("directory_unavailable", "The requested directory cannot be listed.", exception);
        }

        var relativeDirectory = string.IsNullOrEmpty(parameters.RelativeDirectory)
            ? string.Empty
            : NormalizeRelativePath(Path.GetRelativePath(source.Path, directory));
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new DirectoryListingResult(source.Name, relativeDirectory, entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray()),
            AgentJson.Options);
        EnsurePayloadLimit(payload, "directory_too_large", "The directory listing exceeds the result limit.");
        return new(payload, "application/json", InvestigationTaskContract.DirectoryListingResultKind);
    }

    private async Task<TaskExecutionResult> ExecuteFileReadAsync(FileReadParameters parameters, CancellationToken cancellationToken)
    {
        if (parameters.Offset.HasValue != parameters.Length.HasValue || parameters.Offset < 0 || parameters.Length <= 0)
        {
            throw new TaskExecutionException("invalid_range", "Offset and positive length must be supplied together.");
        }

        var source = FindDirectory(parameters.Source);
        var path = SecurePathResolver.ResolveFile(source, parameters.RelativePath);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var totalLength = stream.Length;
            var offset = parameters.Offset ?? 0;
            if (offset > totalLength)
            {
                throw new TaskExecutionException("range_not_satisfiable", "The requested file offset is beyond end of file.");
            }

            var requestedLength = parameters.Length ?? totalLength;
            var effectiveLength = Math.Min(requestedLength, totalLength - offset);
            if (effectiveLength <= InvestigationTaskContract.MaximumResultBytes)
            {
                var bytes = new byte[(int)effectiveLength];
                stream.Position = offset;
                await ReadExactlyAsync(stream, bytes, cancellationToken);
                return new(bytes, "application/octet-stream", InvestigationTaskContract.FileBytesResultKind);
            }

            var preview = await CreatePreviewAsync(
                stream, source.Name, NormalizeRelativePath(parameters.RelativePath), totalLength, offset, effectiveLength, cancellationToken);
            return new(preview, "application/json", InvestigationTaskContract.FilePreviewResultKind);
        }
        catch (TaskExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TaskExecutionException("file_unavailable", "The requested file cannot be read.", exception);
        }
    }

    private async Task<TaskExecutionResult> ExecuteEventLogReadAsync(EventLogReadParameters parameters, CancellationToken cancellationToken)
    {
        var source = configuration.EventLogs.SingleOrDefault(item => string.Equals(item.Name, parameters.Source, StringComparison.Ordinal))
            ?? throw new TaskExecutionException("unknown_source", "The requested Event Log source is not configured.");
        if (parameters.FromUtc >= parameters.ToUtc)
        {
            throw new TaskExecutionException("invalid_range", "The Event Log range must be half-open with fromUtc before toUtc.");
        }

        var levels = parameters.Levels ?? source.AllowedLevels;
        if (levels.Length == 0 || levels.Any(level => !source.AllowedLevels.Contains(level, StringComparer.OrdinalIgnoreCase)))
        {
            throw new TaskExecutionException("invalid_levels", "Requested Event Log levels must be a subset of the configured levels.");
        }

        var fromUtc = parameters.FromUtc.ToUniversalTime();
        var toUtc = parameters.ToUtc.ToUniversalTime();
        var records = await eventLogReader.ReadAsync(source, fromUtc, toUtc, levels, cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new EventLogReadResult(source.Name, fromUtc, toUtc, records), AgentJson.Options);
        EnsurePayloadLimit(payload, "event_range_too_large", "The Event Log range exceeds the result limit; request a narrower UTC range.");
        return new(payload, "application/json", InvestigationTaskContract.EventLogRecordsResultKind);
    }

    private static async Task<byte[]> CreatePreviewAsync(
        FileStream stream, string source, string relativePath, long totalLength, long offset, long length, CancellationToken cancellationToken)
    {
        const int sampleBudget = 1700;
        var stride = Math.Max(InvestigationTaskContract.InitialSampleStride, DivideRoundingUp(length, sampleBudget));
        while (true)
        {
            var samples = await ReadSamplesAsync(stream, offset, length, stride, cancellationToken);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new FilePreviewResult(
                    source,
                    relativePath,
                    totalLength,
                    new RequestedFileRange(offset, length),
                    InvestigationTaskContract.SampleBytes,
                    stride,
                    samples),
                AgentJson.Options);
            if (payload.Length <= InvestigationTaskContract.MaximumResultBytes)
            {
                return payload;
            }

            stride = checked(stride * 2);
        }
    }

    private static async Task<FileSample[]> ReadSamplesAsync(
        FileStream stream, long offset, long length, long stride, CancellationToken cancellationToken)
    {
        var end = checked(offset + length);
        var offsets = new List<long>();
        for (var sampleOffset = offset; sampleOffset < end; sampleOffset = checked(sampleOffset + stride))
        {
            offsets.Add(sampleOffset);
            if (sampleOffset > long.MaxValue - stride)
            {
                break;
            }
        }

        var finalOffset = Math.Max(offset, end - InvestigationTaskContract.SampleBytes);
        if (offsets.Count == 0 || offsets[^1] != finalOffset)
        {
            offsets.Add(finalOffset);
        }

        var samples = new FileSample[offsets.Count];
        for (var index = 0; index < offsets.Count; index++)
        {
            var sampleOffset = offsets[index];
            var size = (int)Math.Min(InvestigationTaskContract.SampleBytes, end - sampleOffset);
            var buffer = new byte[size];
            stream.Position = sampleOffset;
            await ReadExactlyAsync(stream, buffer, cancellationToken);
            samples[index] = new(sampleOffset, Convert.ToBase64String(buffer));
        }

        return samples;
    }

    private LogDirectorySource FindDirectory(string name) =>
        configuration.LogDirectories.SingleOrDefault(source => string.Equals(source.Name, name, StringComparison.Ordinal))
        ?? throw new TaskExecutionException("unknown_source", "The requested directory source is not configured.");

    private static T Deserialize<T>(JsonElement value)
    {
        try
        {
            return value.Deserialize<T>(AgentJson.Options) ?? throw new TaskExecutionException("invalid_task", "Task parameters are missing.");
        }
        catch (JsonException exception)
        {
            throw new TaskExecutionException("invalid_task", "Task parameters are invalid.", exception);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new TaskExecutionException("file_changed", "The file changed while it was being read.", exception);
        }
    }

    private static long DivideRoundingUp(long value, int divisor) => value / divisor + (value % divisor == 0 ? 0 : 1);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void EnsurePayloadLimit(byte[] payload, string code, string message)
    {
        if (payload.Length > InvestigationTaskContract.MaximumResultBytes)
        {
            throw new TaskExecutionException(code, message);
        }
    }
}
