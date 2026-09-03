using System.Text.Json;

namespace Decim.Agent.Tests;

public sealed class TaskExecutorTests
{
    [Test]
    public async Task DirectoryListingReturnsOnlyImmediateRegularChildrenWithSourceRelativePaths()
    {
        var root = TestSupport.CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "child"));
            Directory.CreateDirectory(Path.Combine(root, "child", "nested"));
            await File.WriteAllBytesAsync(Path.Combine(root, "a.log"), [1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(root, "child", "nested.log"), "not immediate");
            var executor = Executor(root);

            var result = await executor.ExecuteAsync(
                TestSupport.Task(InvestigationTaskContract.DirectoryListType, new DirectoryListParameters("logs", null)), CancellationToken.None);
            var listing = JsonSerializer.Deserialize<DirectoryListingResult>(result.Payload, JsonSerializerOptions.Web);

            await Assert.That(result.Kind).IsEqualTo(InvestigationTaskContract.DirectoryListingResultKind);
            await Assert.That(listing).IsNotNull();
            await Assert.That(listing!.Entries.Select(entry => entry.RelativePath)).IsEquivalentTo(["a.log", "child"]);
            await Assert.That(listing.Entries.Single(entry => entry.RelativePath == "a.log").ByteLength).IsEqualTo(3L);
            await Assert.That(listing.Entries.Single(entry => entry.RelativePath == "child").Kind).IsEqualTo("directory");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task FileReadsSupportEmptyExactLimitRangesAndEofTruncation()
    {
        var root = TestSupport.CreateTemporaryDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "empty.log"), []);
            var exact = Enumerable.Range(0, InvestigationTaskContract.MaximumResultBytes).Select(index => (byte)(index % 251)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "exact.log"), exact);
            var executor = Executor(root);

            var empty = await executor.ExecuteAsync(
                TestSupport.Task(InvestigationTaskContract.FileReadType, new FileReadParameters("logs", "empty.log", null, null)), CancellationToken.None);
            var exactResult = await executor.ExecuteAsync(
                TestSupport.Task(InvestigationTaskContract.FileReadType, new FileReadParameters("logs", "exact.log", null, null)), CancellationToken.None);
            var range = await executor.ExecuteAsync(
                TestSupport.Task(InvestigationTaskContract.FileReadType, new FileReadParameters("logs", "exact.log", 123, 17)), CancellationToken.None);
            var throughEof = await executor.ExecuteAsync(
                TestSupport.Task(InvestigationTaskContract.FileReadType, new FileReadParameters("logs", "exact.log", exact.Length - 4, 50)), CancellationToken.None);

            await Assert.That(empty.Payload).IsEmpty();
            await Assert.That(exactResult.Kind).IsEqualTo(InvestigationTaskContract.FileBytesResultKind);
            await Assert.That(exactResult.Payload.AsSpan().SequenceEqual(exact)).IsTrue();
            await Assert.That(range.Payload.AsSpan().SequenceEqual(exact.AsSpan(123, 17))).IsTrue();
            await Assert.That(throughEof.Payload.AsSpan().SequenceEqual(exact.AsSpan(exact.Length - 4))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task OversizedFilesReturnBoundedRawBase64SamplesWithExactOffsets()
    {
        var root = TestSupport.CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "large.log");
            var bytes = new byte[InvestigationTaskContract.MaximumResultBytes + 1];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            var executor = Executor(root);

            var result = await executor.ExecuteAsync(
                TestSupport.Task(InvestigationTaskContract.FileReadType, new FileReadParameters("logs", "large.log", null, null)), CancellationToken.None);
            var preview = JsonSerializer.Deserialize<FilePreviewResult>(result.Payload, JsonSerializerOptions.Web);

            await Assert.That(result.Kind).IsEqualTo(InvestigationTaskContract.FilePreviewResultKind);
            await Assert.That(result.Payload.Length).IsLessThanOrEqualTo(InvestigationTaskContract.MaximumResultBytes);
            await Assert.That(preview).IsNotNull();
            await Assert.That(preview!.Stride).IsEqualTo(InvestigationTaskContract.InitialSampleStride);
            await Assert.That(preview.RequestedRange).IsEqualTo(new RequestedFileRange(0, bytes.LongLength));
            foreach (var sample in preview.Samples)
            {
                var decoded = Convert.FromBase64String(sample.DataBase64);
                await Assert.That(decoded).IsEquivalentTo(bytes.AsSpan((int)sample.Offset, decoded.Length).ToArray());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task VeryLargeSparseFilesIncreaseStrideAndStayWithinTheUploadLimit()
    {
        var root = TestSupport.CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "sparse.log");
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                stream.SetLength(4L * 1024 * 1024 * 1024);
            }

            var result = await Executor(root).ExecuteAsync(
                TestSupport.Task(InvestigationTaskContract.FileReadType, new FileReadParameters("logs", "sparse.log", null, null)), CancellationToken.None);
            var preview = JsonSerializer.Deserialize<FilePreviewResult>(result.Payload, JsonSerializerOptions.Web);

            await Assert.That(preview).IsNotNull();
            await Assert.That(preview!.Stride).IsGreaterThan(InvestigationTaskContract.InitialSampleStride);
            await Assert.That(preview.Samples.First().Offset).IsEqualTo(0L);
            await Assert.That(preview.Samples.Last().Offset).IsEqualTo(preview.TotalFileLength - InvestigationTaskContract.SampleBytes);
            await Assert.That(result.Payload.Length).IsLessThanOrEqualTo(InvestigationTaskContract.MaximumResultBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task RootedTraversalAlternateStreamAndReparsePathsAreRejected()
    {
        var root = TestSupport.CreateTemporaryDirectory();
        var outside = TestSupport.CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "outside.log"), "outside");
            foreach (var relativePath in new[] { "../outside.log", Path.GetFullPath(Path.Combine(root, "absolute.log")), "file.log:secret" })
            {
                var exception = await Assert.That(async () => await Executor(root).ExecuteAsync(
                    TestSupport.Task(
                        InvestigationTaskContract.FileReadType,
                        new FileReadParameters("logs", relativePath, null, null)),
                    CancellationToken.None)).Throws<TaskExecutionException>();
                await Assert.That(exception!.Code).IsEqualTo("invalid_path");
            }

            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
                var exception = await Assert.That(async () => await Executor(root).ExecuteAsync(
                    TestSupport.Task(
                        InvestigationTaskContract.FileReadType,
                        new FileReadParameters("logs", Path.Combine("link", "outside.log"), null, null)),
                    CancellationToken.None)).Throws<TaskExecutionException>();
                await Assert.That(exception!.Code).IsEqualTo("reparse_point_not_allowed");
            }
            catch (UnauthorizedAccessException)
            {
                // Some Windows test accounts do not have symbolic-link permission; path validation remains covered above.
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task EventLogRequestsUseConfiguredFilteringAndRejectOversizedJsonWithoutPartialRecords()
    {
        var root = TestSupport.CreateTemporaryDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var fake = new FakeEventLogSourceReader(
                [new EventLogRecordResult(42, now, "error", "provider", 7, "machine", "message")]);
            var executor = new TaskExecutor(TestSupport.Configuration(root), fake);
            var task = TestSupport.Task(
                InvestigationTaskContract.EventLogReadType,
                new EventLogReadParameters("application", now.AddMinutes(-1), now.AddMinutes(1), ["error"]));

            var result = await executor.ExecuteAsync(task, CancellationToken.None);
            var payload = JsonSerializer.Deserialize<EventLogReadResult>(result.Payload, JsonSerializerOptions.Web);

            await Assert.That(fake.Source?.Channel).IsEqualTo("Application");
            await Assert.That(fake.Levels).IsEquivalentTo(["error"]);
            await Assert.That(payload!.Records.Single().Message).IsEqualTo("message");

            var oversizedReader = new FakeEventLogSourceReader(
                [new EventLogRecordResult(1, now, "error", "provider", 1, "machine", new string('x', InvestigationTaskContract.MaximumResultBytes))]);
            var oversizedExecutor = new TaskExecutor(TestSupport.Configuration(root), oversizedReader);
            var exception = await Assert.That(async () => await oversizedExecutor.ExecuteAsync(task, CancellationToken.None))
                .Throws<TaskExecutionException>();
            await Assert.That(exception!.Code).IsEqualTo("event_range_too_large");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TaskExecutor Executor(string root) => new(TestSupport.Configuration(root), new FakeEventLogSourceReader([]));
}
