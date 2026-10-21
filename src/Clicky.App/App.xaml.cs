using System.Windows;

namespace Clicky.App;

/// <summary>
/// Tray-resident: no StartupUri and OnExplicitShutdown, so closing the last window never exits.
/// Lifetime is driven by <see cref="Program"/>.
/// </summary>
public partial class App : Application;
