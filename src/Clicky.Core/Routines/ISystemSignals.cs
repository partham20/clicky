using System.Net.NetworkInformation;

namespace Clicky.Core.Routines;

/// <summary>What the PC is doing that decides when routines can run: network, sleep and the clock.</summary>
/// <remarks>Events may be raised on any thread.</remarks>
public interface ISystemSignals
{
    /// <summary>True when the PC has internet access (as far as the OS can tell).</summary>
    bool IsOnline { get; }

    event EventHandler? NetworkChanged;

    /// <summary>The PC is about to sleep or hibernate.</summary>
    event EventHandler? Suspending;

    /// <summary>The PC woke up.</summary>
    event EventHandler? Resumed;

    /// <summary>The clock or time zone was changed.</summary>
    event EventHandler? TimeChanged;
}

/// <summary>
/// Portable signals from System.Net.NetworkInformation: network only, no sleep or clock events (the
/// engine's timer cap still notices both, just later). Windows uses its own implementation.
/// </summary>
public sealed class PortableSystemSignals : ISystemSignals, IDisposable
{
    public PortableSystemSignals()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public bool IsOnline => NetworkInterface.GetIsNetworkAvailable();

    public event EventHandler? NetworkChanged;

    public event EventHandler? Suspending
    {
        add { }
        remove { }
    }

    public event EventHandler? Resumed
    {
        add { }
        remove { }
    }

    public event EventHandler? TimeChanged
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => NetworkChanged?.Invoke(this, EventArgs.Empty);

    private void OnAddressChanged(object? sender, EventArgs e) => NetworkChanged?.Invoke(this, EventArgs.Empty);
}
