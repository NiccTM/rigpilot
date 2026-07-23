using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.UiSnapshot;

internal static class Program
{
    private const int DefaultRenderWidth = 1240;
    private const int DefaultRenderHeight = 800;

    private static readonly string[] PageNames =
    [
        "overview",
        "profiles",
        "cooling",
        "performance",
        "lighting",
        "automation",
        "games-tools",
        "devices",
        "diagnostics"
    ];

    private static readonly string[] PageElementNames =
    [
        "OverviewPage",
        "ProfilesPage",
        "CoolingPage",
        "PerformancePage",
        "LightingPage",
        "AutomationPage",
        "EcosystemPage",
        "DevicesPage",
        "DiagnosticsPage"
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault()?.Equals("--smoke", StringComparison.OrdinalIgnoreCase) == true)
        {
            string reportPath = Path.GetFullPath(args.ElementAtOrDefault(1)
                ?? Path.Combine(AppContext.BaseDirectory, "ui-smoke.json"));
            return RunAutomationSmoke(reportPath);
        }

        string outputDirectory = Path.GetFullPath(args.FirstOrDefault()
            ?? Path.Combine(AppContext.BaseDirectory, "ui-snapshots"));
        Directory.CreateDirectory(outputDirectory);
        int pageIndex = args.Length > 1
            ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        if (pageIndex is < 0 or >= 9)
        {
            Console.Error.WriteLine("Page index must be between 0 and 8.");
            return 2;
        }

