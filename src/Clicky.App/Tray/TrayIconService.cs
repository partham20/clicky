using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace Clicky.App.Tray;

/// <summary>Owns the notification-area icon. Create, show and dispose on the UI thread.</summary>
public sealed class TrayIconService(TrayViewModel viewModel) : IDisposable
{
    private TaskbarIcon? _icon;

    public void Show()
    {
        if (_icon is not null)
        {
            return;
        }

        var icon = new TaskbarIcon
        {
            ToolTipText = "Clicky",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/clicky.ico", UriKind.Absolute)),
            ContextMenu = BuildMenu(),
            MenuActivation = PopupActivationMode.RightClick,
            DoubleClickCommand = viewModel.OpenHomeCommand,
            NoLeftClickDelay = true,
            DataContext = viewModel,
        };

        // The icon is not in a visual tree, so it must be created explicitly. Efficiency mode is off:
        // it puts the whole process on EcoQoS, which would throttle the cursor overlay (M1) and audio.
        icon.ForceCreate(enablesEfficiencyMode: false);
        _icon = icon;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
        viewModel.Dispose();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { DataContext = viewModel };

        menu.Items.Add(new MenuItem
        {
            Header = "Open Home",
            Command = viewModel.OpenHomeCommand,
            FontWeight = FontWeights.SemiBold,
        });
        menu.Items.Add(new MenuItem { Header = "Settings…", Command = viewModel.OpenSettingsCommand });
        menu.Items.Add(new Separator());

        var startWithWindows = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        startWithWindows.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(TrayViewModel.StartWithWindows)) { Mode = BindingMode.TwoWay });
        menu.Items.Add(startWithWindows);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Quit Clicky", Command = viewModel.QuitCommand });
        return menu;
    }
}
