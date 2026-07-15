using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32.SafeHandles;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.EffectHost;

internal static class Program
{
    private const int MaximumRequestBytes = 2 * 1024 * 1024;
    private static int _exitCode = 1;
    private static EffectHostJob? _processJob;

    [STAThread]
    public static int Main(string[] args)
    {
        Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            try
            {
                _exitCode = await RunAsync(args).ConfigureAwait(true);
            }
            finally
            {
                application.Shutdown(_exitCode);
            }
        };
        application.Run();
        return _exitCode;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        EffectRenderRequestV1? request = null;
        try
        {
            if (WindowsIdentity.GetCurrent().IsSystem)
            {
                throw new InvalidOperationException("Effect Host refuses to execute as LocalSystem.");
            }
            request = await ReadRequestAsync(args).ConfigureAwait(true);
            ValidateInput(request);
            EffectScriptPackageInspection inspection = await EffectScriptPackageValidator.InspectAsync(
                request.Manifest,
                request.PackageRoot,
                CancellationToken.None).ConfigureAwait(true);
            if (!inspection.Validation.IsValid || inspection.Source is null)
            {
                throw new InvalidDataException(string.Join(" ", inspection.Validation.Errors));
            }

            _processJob = new EffectHostJob(512L * 1024 * 1024);
            _processJob.AddCurrentProcess();
            EffectRenderResultV1 result = await RenderAsync(request, inspection.Source, started).ConfigureAwait(true);
            await WriteResultAsync(args, result).ConfigureAwait(true);
            return result.Completed ? 0 : 1;
        }
        catch (Exception exception)
        {
            EffectRenderResultV1 failed = new(
                EffectRenderResultV1.CurrentSchemaVersion,
                request?.Manifest.Id ?? "unknown",
                Completed: false,
                TimedOut: exception is TimeoutException,
                Colours: [],
                Error: exception.Message,
                started,
                DateTimeOffset.UtcNow);
            await WriteResultAsync(args, failed).ConfigureAwait(true);
            return 1;
        }
    }

    private static async Task WriteResultAsync(string[] args, EffectRenderResultV1 result)
    {
        string json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        int index = Array.FindIndex(args, item => item.Equals("--response", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            string responsePath = Path.GetFullPath(args[index + 1]);
            await File.WriteAllTextAsync(responsePath, json).ConfigureAwait(true);
        }
        Console.WriteLine(json);
    }

    private static async Task<EffectRenderRequestV1> ReadRequestAsync(string[] args)
    {
        int index = Array.FindIndex(args, item => item.Equals("--request", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            throw new ArgumentException("Usage: PCHelper.EffectHost --request <request.json>");
        }
        string path = Path.GetFullPath(args[index + 1]);
        FileInfo file = new(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumRequestBytes)
        {
            throw new InvalidDataException("Effect request must be 1 byte to 2 MB.");
        }
        return JsonSerializer.Deserialize<EffectRenderRequestV1>(
            await File.ReadAllTextAsync(path).ConfigureAwait(true),
            JsonDefaults.Options) ?? throw new InvalidDataException("Effect request is empty.");
    }

    private static void ValidateInput(EffectRenderRequestV1 request)
    {
        if (request.SchemaVersion != EffectRenderRequestV1.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported effect request schema {request.SchemaVersion}.");
        }
        if (request.WatchdogMilliseconds is < 50 or > 5000)
        {
            throw new InvalidDataException("Effect watchdog must be 50-5000 ms.");
        }
        if (request.Input.Leds.Count is 0 || request.Input.Leds.Count > request.Manifest.MaximumLedCount)
        {
            throw new InvalidDataException("Effect request exceeds its manifest LED limit.");
        }
        if (!double.IsFinite(request.Input.ElapsedMilliseconds)
            || request.Input.ElapsedMilliseconds < 0
            || request.Input.Leds.Select(led => led.Index).Distinct().Count() != request.Input.Leds.Count
            || request.Input.Leds.Any(led => led.Index < 0 || !double.IsFinite(led.X) || !double.IsFinite(led.Y))
            || request.Input.Sensors.Values.Any(value => !double.IsFinite(value))
            || request.Input.AudioBins.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
        {
            throw new InvalidDataException("Effect frame input contains invalid coordinates or non-finite values.");
        }
    }

    private static async Task<EffectRenderResultV1> RenderAsync(
        EffectRenderRequestV1 request,
        string source,
        DateTimeOffset started)
    {
        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCHelper",
            "EffectHost",
            "WebView2");
        Directory.CreateDirectory(userData);
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userData).ConfigureAwait(true);
        WebView2 view = new();
        Window window = new()
        {
            Content = view,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            Opacity = 0.01,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        window.Show();
        try
        {
            await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            CoreWebView2 core = view.CoreWebView2;
            Harden(core);

            TaskCompletionSource<bool> navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, eventArgs) =>
            {
                if (eventArgs.IsSuccess) navigation.TrySetResult(true);
                else navigation.TrySetException(new InvalidOperationException($"Effect sandbox initialization failed: {eventArgs.WebErrorStatus}."));
            };
            core.NavigateToString("<!doctype html><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; connect-src 'none'; img-src data:; media-src 'none'; object-src 'none'; frame-src 'none';\"><title>RigPilot Effect Host</title>");
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            BlockExternalResources(core);

            await core.ExecuteScriptAsync(source).WaitAsync(TimeSpan.FromMilliseconds(request.WatchdogMilliseconds)).ConfigureAwait(true);
            TaskCompletionSource<string> message = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebMessageReceived += (_, eventArgs) => message.TrySetResult(eventArgs.TryGetWebMessageAsString());
            string input = JsonSerializer.Serialize(request.Input, JsonDefaults.Options);
            string runner = $$"""
                (() => {
                  const fail = error => chrome.webview.postMessage(JSON.stringify({ ok: false, error: String(error && error.message || error) }));
                  if (typeof globalThis.render !== 'function') { fail('Effect must define globalThis.render(input).'); return; }
                  Promise.resolve(globalThis.render({{input}})).then(colours => {
                    if (!Array.isArray(colours)) throw new Error('Effect render result must be an array.');
                    chrome.webview.postMessage(JSON.stringify({ ok: true, colours }));
                  }).catch(fail);
                })();
                """;
            await core.ExecuteScriptAsync(runner).WaitAsync(TimeSpan.FromMilliseconds(request.WatchdogMilliseconds)).ConfigureAwait(true);
            string response = await message.Task.WaitAsync(TimeSpan.FromMilliseconds(request.WatchdogMilliseconds)).ConfigureAwait(true);
            ScriptResponse parsed = JsonSerializer.Deserialize<ScriptResponse>(response, JsonDefaults.Options)
                ?? throw new InvalidDataException("Effect returned an empty result.");
            if (!parsed.Ok)
            {
                throw new InvalidOperationException(parsed.Error ?? "Effect execution failed.");
            }
            if (parsed.Colours is null || parsed.Colours.Count != request.Input.Leds.Count)
            {
                throw new InvalidDataException("Effect must return exactly one colour per requested LED.");
            }
            return new EffectRenderResultV1(
                EffectRenderResultV1.CurrentSchemaVersion,
                request.Manifest.Id,
                Completed: true,
                TimedOut: false,
                parsed.Colours,
                Error: null,
                started,
                DateTimeOffset.UtcNow);
        }
        catch (TimeoutException)
        {
            return new EffectRenderResultV1(
                EffectRenderResultV1.CurrentSchemaVersion,
                request.Manifest.Id,
                Completed: false,
                TimedOut: true,
                Colours: [],
                Error: $"Effect exceeded its {request.WatchdogMilliseconds} ms watchdog.",
                started,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            view.Dispose();
            window.Close();
        }
    }

    private static void Harden(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsWebMessageEnabled = true;
        core.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
        core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
    }

    private static void BlockExternalResources(CoreWebView2 core)
    {
        core.NavigationStarting += (_, eventArgs) => eventArgs.Cancel = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, eventArgs) =>
        {
            eventArgs.Response = core.Environment.CreateWebResourceResponse(
                Stream.Null,
                403,
                "Blocked by RigPilot Effect Host",
                "Content-Type: text/plain");
        };
    }

    private sealed record ScriptResponse(bool Ok, IReadOnlyList<EffectColourV1>? Colours, string? Error);
}

internal sealed class EffectHostJob : IDisposable
{
    private const uint JobMemory = 0x00000200;
    private readonly SafeFileHandle _handle;

    public EffectHostJob(long maximumBytes)
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Effect Host job object.");
        ExtendedLimitInformation information = new()
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobMemory },
            JobMemoryLimit = checked((nuint)maximumBytes)
        };
        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!SetInformationJobObject(_handle, 9, buffer, (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the Effect Host memory limit.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void AddCurrentProcess()
    {
        using Process process = Process.GetCurrentProcess();
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not isolate the Effect Host process tree.");
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
