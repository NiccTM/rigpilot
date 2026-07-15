using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace PCHelper.GameBarWidget;

sealed partial class App : Application
{
    private XboxGameBarWidget? _widget;

    public App()
    {
        InitializeComponent();
        Suspending += OnSuspending;
    }

    protected override void OnActivated(IActivatedEventArgs args)
    {
        XboxGameBarWidgetActivatedEventArgs? widgetArgs = null;
        if (args.Kind == ActivationKind.Protocol
            && args is IProtocolActivatedEventArgs protocolArgs
            && string.Equals(protocolArgs.Uri.Scheme, "ms-gamebarwidget", StringComparison.OrdinalIgnoreCase))
        {
            widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
        }

        if (widgetArgs?.IsLaunchActivation != true)
        {
            return;
        }

        Frame rootFrame = new();
        rootFrame.NavigationFailed += OnNavigationFailed;
        Window.Current.Content = rootFrame;
        _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
        rootFrame.Navigate(typeof(WidgetPage));
        Window.Current.Closed += OnWidgetWindowClosed;
        Window.Current.Activate();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Frame rootFrame = Window.Current.Content as Frame ?? new Frame();
        rootFrame.NavigationFailed += OnNavigationFailed;
        Window.Current.Content = rootFrame;
        if (!args.PrelaunchActivated && rootFrame.Content is null)
        {
            rootFrame.Navigate(typeof(WidgetPage));
        }
        if (!args.PrelaunchActivated)
        {
            Window.Current.Activate();
        }
    }

    private static void OnNavigationFailed(object sender, NavigationFailedEventArgs args) =>
        throw new InvalidOperationException($"Failed to load page {args.SourcePageType.FullName}.");

    private void OnWidgetWindowClosed(object sender, Windows.UI.Core.CoreWindowEventArgs args)
    {
        _widget = null;
        Window.Current.Closed -= OnWidgetWindowClosed;
    }

    private void OnSuspending(object sender, SuspendingEventArgs args)
    {
        SuspendingDeferral deferral = args.SuspendingOperation.GetDeferral();
        _widget = null;
        deferral.Complete();
    }
}
