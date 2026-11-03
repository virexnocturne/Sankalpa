using Blocker.Core;
using Blocker.Service;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

if (args.Length > 0 && args[0] == "--benchmark")
{
    BenchmarkRunner.Run();
    return 0;
}

const string MutexName = @"Global\BlockerServiceSingletonMutex";
bool hasHandle = false;
Mutex? mutex = null;

try
{
    try
    {
        mutex = new Mutex(true, MutexName, out bool createdNew);
        hasHandle = createdNew;
    }
    catch (AbandonedMutexException)
    {
        hasHandle = true;
    }
    catch
    {
        hasHandle = true;
    }

    if (!hasHandle)
    {
        Console.WriteLine("Another instance of Blocker Service is already running. Exiting.");
        return 1;
    }

    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = "SankalpaService";
        })
        .ConfigureServices(services =>
        {
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
    if (mutex != null)
    {
        if (hasHandle)
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
        mutex.Dispose();
    }
}
