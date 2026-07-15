using System.Security.Principal;
using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;
using Forms = System.Windows.Forms;

namespace PCHelper.Integration.Tests;

public sealed class UserAgentRuntimeTests
{
    [Fact]
    public async Task CurrentUserCanPersistValidatedMacroButOtherIdentityIsRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory);
            await runtime.InitializeAsync(CancellationToken.None);
            string currentUser = WindowsIdentity.GetCurrent().Name;
            IpcClientContext current = new(IsOperator: false, currentUser);
            MacroV1 macro = new(
                MacroV1.CurrentSchemaVersion,
                "macro.test",
                "Test macro",
                [
                    new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero),
                    new MacroStepV1(MacroStepKind.KeyUp, 65, 0, 0, 0, TimeSpan.FromMilliseconds(10))
                ]);

            IpcResponse saved = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveMacro, macro),
                current,
                CancellationToken.None);
            IpcResponse listed = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetMacros),
                current,
                CancellationToken.None);
            IpcResponse rejected = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetMacros),
                new IpcClientContext(false, "OTHER\\User"),
                CancellationToken.None);

            Assert.True(saved.Success);
            Assert.Equal(macro.Id, Assert.Single(IpcJson.FromElement<IReadOnlyList<MacroV1>>(listed.Payload)!).Id);
            Assert.False(rejected.Success);
            Assert.Equal("NOT_CURRENT_USER", rejected.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverUpdatesIsHandledAndHonestlyReportsNoConfiguredSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.DiscoverUpdates),
                current,
                CancellationToken.None);

            // Previously a dead route (WRONG_EXECUTION_CONTEXT); now handled truthfully.
            Assert.True(response.Success);
            Assert.NotEqual("WRONG_EXECUTION_CONTEXT", response.ErrorCode);
            UpdateDiscoveryResultV1 result = IpcJson.FromElement<UpdateDiscoveryResultV1>(response.Payload)!;
            Assert.False(result.SourceConfigured);
            Assert.Empty(result.Candidates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidMacroNeverEntersUserState()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            MacroV1 invalid = new(
                MacroV1.CurrentSchemaVersion,
                "macro.invalid",
                "Invalid",
                [new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero)]);

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveMacro, invalid),
                current,
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("INVALID_MACRO", response.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidOsdLayoutIsRejectedBeforePersistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            OsdLayoutV1 invalid = new(
                OsdLayoutV1.CurrentSchemaVersion,
                "osd.invalid",
                "Invalid",
                [new OsdWidgetV1("cpu.temp", "CPU", "{0:unsafe}", 0, 0, "red")],
                1,
                1,
                false);

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveOsdLayout, invalid),
                current,
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("INVALID_OSD_LAYOUT", response.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OSDPresentationAndMonitoringPreferencesPersistOnlyWithinValidatedBounds()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            OsdPresentationSettingsV1 settings = new(
                OsdPresentationSettingsV1.CurrentSchemaVersion,
                OsdPresentationSettingsV1.DefaultId,
                "display:DISPLAY1",
                OsdScreenAnchor.BottomRight,
                0.8,
                1.2,
                "Ctrl+Alt+O",
                Enabled: true);
            MonitoringPreferencesV1 preferences = new(
                MonitoringPreferencesV1.CurrentSchemaVersion,
                MonitoringPreferencesV1.DefaultId,
                [new SensorAliasV1("cpu.temp", "CPU package")],
                ["cpu.temp"],
                DateTimeOffset.UtcNow);

            IpcResponse savedSettings = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveOsdPresentationSettings, settings), current, CancellationToken.None);
            IpcResponse savedPreferences = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveMonitoringPreferences, preferences), current, CancellationToken.None);
            IpcResponse loadedSettings = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetOsdPresentationSettings), current, CancellationToken.None);
            IpcResponse loadedPreferences = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetMonitoringPreferences), current, CancellationToken.None);

            Assert.True(savedSettings.Success, $"{savedSettings.ErrorCode}: {savedSettings.Error}");
            Assert.True(savedPreferences.Success, $"{savedPreferences.ErrorCode}: {savedPreferences.Error}");
            Assert.Equal(OsdScreenAnchor.BottomRight, IpcJson.FromElement<OsdPresentationSettingsV1>(loadedSettings.Payload)!.Anchor);
            Assert.Equal("CPU package", Assert.Single(IpcJson.FromElement<MonitoringPreferencesV1>(loadedPreferences.Payload)!.Aliases).Alias);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MonitoringComparisonLayoutPersistsAtMostFourDistinctSensorIds()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            MonitoringComparisonLayoutV1 valid = new(
                MonitoringComparisonLayoutV1.CurrentSchemaVersion,
                MonitoringComparisonLayoutV1.DefaultId,
                ["cpu.temp", "gpu.temp"],
                NormalizeEachSeries: true,
                DateTimeOffset.UtcNow);
            MonitoringComparisonLayoutV1 invalid = valid with
            {
                SensorIds = ["cpu.temp", "gpu.temp", "pump.rpm", "fan.rpm", "memory.temp"]
            };

            IpcResponse saved = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveMonitoringComparisonLayout, valid), current, CancellationToken.None);
            IpcResponse loaded = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetMonitoringComparisonLayout), current, CancellationToken.None);
            IpcResponse rejected = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveMonitoringComparisonLayout, invalid), current, CancellationToken.None);

            Assert.True(saved.Success, $"{saved.ErrorCode}: {saved.Error}");
            Assert.Equal(
                ["cpu.temp", "gpu.temp"],
                IpcJson.FromElement<MonitoringComparisonLayoutV1>(loaded.Payload)!.SensorIds);
            Assert.False(rejected.Success);
            Assert.Equal("INVALID_MONITORING_COMPARISON", rejected.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidOsdPresentationNeverPersists()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            OsdPresentationSettingsV1 invalid = new(
                OsdPresentationSettingsV1.CurrentSchemaVersion,
                OsdPresentationSettingsV1.DefaultId,
                null,
                OsdScreenAnchor.TopLeft,
                0.1,
                3.0,
                string.Empty,
                Enabled: true);

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.SaveOsdPresentationSettings, invalid), current, CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("INVALID_OSD_PRESENTATION", response.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VisibleRecordingPersistsOnlyAfterExplicitStopAndReleasesTheInputGate()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeMacroRecorder recorder = new();
            await using UserAgentRuntime runtime = new(directory, recorder);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);

            IpcResponse begun = await runtime.HandleRequestAsync(
                Request(IpcCommand.BeginMacroRecording, new BeginMacroRecordingRequest("Aim reset", TimeSpan.FromSeconds(30))),
                current,
                CancellationToken.None);
            MacroRecordingSessionV1 session = IpcJson.FromElement<MacroRecordingSessionV1>(begun.Payload)!;
            IpcResponse status = await runtime.HandleRequestAsync(Request(IpcCommand.GetMacroRecordingStatus), current, CancellationToken.None);
            IpcResponse stopped = await runtime.HandleRequestAsync(
                Request(IpcCommand.StopMacroRecording, new StopMacroRecordingRequest(session.Id)),
                current,
                CancellationToken.None);
            IpcResponse macros = await runtime.HandleRequestAsync(Request(IpcCommand.GetMacros), current, CancellationToken.None);
            IpcResponse sessions = await runtime.HandleRequestAsync(Request(IpcCommand.GetMacroRecordingSessions), current, CancellationToken.None);

            Assert.True(begun.Success);
            Assert.True(status.Success);
            Assert.True(stopped.Success);
            Assert.True(IpcJson.FromElement<MacroRecordingStatusV1>(status.Payload)!.InputCaptureActive);
            MacroRecordingResultV1 result = IpcJson.FromElement<MacroRecordingResultV1>(stopped.Payload)!;
            Assert.Equal(MacroRecordingState.Completed, result.Session.State);
            Assert.NotNull(result.Macro);
            Assert.Equal(result.Macro!.Id, Assert.Single(IpcJson.FromElement<IReadOnlyList<MacroV1>>(macros.Payload)!).Id);
            Assert.Equal(MacroRecordingState.Completed, Assert.Single(IpcJson.FromElement<IReadOnlyList<MacroRecordingSessionV1>>(sessions.Payload)!).State);
            Assert.Equal(1, recorder.StartCount);
            Assert.Equal(1, recorder.StopCount);

            IpcResponse second = await runtime.HandleRequestAsync(
                Request(IpcCommand.BeginMacroRecording, new BeginMacroRecordingRequest("Second", TimeSpan.FromSeconds(10))),
                current,
                CancellationToken.None);
            Assert.True(second.Success);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedRecordingIsRecoveredWithoutRetainingRawInput()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            await using (UserAgentRuntime first = new(directory, new FakeMacroRecorder()))
            {
                await first.InitializeAsync(CancellationToken.None);
                IpcResponse begun = await first.HandleRequestAsync(
                    Request(IpcCommand.BeginMacroRecording, new BeginMacroRecordingRequest("Interrupted", TimeSpan.FromSeconds(30))),
                    current,
                    CancellationToken.None);
                Assert.True(begun.Success);
            }

            await using UserAgentRuntime recovered = new(directory, new FakeMacroRecorder());
            await recovered.InitializeAsync(CancellationToken.None);
            IpcResponse sessions = await recovered.HandleRequestAsync(
                Request(IpcCommand.GetMacroRecordingSessions),
                current,
                CancellationToken.None);
            MacroRecordingSessionV1 session = Assert.Single(IpcJson.FromElement<IReadOnlyList<MacroRecordingSessionV1>>(sessions.Payload)!);

            Assert.Equal(MacroRecordingState.Cancelled, session.State);
            Assert.Null(session.MacroId);
            Assert.Contains("discarded", session.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrustedGameBarPackageCanReadOnlyOverlayStatusAndCannotMutateUserState()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            SecurityIdentifier packageSid = new("S-1-15-2-1");
            await using UserAgentRuntime runtime = new(directory, gameBarPackageSids: [packageSid]);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext widget = new(
                IsOperator: false,
                UserName: "APPLICATION PACKAGE AUTHORITY\\RigPilot",
                UserSid: packageSid.Value,
                IsAppContainer: true);
            MacroV1 macro = new(
                MacroV1.CurrentSchemaVersion,
                "macro.widget",
                "Widget mutation attempt",
                [
                    new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero),
                    new MacroStepV1(MacroStepKind.KeyUp, 65, 0, 0, 0, TimeSpan.FromMilliseconds(10))
                ]);

            IpcResponse handshake = await runtime.HandleRequestAsync(Request(IpcCommand.Handshake), widget, CancellationToken.None);
            IpcResponse status = await runtime.HandleRequestAsync(Request(IpcCommand.GetOverlayStatus), widget, CancellationToken.None);
            IpcResponse mutation = await runtime.HandleRequestAsync(Request(IpcCommand.SaveMacro, macro), widget, CancellationToken.None);
            IpcResponse spoof = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetOverlayStatus),
                widget with { IsAppContainer = false },
                CancellationToken.None);

            Assert.True(handshake.Success);
            Assert.True(status.Success);
            Assert.False(mutation.Success);
            Assert.Equal("GAMEBAR_READ_ONLY", mutation.ErrorCode);
            Assert.False(spoof.Success);
            Assert.Equal("NOT_CURRENT_USER", spoof.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopSnapshotRequiresVisibleConfirmationAndIsIdempotent()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeDesktopSnapshotBackend snapshots = new();
            await using UserAgentRuntime runtime = new(directory, desktopSnapshots: snapshots);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);

            IpcResponse listed = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetCaptureTargets),
                current,
                CancellationToken.None);
            CaptureTargetV1 target = Assert.Single(IpcJson.FromElement<IReadOnlyList<CaptureTargetV1>>(listed.Payload)!);
            CaptureSnapshotRequestV1 unconfirmed = new(
                CaptureSnapshotRequestV1.CurrentSchemaVersion,
                target,
                ConfirmedVisibleCapture: false);
            IpcResponse rejected = await runtime.HandleRequestAsync(
                Request(IpcCommand.CaptureDesktopSnapshot, unconfirmed, idempotencyKey: "snapshot.unconfirmed"),
                current,
                CancellationToken.None);
            CaptureSnapshotRequestV1 confirmed = unconfirmed with { ConfirmedVisibleCapture = true };
            IpcResponse first = await runtime.HandleRequestAsync(
                Request(IpcCommand.CaptureDesktopSnapshot, confirmed, idempotencyKey: "snapshot.confirmed"),
                current,
                CancellationToken.None);
            IpcResponse retry = await runtime.HandleRequestAsync(
                Request(IpcCommand.CaptureDesktopSnapshot, confirmed, idempotencyKey: "snapshot.confirmed"),
                current,
                CancellationToken.None);

            Assert.True(listed.Success);
            Assert.False(rejected.Success);
            Assert.Equal("CAPTURE_CONFIRMATION_REQUIRED", rejected.ErrorCode);
            Assert.True(first.Success);
            Assert.True(retry.Success);
            Assert.Equal(1, snapshots.CaptureCount);
            CaptureSnapshotResultV1 firstResult = IpcJson.FromElement<CaptureSnapshotResultV1>(first.Payload)!;
            CaptureSnapshotResultV1 retriedResult = IpcJson.FromElement<CaptureSnapshotResultV1>(retry.Payload)!;
            Assert.Equal(firstResult.Id, retriedResult.Id);
            Assert.Equal(CaptureSnapshotBackend.GdiDesktop, firstResult.Backend);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopSnapshotRejectsAnInvalidSchemaBeforeInvokingTheBackend()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeDesktopSnapshotBackend snapshots = new();
            await using UserAgentRuntime runtime = new(directory, desktopSnapshots: snapshots);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            CaptureSnapshotRequestV1 invalid = new(
                CaptureSnapshotRequestV1.CurrentSchemaVersion + 1,
                Assert.Single(snapshots.DiscoverTargets()),
                ConfirmedVisibleCapture: true);

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.CaptureDesktopSnapshot, invalid, idempotencyKey: "snapshot.invalid-schema"),
                current,
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("INVALID_CAPTURE_REQUEST", response.ErrorCode);
            Assert.Equal(0, snapshots.CaptureCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MonitorBrightnessRequiresExactConfirmationAndReadBackBeforeItCommits()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeMonitorBrightnessBackend brightness = new();
            await using UserAgentRuntime runtime = new(directory, monitorBrightness: brightness);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);

            IpcResponse listed = await runtime.HandleRequestAsync(
                Request(IpcCommand.GetMonitorBrightnesses),
                current,
                CancellationToken.None);
            MonitorBrightnessDeviceV1 device = Assert.Single(IpcJson.FromElement<IReadOnlyList<MonitorBrightnessDeviceV1>>(listed.Payload)!);
            SetMonitorBrightnessRequestV1 unconfirmed = new(
                SetMonitorBrightnessRequestV1.CurrentSchemaVersion,
                device.Id,
                61,
                ConfirmDevice: false);
            IpcResponse rejected = await runtime.HandleRequestAsync(
                Request(IpcCommand.SetMonitorBrightness, unconfirmed, idempotencyKey: "brightness.unconfirmed"),
                current,
                CancellationToken.None);
            SetMonitorBrightnessRequestV1 confirmed = unconfirmed with { ConfirmDevice = true };
            IpcResponse missingKey = await runtime.HandleRequestAsync(
                Request(IpcCommand.SetMonitorBrightness, confirmed),
                current,
                CancellationToken.None);
            IpcResponse first = await runtime.HandleRequestAsync(
                Request(IpcCommand.SetMonitorBrightness, confirmed, idempotencyKey: "brightness.confirmed"),
                current,
                CancellationToken.None);
            IpcResponse retry = await runtime.HandleRequestAsync(
                Request(IpcCommand.SetMonitorBrightness, confirmed, idempotencyKey: "brightness.confirmed"),
                current,
                CancellationToken.None);

            Assert.True(listed.Success);
            Assert.Equal(CapabilityAccessState.Experimental, device.State);
            Assert.Equal("External monitor via DDC/CI", device.TransportLabel);
            Assert.Equal("0% to 100% reported range", device.RangeLabel);
            Assert.False(rejected.Success);
            Assert.Equal("MONITOR_BRIGHTNESS_CONFIRMATION_REQUIRED", rejected.ErrorCode);
            Assert.False(missingKey.Success);
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", missingKey.ErrorCode);
            Assert.True(first.Success, $"{first.ErrorCode}: {first.Error}");
            Assert.True(retry.Success, $"{retry.ErrorCode}: {retry.Error}");
            Assert.Equal(1, brightness.SetCount);
            MonitorBrightnessApplyResultV1 result = IpcJson.FromElement<MonitorBrightnessApplyResultV1>(first.Payload)!;
            Assert.True(result.Applied);
            Assert.True(result.ReadBackVerified);
            Assert.Equal(61, result.ObservedPercent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MonitorBrightnessRejectsInvalidPayloadBeforeTheBackendWrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeMonitorBrightnessBackend brightness = new();
            await using UserAgentRuntime runtime = new(directory, monitorBrightness: brightness);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            SetMonitorBrightnessRequestV1 invalid = new(
                SetMonitorBrightnessRequestV1.CurrentSchemaVersion + 1,
                brightness.Device.Id,
                101,
                ConfirmDevice: true);

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.SetMonitorBrightness, invalid, idempotencyKey: "brightness.invalid"),
                current,
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("INVALID_MONITOR_BRIGHTNESS_REQUEST", response.ErrorCode);
            Assert.Equal(0, brightness.SetCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WindowsMonitorBrightnessBackendKeepsEveryLogicalDisplayVisibleWithoutWriting()
    {
        WindowsMonitorBrightnessBackend backend = new();

        IReadOnlyList<MonitorBrightnessDeviceV1> devices = backend.Discover();

        Assert.NotEmpty(devices);
        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            Assert.Contains(devices, device => string.Equals(
                device.DisplayDeviceName,
                screen.DeviceName,
                StringComparison.OrdinalIgnoreCase));
        }
        Assert.All(devices, device => Assert.Equal(MonitorBrightnessDeviceV1.CurrentSchemaVersion, device.SchemaVersion));
    }

    [Fact]
    public async Task WindowsSnapshotBackendRejectsForgedTargetBeforeCreatingOutput()
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), $"rigpilot-snapshot-{Guid.NewGuid():N}");
        try
        {
            WindowsDesktopSnapshotBackend backend = new(outputDirectory);
            CaptureSnapshotRequestV1 forged = new(
                CaptureSnapshotRequestV1.CurrentSchemaVersion,
                new CaptureTargetV1(CaptureTargetKind.Display, "window:0x1234", "Forged display"),
                ConfirmedVisibleCapture: true);

            await Assert.ThrowsAsync<InvalidDataException>(() => backend.CaptureAsync(forged, CancellationToken.None));

            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UserAgentRunsOnlyInjectedNoWriteElevatedFanDiagnostic()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeInteractiveFanPreflightLauncher launcher = new(prepared: true);
            await using UserAgentRuntime runtime = new(directory, interactiveFanPreflight: launcher);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            InteractiveFanPreflightRequestV1 request = new(
                InteractiveFanPreflightRequestV1.CurrentSchemaVersion,
                "lhm.control:/lpc/nct6798d/0/control/0");

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.RunInteractiveFanPreflight, request),
                current,
                CancellationToken.None);

            Assert.True(response.Success, $"{response.ErrorCode}: {response.Error}");
            InteractiveFanPreflightResultV1 result = IpcJson.FromElement<InteractiveFanPreflightResultV1>(response.Payload)!;
            Assert.True(result.Prepared);
            Assert.False(result.ApplyIssued);
            Assert.False(result.VerifyIssued);
            Assert.False(result.RollbackIssued);
            Assert.False(result.ResetIssued);
            Assert.Equal(request.CapabilityId, launcher.LastRequest?.CapabilityId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UserAgentRejectsInvalidElevatedFanDiagnosticBeforeLaunchingUacChild()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeInteractiveFanPreflightLauncher launcher = new(prepared: true);
            await using UserAgentRuntime runtime = new(directory, interactiveFanPreflight: launcher);
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);
            InteractiveFanPreflightRequestV1 request = new(
                InteractiveFanPreflightRequestV1.CurrentSchemaVersion,
                "lhm.control:/gpu-nvidia/0/control/0");

            IpcResponse response = await runtime.HandleRequestAsync(
                Request(IpcCommand.RunInteractiveFanPreflight, request),
                current,
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("INVALID_INTERACTIVE_PREFLIGHT", response.ErrorCode);
            Assert.Null(launcher.LastRequest);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InteractiveHostInvocationRejectsUnknownArgumentsAndParsesBoundedRequest()
    {
        string[] valid =
        [
            "--interactive-fan-preflight",
            "--pipe", "pchelper.interactive-preflight.1234.0123456789abcdef0123456789abcdef",
            "--token", new string('A', 64),
            "--capability", "lhm.control:/lpc/nct6798d/0/control/0"
        ];

        Assert.True(InteractiveFanPreflightHost.TryParseInvocation(valid, out InteractiveFanPreflightInvocation? invocation, out string? error), error);
        Assert.Equal("lhm.control:/lpc/nct6798d/0/control/0", invocation!.CapabilityId);
        Assert.False(InteractiveFanPreflightHost.TryParseInvocation(
            ["--interactive-fan-preflight", "--pipe", "bad", "--token", "bad", "--capability", "anything"],
            out _,
            out _));
    }

    private static IpcRequest Request<T>(IpcCommand command, T? payload = default, string? idempotencyKey = null) => new(
        ProtocolConstants.Version,
        Guid.NewGuid().ToString("N"),
        command,
        null,
        idempotencyKey,
        payload is null ? null : IpcJson.ToElement(payload));

    private static IpcRequest Request(IpcCommand command) => Request<object>(command);

    private sealed class FakeMacroRecorder : IMacroRecorder
    {
        public bool IsRecording { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(TimeSpan maximumDuration, CancellationToken cancellationToken)
        {
            IsRecording = true;
            StartCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MacroStepV1>> StopAsync(CancellationToken cancellationToken)
        {
            IsRecording = false;
            StopCount++;
            return Task.FromResult<IReadOnlyList<MacroStepV1>>(
            [
                new MacroStepV1(MacroStepKind.KeyDown, 65, 0, 0, 0, TimeSpan.Zero),
                new MacroStepV1(MacroStepKind.KeyUp, 65, 0, 0, 0, TimeSpan.FromMilliseconds(15))
            ]);
        }

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            IsRecording = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMonitorBrightnessBackend : IMonitorBrightnessBackend
    {
        public MonitorBrightnessDeviceV1 Device { get; private set; } = new(
            MonitorBrightnessDeviceV1.CurrentSchemaVersion,
            "ddcci:fake-primary",
            "Primary fake display",
            @"\\.\DISPLAY1",
            MonitorBrightnessTransport.DdcCi,
            CapabilityAccessState.Experimental,
            0,
            100,
            50,
            "Fake DDC/CI brightness path for user-agent testing.");

        public int SetCount { get; private set; }

        public IReadOnlyList<MonitorBrightnessDeviceV1> Discover() => [Device];

        public Task<MonitorBrightnessApplyResultV1> SetBrightnessAsync(
            SetMonitorBrightnessRequestV1 request,
            CancellationToken cancellationToken)
        {
            SetCount++;
            Device = Device with { CurrentPercent = request.BrightnessPercent };
            return Task.FromResult(new MonitorBrightnessApplyResultV1(
                MonitorBrightnessApplyResultV1.CurrentSchemaVersion,
                request.MonitorId,
                request.BrightnessPercent,
                request.BrightnessPercent,
                Applied: true,
                ReadBackVerified: true,
                RollbackAttempted: false,
                "Fake monitor brightness write was read back."));
        }
    }

    private sealed class FakeDesktopSnapshotBackend : IDesktopSnapshotBackend
    {
        private readonly CaptureTargetV1 _target = new(
            CaptureTargetKind.Display,
            "display:fake-primary",
            "Primary display (1920 x 1080)");

        public int CaptureCount { get; private set; }

        public IReadOnlyList<CaptureTargetV1> DiscoverTargets() => [_target];

        public Task<CaptureSnapshotResultV1> CaptureAsync(
            CaptureSnapshotRequestV1 request,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(new CaptureSnapshotResultV1(
                CaptureSnapshotResultV1.CurrentSchemaVersion,
                "snapshot.fake",
                request.Target,
                @"C:\Pictures\RigPilot\Snapshots\fake.png",
                DateTimeOffset.UtcNow,
                1920,
                1080,
                42,
                CaptureSnapshotBackend.GdiDesktop,
                null));
        }
    }

    private sealed class FakeInteractiveFanPreflightLauncher(bool prepared) : IInteractiveFanPreflightLauncher
    {
        private readonly bool _prepared = prepared;

        public InteractiveFanPreflightRequestV1? LastRequest { get; private set; }

        public Task<InteractiveFanPreflightResultV1> RunAsync(
            InteractiveFanPreflightRequestV1 request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new InteractiveFanPreflightResultV1(
                InteractiveFanPreflightResultV1.CurrentSchemaVersion,
                request.CapabilityId,
                _prepared,
                ApplyIssued: false,
                VerifyIssued: false,
                RollbackIssued: false,
                ResetIssued: false,
                IsElevated: true,
                ExecutionContext: "Test",
                DateTimeOffset.UtcNow,
                _prepared ? "PREPARE_SUCCEEDED_NO_WRITE" : "PREPARE_FAILED_NO_WRITE",
                "Test diagnostic result.",
                null));
        }
    }
}
