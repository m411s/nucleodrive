using NucleoDrive.Daemon;
using NucleoDrive.Sync;

foreach (var dir in NucleoDrivePaths.MigrateFromPdrive()) // renamed from pdrive after 0.2.1
    Console.WriteLine($"Moved {dir} to its new name (nucleodrive)");
var paths = NucleoDrivePaths.FromEnvironment();
try
{
    NucleoDrive.Ipc.SocketPath.Validate(paths.DaemonSocket);
}
catch (NucleoDrive.Ipc.DaemonException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

// Config and state hold folder names and what's synced where: keep them private (0700).
NucleoDrivePaths.EnsurePrivateDir(paths.ConfigDir);
NucleoDrivePaths.EnsurePrivateDir(paths.StateDir);
if (File.Exists(paths.ConfigFile))
    File.SetUnixFileMode(paths.ConfigFile, NucleoDrivePaths.PrivateFile);

using var daemonLock = DaemonLock.TryAcquire(paths.DaemonLock);
if (daemonLock is null)
{
    Console.Error.WriteLine($"Another backup daemon is already running (lock: {paths.DaemonLock}).");
    return 1;
}

using var host = DaemonHost.Build(args, paths, new DaemonOptions());
await host.RunAsync();
return Environment.ExitCode;
