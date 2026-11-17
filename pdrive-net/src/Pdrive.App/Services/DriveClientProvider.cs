using Pdrive.Cli;

namespace Pdrive.App.Services;

/// <summary>The current Drive client, created on first use and again after the CLI is installed
/// from Settings (so the app needn't be restarted).</summary>
public sealed class DriveClientProvider(Func<IDriveClient> factory)
{
    private IDriveClient? _client;
    private string? _error;
    private bool _tried;

    public IDriveClient? Client
    {
        get
        {
            if (!_tried)
                Create();
            return _client;
        }
    }

    /// <summary>Why there is no client (e.g. "proton-drive CLI not found…").</summary>
    public string? Error
    {
        get
        {
            if (!_tried)
                Create();
            return _error;
        }
    }

    /// <summary>Raised after <see cref="Reset"/>.</summary>
    public event Action? Changed;

    /// <summary>Looks for the CLI again (after installing or updating it).</summary>
    public void Reset()
    {
        Create();
        Changed?.Invoke();
    }

    private void Create()
    {
        _tried = true;
        try
        {
            _client = factory();
            _error = null;
        }
        catch (DriveException e)
        {
            _client = null;
            _error = e.Message;
        }
    }
}
