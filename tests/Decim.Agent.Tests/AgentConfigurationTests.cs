using System.Text.Json;

namespace Decim.Agent.Tests;

public sealed class AgentConfigurationTests
{
    private static readonly string[] ValidLevels = ["Error", "Warning"];

    [Test]
    public async Task ValidConfigurationDefaultsToFiveSecondsAndNormalizesValues()
    {
        var directory = TestSupport.CreateTemporaryDirectory();
        var configurationPath = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(new
            {
                ApiBaseUrl = "http://localhost:5080",
                ApiKey = "decim_agent_test_key_not_a_secret",
                TenantId = "11111111-1111-1111-1111-111111111111",
                LogDirectories = new[] { new { Name = "logs", Path = directory } },
                EventLogs = new[] { new { Name = "app", Channel = "Application", AllowedLevels = ValidLevels } }
            }, JsonSerializerOptions.Web));

            var configuration = AgentConfiguration.Load(configurationPath);

            await Assert.That(configuration.ApiBaseUrl.AbsoluteUri).IsEqualTo("http://localhost:5080/");
            await Assert.That(configuration.PollInterval).IsEqualTo(TimeSpan.FromSeconds(5));
            await Assert.That(configuration.LogDirectories.Single().Path).IsEqualTo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
            await Assert.That(configuration.EventLogs.Single().AllowedLevels).IsEquivalentTo(["error", "warning"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RejectsInsecureRemoteUrlsMissingCredentialsRelativePathsAndDuplicateNames()
    {
        var directory = TestSupport.CreateTemporaryDirectory();
        try
        {
            foreach (var document in new object[]
            {
                Document(directory, apiBaseUrl: "http://example.com"),
                Document(directory, apiKey: ""),
                Document(directory, tenantId: Guid.Empty.ToString()),
                Document("relative"),
                Document(directory, duplicateDirectories: true),
                Document(directory, duplicateEventLogs: true)
            })
            {
                var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, JsonSerializerOptions.Web));
                await Assert.That(() => AgentConfiguration.Load(path)).Throws<AgentConfigurationException>();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static object Document(
        string path, string apiBaseUrl = "https://decim.test", string apiKey = "decim_agent_test_key_not_a_secret",
        string tenantId = "11111111-1111-1111-1111-111111111111", bool duplicateDirectories = false, bool duplicateEventLogs = false) => new
        {
            ApiBaseUrl = apiBaseUrl,
            ApiKey = apiKey,
            TenantId = tenantId,
            LogDirectories = duplicateDirectories
            ? new[] { new { Name = "logs", Path = path }, new { Name = "LOGS", Path = path } }
            : new[] { new { Name = "logs", Path = path } },
            EventLogs = duplicateEventLogs
            ? new[]
            {
                new { Name = "app", Channel = "Application", AllowedLevels = new[] { "error" } },
                new { Name = "APP", Channel = "System", AllowedLevels = new[] { "warning" } }
            }
            : [new { Name = "app", Channel = "Application", AllowedLevels = new[] { "error" } }]
        };
}
