using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Clicky.App.Annotations;
using Clicky.App.Ink;
using Clicky.App.Walkthroughs;
using Clicky.Core.Buddy;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace Clicky.App.Tray;

/// <summary>Owns the notification-area icon. Create, show and dispose on the UI thread.</summary>
public sealed class TrayIconService(TrayViewModel viewModel, AnnotationDemo annotationDemo, InkTrailDemo inkTrailDemo, WalkthroughDemo walkthroughDemo) : IDisposable
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

    /// <summary>A Windows notification from the tray icon. UI thread; does nothing before <see cref="Show"/>.</summary>
    public void Notify(string title, string message, bool warning)
    {
        _icon?.ShowNotification(title, message, warning ? NotificationIcon.Warning : NotificationIcon.Info);
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
        menu.Items.Add(new MenuItem { Header = "New conversation", Command = viewModel.NewConversationCommand, ToolTip = "Clicky forgets what you talked about so far" });
        menu.Items.Add(new Separator());

        var startWithWindows = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        startWithWindows.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(TrayViewModel.StartWithWindows)) { Mode = BindingMode.TwoWay });
        menu.Items.Add(startWithWindows);

        var hideFloatingCursor = new MenuItem { Header = "Hide floating cursor", IsCheckable = true };
        hideFloatingCursor.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(TrayViewModel.HideFloatingCursor)) { Mode = BindingMode.TwoWay });
        menu.Items.Add(hideFloatingCursor);

        var quietMode = new MenuItem { Header = "Quiet mode (show replies as text)", IsCheckable = true };
        quietMode.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(TrayViewModel.QuietMode)) { Mode = BindingMode.TwoWay });
        menu.Items.Add(quietMode);

        menu.Items.Add(BuildBuddyStatePreviewMenu());

        // Preview: shows each kind of mark on the monitor under the pointer.
        var drawingDemo = new MenuItem { Header = "Drawing demo (preview)" };
        drawingDemo.Click += (_, _) => annotationDemo.Play();
        menu.Items.Add(drawingDemo);

        // Until the talk loop (M6) records the pointer during Talk, this runs the same steps without the voice.
        var inkTest = new MenuItem { Header = "Ink trail test (preview)" };
        inkTest.Click += (_, _) => inkTrailDemo.Play();
        menu.Items.Add(inkTest);

        // Scripted steps in a practice window: checks click, keyboard and off-track detection without Claude.
        var walkthroughDemoItem = new MenuItem { Header = "Walkthrough demo (preview)" };
        walkthroughDemoItem.Click += (_, _) => walkthroughDemo.Play();
        menu.Items.Add(walkthroughDemoItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Quit Clicky", Command = viewModel.QuitCommand });
        return menu;
    }

    // Preview: shows each buddy state without having to talk.
    private MenuItem BuildBuddyStatePreviewMenu()
    {
        var parent = new MenuItem { Header = "Buddy state (preview)" };
        foreach (var state in Enum.GetValues<BuddyState>())
        {
            parent.Items.Add(new MenuItem
            {
                Header = state.ToString(),
                Command = viewModel.SetBuddyStateCommand,
                CommandParameter = state,
                Tag = state,
            });
        }

        parent.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem item in parent.Items)
            {
                item.IsChecked = Equals(item.Tag, viewModel.BuddyState);
            }
        };

        return parent;
    }
}
