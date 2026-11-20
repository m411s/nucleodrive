using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NucleoDrive.Sync;

namespace NucleoDrive.Daemon;

public static class DaemonHost
{
    /// <summary>Builds the daemon host. The caller must already hold the <see cref="DaemonLock"/>.</summary>
    public static IHost Build(string[] args, NucleoDrivePaths paths, DaemonOptions options)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSystemd(); // sd_notify READY/STOPPING and journald-friendly logs when run by systemd
        builder.Logging.SetMinimumLevel(options.MinLogLevel);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SyncWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncWorker>());
        builder.Services.AddSingleton<TwoWaySyncWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TwoWaySyncWorker>());
        builder.Services.AddHostedService<RpcServer>();
        return builder.Build();
    }
}
