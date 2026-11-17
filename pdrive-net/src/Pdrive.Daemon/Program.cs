using Pdrive.Daemon;
using Pdrive.Sync;

var paths = PdrivePaths.FromEnvironment();
try
{
    Pdrive.Ipc.SocketPath.Validate(paths.DaemonSocket);
}
catch (Pdrive.Ipc.DaemonException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

// Config and state hold folder names and what's synced where: keep them private (0700).
PdrivePaths.EnsurePrivateDir(paths.ConfigDir);
PdrivePaths.EnsurePrivateDir(paths.StateDir);
if (File.Exists(paths.ConfigFile))
    File.SetUnixFileMode(paths.ConfigFile, PdrivePaths.PrivateFile);

using var daemonLock = DaemonLock.TryAcquire(paths.DaemonLock);
if (daemonLock is null)
{
    Console.Error.WriteLine($"Another backup daemon is already running (lock: {paths.DaemonLock}).");
    return 1;
}

using var host = DaemonHost.Build(args, paths, new DaemonOptions());
await host.RunAsync();
return Environment.ExitCode;