        int renderWidth = args.Length > 2
            ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
            : DefaultRenderWidth;
        int renderHeight = args.Length > 3
            ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
            : DefaultRenderHeight;
        bool advancedLab = args.Length > 4
            && args[4].Equals("advanced", StringComparison.OrdinalIgnoreCase);
        double scrollOffset = args.Length > 5
            ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture)
            : 0;

        bool portable = args.Contains("portable", StringComparer.OrdinalIgnoreCase);
        bool onboarding = args.Contains("onboarding", StringComparer.OrdinalIgnoreCase);
        using MainViewModel viewModel = new() { IsPortableMode = portable };
        try
        {
            // Load data before any WPF control subscribes to the view model. This keeps
            // initialization thread-safe and lets the requested page render completely
            // in its first presentation frame.
            viewModel.InitialiseAsync(startAutomaticRefresh: false).GetAwaiter().GetResult();
            viewModel.IsAdvancedLab = advancedLab;
            if (onboarding)
            {
                viewModel.ShowOnboardingForSnapshot(step: 2);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }

        PCHelper.App.App application = new() { SuppressProductStartup = true };
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow window = new(viewModel)
        {
            Width = renderWidth,
            Height = renderHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        ListBox navigation = (ListBox)(window.FindName("Navigation")
            ?? throw new InvalidOperationException("The navigation list could not be located."));
        navigation.SelectedIndex = pageIndex;
        application.MainWindow = window;

        int result = 0;
        bool rendered = false;
        window.ContentRendered += async (_, _) =>
        {
            if (rendered)
            {
                return;
            }

            rendered = true;
            try
            {
                viewModel.DismissNoticeCommand.Execute(null);
                FrameworkElement root = (FrameworkElement)window.Content;
                root.Opacity = 0.999;
                await window.Dispatcher.InvokeAsync(
                    () => root.InvalidateVisual(),
                    DispatcherPriority.Render);
                if (scrollOffset > 0 && window.FindName(PageElementNames[pageIndex]) is ScrollViewer page)
                {
                    page.ScrollToVerticalOffset(scrollOffset);
                    await window.Dispatcher.InvokeAsync(() => page.UpdateLayout(), DispatcherPriority.ContextIdle);
                }
                root.Opacity = 1;
                await window.Dispatcher.InvokeAsync(() => root.InvalidateVisual(), DispatcherPriority.Render);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Capture(window, outputDirectory, pageIndex);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                result = 1;
            }
            finally
            {
                application.Shutdown();
            }
        };

        _ = application.Run(window);
        return result;
    }

    private static int RunAutomationSmoke(string reportPath)
    {
        using MainViewModel viewModel = new();
        try
        {
            viewModel.InitialiseAsync(startAutomaticRefresh: false).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }

        PCHelper.App.App application = new() { SuppressProductStartup = true };
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow window = new(viewModel)
        {
            Width = 960,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        application.MainWindow = window;

        int exitCode = 1;
        bool ran = false;
        window.ContentRendered += async (_, _) =>
        {
            if (ran)
            {
                return;
            }

            ran = true;
            try
            {
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                UiAutomationSmokeReport report = InspectAutomationSurface(window, viewModel);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonDefaults.Options));
                if (!report.Passed)
                {
                    throw new InvalidOperationException(string.Join(Environment.NewLine, report.Errors));
                }

                Console.WriteLine(reportPath);
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                application.Shutdown();
            }
        };

        _ = application.Run(window);
        return exitCode;
    }

    private static UiAutomationSmokeReport InspectAutomationSurface(MainWindow window, MainViewModel viewModel)
    {
        List<string> errors = [];
        List<string> visitedPages = [];
        ListBox navigation = Require<ListBox>(window, "Navigation");
        string[] pageElementNames =
        [
            "OverviewPage",
            "ProfilesPage",
            "CoolingPage",
            "PerformancePage",
            "LightingPage",
            "AutomationPage",
            "EcosystemPage",
            "DevicesPage",
            "DiagnosticsPage"
        ];
        string[] pageTitles =
        [
            "Overview",
            "Profiles",
            "Cooling",
            "Performance",
            "Lighting",
            "Automation",
            "Games & tools",
            "Devices",
            "Diagnostics"
        ];

        if (navigation.Items.Count != pageElementNames.Length)
        {
            errors.Add($"Navigation exposed {navigation.Items.Count} pages instead of {pageElementNames.Length}.");
        }

        for (int index = 0; index < pageElementNames.Length; index++)
        {
            navigation.SelectedIndex = index;
            window.UpdateLayout();
            FrameworkElement selected = Require<FrameworkElement>(window, pageElementNames[index]);
            int visiblePages = pageElementNames.Count(name => Require<FrameworkElement>(window, name).Visibility == Visibility.Visible);
            if (selected.Visibility != Visibility.Visible || visiblePages != 1)
            {
                errors.Add($"Page {pageTitles[index]} did not become the only visible page.");
            }

            if (!window.Title.EndsWith(pageTitles[index], StringComparison.Ordinal))
            {
                errors.Add($"Window title did not track page {pageTitles[index]}.");
            }

            visitedPages.Add(pageTitles[index]);
        }

        string[] simpleOnlyPanelNames =
        [
            "SimpleOverviewGuide",
            "SimpleProfilesGuide",
            "SimpleCoolingStatus",
            "SimplePerformanceStatus",
            "SimpleLightingGuide"
        ];
        string[] advancedOnlyPanelNames =
        [
            "AdvancedOverviewWorkspace",
            "AdvancedOverviewBanner",
            "ExperimentalControlCenter",
            "AdvancedProfileTools",
            "AdvancedCoolingWorkspace",
            "AdvancedPerformanceWorkspace",
            "AdvancedLightingWorkspace",
            "AdvancedAutomationPolicy",
            "AdvancedMacroEditor",
            "AdvancedDeviceOwnership",
            "AdvancedDeviceDecisionMatrix"
        ];
        bool initialAdvanced = viewModel.IsAdvancedLab;
        viewModel.IsAdvancedLab = false;
        window.UpdateLayout();
        bool simpleSurfaceWorked = simpleOnlyPanelNames.All(name => Require<FrameworkElement>(window, name).Visibility == Visibility.Visible)
            && advancedOnlyPanelNames.All(name => Require<FrameworkElement>(window, name).Visibility == Visibility.Collapsed)
            && viewModel.InterfaceModeLabel == "Simple"
            && viewModel.InterfaceModeActionLabel == "Open Advanced Lab";
        if (!simpleSurfaceWorked)
        {
            errors.Add("Simple mode did not hide the advanced workspace or present its daily-control guidance.");
        }

        viewModel.ToggleAdvancedLabCommand.Execute(null);
        window.UpdateLayout();
        bool advancedToggleWorked = viewModel.IsAdvancedLab;
        bool advancedSurfaceWorked = simpleOnlyPanelNames.All(name => Require<FrameworkElement>(window, name).Visibility == Visibility.Collapsed)
            && advancedOnlyPanelNames.All(name => Require<FrameworkElement>(window, name).Visibility == Visibility.Visible)
            && viewModel.InterfaceModeLabel == "Advanced Lab"
            && viewModel.InterfaceModeActionLabel == "Use Simple mode";
        if (!advancedToggleWorked || !advancedSurfaceWorked)
        {
            errors.Add("Advanced Lab command did not expose its dedicated workspace or update the mode action.");
        }

        viewModel.ToggleAdvancedLabCommand.Execute(null);
        viewModel.IsAdvancedLab = initialAdvanced;
        window.UpdateLayout();
        string[] requiredAutomationIds =
        [
            "Navigation.Pages",
            "Navigation.Overview",
            "Navigation.Cooling",
            "Navigation.Diagnostics",
            "Header.ToggleAdvancedLab",
            "Header.Refresh",
            "Overview.AcknowledgeExperimental",
            "Overview.OpenCoolingCommissioning",
            "Overview.OpenDeviceEvidence",
            "Profiles.AfterburnerPath",
            "Profiles.AfterburnerSection",
            "Profiles.PreviewAfterburner",
            "Profiles.SaveAfterburner",
            "Profiles.AcknowledgeManualVoltage",
            "Cooling.CalibrationTarget",
            "Cooling.OutputRole",
            "Cooling.OutputHeader",
            "Cooling.AcknowledgeRemoveProtection",
            "Cooling.SaveOutputRole",
            "Cooling.AcknowledgeExperimental",
            "Cooling.AcknowledgeDevice",
            "Cooling.AllowFanStop",
            "Cooling.SettlingSeconds",
            "Cooling.RestartCycles",
            "Cooling.StartCalibration",
            "Cooling.AbortCalibration",
            "Cooling.ApplyPump",
            "Cooling.CaseFansSilent",
            "Cooling.EnableCaseFansAutoMode",
            "Cooling.CaseFansCooling",
            "Cooling.CaseFansAdaptive",
            "Cooling.CommissioningSession",
            "Cooling.HeaderName",
            "Cooling.ConfirmHeader",
            "Cooling.BeginCommissioning",
            "Cooling.PulseCommissioning",
            "Cooling.RunElevatedDiagnostic",
            "Cooling.ObserveCommissioning",
            "Cooling.ConfirmCommissioning",
            "Cooling.CompleteCommissioning",
            "Cooling.CreateAdaptiveCurve",
            "Cooling.CustomCurveName",
            "Cooling.CustomCurvePoints",
            "Cooling.CustomCurveHysteresisUp",
            "Cooling.CustomCurveHysteresisDown",
            "Cooling.CustomCurveResponseUp",
            "Cooling.CustomCurveResponseDown",
            "Cooling.SaveCustomCurve",
            "Cooling.CancelCommissioning",
            "Cooling.RecoverCommissioning",
            "Cooling.FanControlPath",
            "Cooling.FanControlSensorMappings",
            "Cooling.FanControlControlMappings",
            "Cooling.PreviewFanControl",
            "Cooling.SaveFanControl",
            "Performance.TuneTarget",
            "Performance.TuneObjective",
            "Performance.TemperatureCeiling",
            "Performance.PowerCeiling",
            "Performance.StartTune",
            "Performance.AutoOcCore",
            "Performance.AutoOcMemory",
            "Performance.FullAutoOc",
            "Performance.GpuFanSilent",
            "Performance.EnableGpuFanAutoMode",
            "Performance.GpuFanCooling",
            "Performance.UndervoltQuiet",
            "Performance.UndervoltEfficient",
            "Performance.UndervoltStock",
            "Lighting.OpenRgbColour",
            "Lighting.OpenRgbBrightness",
            "Lighting.SyncAllRgb",
            "Lighting.SyncAllRgbOff",
            "Lighting.ApplyKrakenLighting",
            "Lighting.KrakenLightingOff",
            "Lighting.ApplyAura",
            "Lighting.AuraOff",
            "Lighting.ApplyGpuBracket",
            "Lighting.GpuBracketOff",
            "Lighting.ApplyDimmRgb",
            "Lighting.DimmRgbOff",
            "Lighting.ApplyRazerUsb",
            "Lighting.RazerUsbOff",
            "Lighting.RouteMatrix",
            "Lighting.LayoutDevice",
            "Lighting.ZoneName",
            "Lighting.ZoneIndices",
            "Lighting.AddZone",
            "Lighting.LayoutName",
            "Lighting.SelectedScene",
            "Lighting.SaveLayout",
            "Lighting.ApplyDynamicScene",
            "Automation.RuleName",
            "Automation.TriggerType",
            "Automation.TriggerValue",
            "Automation.TargetProfile",
            "Automation.Priority",
            "Games.MacroName",
            "Games.MacroDuration",
            "Games.StartMacroRecording",
            "Games.StopMacroRecording",
            "Games.CancelMacroRecording",
            "Games.SelectedMacro",
            "Games.TestMacro",
            "Games.MacroEditorName",
            "Games.MacroEditorKeyCode",
            "Games.MacroEditorDelay",
            "Games.AddMacroKeyPress",
            "Games.RemoveMacroKeyPress",
            "Games.SaveMacroEdit",
            "Games.SelectedGame",
            "Games.GameProfile",
            "Games.GameLighting",
            "Games.GameMacro",
            "Games.GameOsd",
            "Games.GameCapture",
            "Games.SaveBundle",
            "Games.ApplyBundle",
            "Games.DesktopOsdLayout",
            "Games.ShowDesktopOsd",
            "Games.HideDesktopOsd",
            "Games.SnapshotTarget",
            "Games.CaptureSnapshot",
            "Devices.CompatibilitySummary",
            "Devices.Search",
            "Devices.RefreshMonitorBrightness",
            "Devices.MonitorBrightnessDevice",
            "Devices.MonitorBrightnessPercent",
            "Devices.ConfirmMonitorBrightness",
            "Devices.ApplyMonitorBrightness",
            "Devices.PreviewTakeover",
            "Devices.TakeoverTarget",
            "Devices.TakeoverForceStop",
            "Devices.TakeoverStartup",
            "Devices.GrantTakeoverConsent",
            "Devices.ConfirmTakeover",
            "Devices.ExecuteTakeover",
            "Devices.ReleaseOwnership"
        ];
        string[] actionableHardwareControlIds =
        [
            "Cooling.ApplyPump",
            "Cooling.CaseFansSilent",
            "Cooling.EnableCaseFansAutoMode",
            "Cooling.CaseFansCooling",
            "Cooling.CaseFansAdaptive",
            "Cooling.StartCalibration",
            "Cooling.AbortCalibration",
            "Performance.StartTune",
            "Performance.AutoOcCore",
            "Performance.AutoOcMemory",
            "Performance.FullAutoOc",
            "Performance.GpuFanSilent",
            "Performance.EnableGpuFanAutoMode",
            "Performance.GpuFanCooling",
            "Performance.UndervoltQuiet",
            "Performance.UndervoltEfficient",
            "Performance.UndervoltStock",
            "Lighting.SyncAllRgb",
            "Lighting.SyncAllRgbOff",
            "Lighting.ApplyKrakenLighting",
            "Lighting.KrakenLightingOff",
            "Lighting.ApplyAura",
            "Lighting.AuraOff",
            "Lighting.ApplyGpuBracket",
            "Lighting.GpuBracketOff",
            "Lighting.ApplyDimmRgb",
            "Lighting.DimmRgbOff",
            "Lighting.ApplyRazerUsb",
            "Lighting.RazerUsbOff",
            "Games.ApplyBundle"
        ];

        FrameworkElement[] elements = Descendants(window).OfType<FrameworkElement>().ToArray();
        IReadOnlyDictionary<string, FrameworkElement> expectedPageByAutomationPrefix = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal)
        {
            ["Overview."] = Require<FrameworkElement>(window, "OverviewPage"),
            ["Profiles."] = Require<FrameworkElement>(window, "ProfilesPage"),
            ["Cooling."] = Require<FrameworkElement>(window, "CoolingPage"),
            ["Performance."] = Require<FrameworkElement>(window, "PerformancePage"),
            ["Lighting."] = Require<FrameworkElement>(window, "LightingPage"),
            ["Automation."] = Require<FrameworkElement>(window, "AutomationPage"),
            ["Games."] = Require<FrameworkElement>(window, "EcosystemPage"),
            ["Devices."] = Require<FrameworkElement>(window, "DevicesPage")
        };
        Dictionary<string, List<FrameworkElement>> byAutomationId = elements
            .Select(element => (Element: element, Id: AutomationProperties.GetAutomationId(element)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Element).ToList(), StringComparer.Ordinal);
        foreach (string id in requiredAutomationIds)
        {
            if (!byAutomationId.TryGetValue(id, out List<FrameworkElement>? matches))
            {
                errors.Add($"Required automation ID is missing: {id}.");
                continue;
            }

            if (matches.Count != 1)
            {
                errors.Add($"Automation ID {id} is not unique.");
            }

            FrameworkElement element = matches[0];
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(element)
                ?? new FrameworkElementAutomationPeer(element);
            string accessibleName = peer.GetName();
            if (string.IsNullOrWhiteSpace(accessibleName))
            {
                errors.Add($"Automation ID {id} has no accessible name.");
            }

            if (element is Control control && control is not ProgressBar && !control.Focusable)
            {
                errors.Add($"Automation ID {id} is not keyboard focusable.");
            }

            KeyValuePair<string, FrameworkElement> expectedPage = expectedPageByAutomationPrefix
                .FirstOrDefault(pair => id.StartsWith(pair.Key, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(expectedPage.Key)
                && !Descendants(expectedPage.Value).OfType<FrameworkElement>().Contains(element))
            {
                errors.Add($"Automation ID {id} is not on the expected {expectedPage.Key[..^1]} page.");
            }
        }

        foreach (string id in actionableHardwareControlIds)
        {
            if (!byAutomationId.TryGetValue(id, out List<FrameworkElement>? matches) || matches.Count != 1)
            {
                continue;
            }

            if (matches[0] is not Control control || !control.IsEnabled)
            {
                errors.Add($"Hardware action {id} is greyed out instead of remaining actionable.");
            }
        }

        string[] duplicateIds = byAutomationId
            .Where(pair => pair.Key.Contains('.', StringComparison.Ordinal) && pair.Value.Count > 1)
            .Select(pair => pair.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            errors.Add($"Duplicate automation IDs: {string.Join(", ", duplicateIds)}.");
        }

        viewModel.IsAdvancedLab = true;
        navigation.SelectedIndex = 0;
        window.UpdateLayout();
        if (byAutomationId.TryGetValue("Overview.OpenCoolingCommissioning", out List<FrameworkElement>? coolingRoutes)
            && coolingRoutes.Count == 1
            && coolingRoutes[0] is ButtonBase coolingRoute)
        {
            coolingRoute.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            if (navigation.SelectedIndex != 2)
            {
                errors.Add("Experimental Control Center did not route its Cooling action to the Cooling page.");
            }
        }

        navigation.SelectedIndex = 0;
        window.UpdateLayout();
        if (byAutomationId.TryGetValue("Overview.OpenDeviceEvidence", out List<FrameworkElement>? deviceRoutes)
            && deviceRoutes.Count == 1
            && deviceRoutes[0] is ButtonBase deviceRoute)
        {
            deviceRoute.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            if (navigation.SelectedIndex != 7)
            {
                errors.Add("Experimental Control Center did not route its device-evidence action to the Devices page.");
            }
        }

        navigation.SelectedIndex = 0;
        viewModel.IsAdvancedLab = initialAdvanced;
        window.UpdateLayout();

        Control[] interactive = elements.OfType<Control>()
            .Where(control => control.TemplatedParent is null && (control is ButtonBase or TextBoxBase or Selector))
            .ToArray();
        string[] unnamedInteractive = interactive.Where(control =>
        {
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(control)
                ?? new FrameworkElementAutomationPeer(control);
            return string.IsNullOrWhiteSpace(peer.GetName());
        }).Select(control => $"{control.GetType().Name}:{control.Name}:{AutomationProperties.GetAutomationId(control)}").ToArray();
        int namedInteractive = interactive.Length - unnamedInteractive.Length;

        return new UiAutomationSmokeReport(
            errors.Count == 0,
            DateTimeOffset.UtcNow,
            visitedPages,
            requiredAutomationIds,
            actionableHardwareControlIds,
            elements.Length,
            interactive.Length,
            namedInteractive,
            unnamedInteractive,
            advancedToggleWorked,
            simpleSurfaceWorked,
            advancedSurfaceWorked,
            duplicateIds,
            BuildFeatureReadiness(viewModel),
            errors);
    }

    private static FeatureReadinessReport BuildFeatureReadiness(MainViewModel viewModel)
    {
        bool HasAvailableTarget(string prefix) => viewModel.TuneTargets.Any(target =>
            target.IsAvailable
            && target.Descriptor.Id.StartsWith(prefix, StringComparison.Ordinal));
        bool IsProtected(string capabilityId) => viewModel.CoolingOutputAssignments.Any(assignment =>
            string.Equals(assignment.CapabilityId, capabilityId, StringComparison.Ordinal)
            && assignment.IsSafetyCritical);
        int caseFanOutputs = viewModel.FanControlSliders.Count(slider => !IsProtected(slider.CapabilityId));
        int protectedOutputs = viewModel.CoolingOutputAssignments.Count(assignment => assignment.IsSafetyCritical);

        return new FeatureReadinessReport(
            viewModel.IsServiceOnline,
            viewModel.CanUseServiceWrites,
            viewModel.HardwareControlEnabled,
            HasAvailableTarget("gpuclock.core:"),
            HasAvailableTarget("gpuclock.memory:"),
            HasAvailableTarget("gpufan.duty:"),
            HasAvailableTarget("gpupower.limit:"),
            caseFanOutputs,
            protectedOutputs,
            viewModel.ServiceCompatibilityMessage);
    }

    private static T Require<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"Required UI element {name} is missing.");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            foreach (DependencyObject descendant in Descendants(VisualTreeHelper.GetChild(root, index)))
            {
                yield return descendant;
            }
        }
    }

    private sealed record UiAutomationSmokeReport(
        bool Passed,
        DateTimeOffset CapturedAt,
        IReadOnlyList<string> VisitedPages,
        IReadOnlyList<string> RequiredAutomationIds,
        IReadOnlyList<string> ActionableHardwareControlIds,
        int VisualElementCount,
        int InteractiveControlCount,
        int NamedInteractiveControlCount,
        IReadOnlyList<string> UnnamedInteractiveControls,
        bool AdvancedToggleWorked,
        bool SimpleSurfaceWorked,
        bool AdvancedSurfaceWorked,
        IReadOnlyList<string> DuplicateAutomationIds,
        FeatureReadinessReport FeatureReadiness,
        IReadOnlyList<string> Errors);

    private sealed record FeatureReadinessReport(
        bool ServiceOnline,
        bool ServiceWritesReady,
        bool HardwareControlEnabled,
        bool GpuCoreClockTargetReady,
        bool GpuMemoryClockTargetReady,
        bool GpuFanTargetReady,
        bool GpuPowerTargetReady,
        int SafeCaseFanOutputCount,
        int ProtectedCoolingOutputCount,
        string ServiceCompatibilityMessage);

    private static void Capture(MainWindow window, string outputDirectory, int pageIndex)
    {
        FrameworkElement root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        RenderTargetBitmap bitmap = new(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(root);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string outputPath = Path.Combine(outputDirectory, $"{pageIndex + 1:00}-{PageNames[pageIndex]}.png");
        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine(outputPath);
    }
}
