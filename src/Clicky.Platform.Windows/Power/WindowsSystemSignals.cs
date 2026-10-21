using Clicky.Core.Routines;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace Clicky.Platform.Windows.Power;

/// <summary>
/// Network, sleep and clock signals for routines. Internet access comes from Windows' own
/// connectivity check (the same one behind the tray's "No internet"), not
/// NetworkInterface.GetIsNetworkAvailable, which says "up" for any adapter, including Hyper-V and
/// VPN virtual ones with no route out.
/// </summary>
/// <remarks>
/// Corporate networks often block the connectivity probe, so Windows reports local access only
/// while the internet works fine through the proxy; a domain-authenticated network counts as
/// online for that reason. Sleep and clock changes come from SystemEvents (WM_POWERBROADCAST and
/// WM_TIMECHANGE on its own hidden window); on Modern Standby PCs the suspend notice can arrive late
/// or not at all, which the engine's timer cap covers.
/// </remarks>
public sealed class WindowsSystemSignals : ISystemSignals, IDisposable
{
    private readonly ILogger<WindowsSystemSignals> _logger;
    private volatile bool _online;

    public WindowsSystemSignals(ILogger<WindowsSystemSignals> logger)
    {
        _logger = logger;
        _online = ReadOnline();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
    }

    public bool IsOnline => _online;

    public event EventHandler? NetworkChanged;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public event EventHandler? TimeChanged;

    public void Dispose()
    {
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
    }

    private bool ReadOnline()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return false;
            }

            return profile.GetNetworkConnectivityLevel() switch
            {
                NetworkConnectivityLevel.InternetAccess => true,

                // A captive portal or a probe the network blocks: worth trying; a real failure is retried.
                NetworkConnectivityLevel.ConstrainedInternetAccess => true,
                NetworkConnectivityLevel.LocalAccess => profile.GetDomainConnectivityLevel() == DomainConnectivityLevel.Authenticated,
                _ => false,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail open: a routine that tries and fails is better than one that waits forever.
            _logger.LogWarning(ex, "Could not read network connectivity; assuming online");
            return true;
        }
    }

    // Raised on a thread-pool thread, often several times per change.
    private void OnNetworkStatusChanged(object sender)
    {
        var online = ReadOnline();
        if (online != _online)
        {
            _logger.LogInformation("Network: {State}", online ? "online" : "offline");
        }

        _online = online;
        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _logger.LogInformation("PC is going to sleep");
                Suspending?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.Resume:
                _logger.LogInformation("PC woke up");
                _online = ReadOnline();
                Resumed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => TimeChanged?.Invoke(this, EventArgs.Empty);
}
