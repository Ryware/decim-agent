using Decim.Agent;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("decim-agent runs on Windows only.");
    return 2;
}

try
{
    var configurationPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    var configuration = AgentConfiguration.Load(configurationPath);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    using var handler = new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };
    using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    var apiClient = new AgentApiClient(httpClient, configuration);
    var executor = new TaskExecutor(configuration, new WindowsEventLogSourceReader());
    var runner = new AgentRunner(configuration, apiClient, executor, Console.Out);
    Console.WriteLine("decim-agent started; outbound polling is active.");
    try
    {
        await runner.RunAsync(cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }

    Console.WriteLine("decim-agent stopped.");
    return 0;
}
catch (AgentConfigurationException exception)
{
    Console.Error.WriteLine($"Configuration error: {exception.Message}");
    return 2;
}
catch (AgentAuthenticationException exception)
{
    Console.Error.WriteLine($"Authentication error: {exception.Message}");
    return 3;
}
catch (AgentProtocolException exception)
{
    Console.Error.WriteLine($"API protocol error: {exception.Message}");
    return 4;
}
