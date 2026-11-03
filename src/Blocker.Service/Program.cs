using Blocker.Core;
using Blocker.Service;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run();
    return 0;
}

const string MutexName = @"Global\BlockerServiceSingletonMutex";

var mutexManager = new ServiceMutexManager(
    MutexName,
    logWarning: msg => Console.Error.WriteLine($"[WARN] {msg}"),
    logError: msg => Console.Error.WriteLine($"[ERROR] {msg}"));

try
{
    var acquisition = mutexManager.TryAcquire();
    if (!acquisition.IsAcquired)
    {
        if (acquisition.Status == MutexAcquisitionStatus.AlreadyRunning)
        {
            Console.WriteLine("Another instance of Blocker Service is already running. Exiting.");
        }
        else
        {
            Console.Error.WriteLine($"Blocker Service cannot start: {acquisition.Message}");
        }
        return 1;
    }

    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "SankalpaService";
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton<ISingletonMutex>(mutexManager);
            services.AddSingleton<BlockerDatabase>();
            services.AddSingleton<IBlockStore>(sp => sp.GetRequiredService<BlockerDatabase>());
            services.AddHostedService<Worker>();
            services.AddHostedService<PipeServer>();
        })
        .Build();

    await host.RunAsync();
    return 0;
}
finally
{
    mutexManager.Dispose();
}
