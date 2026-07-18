using Microsoft.Extensions.Hosting.WindowsServices;
using PCHelper.Core;
using PCHelper.Service;

if (args is ["--print-release-policy"])
{
    ReleaseTrustPolicy policy = ReleaseTrustPolicy.FromAssembly(typeof(PCHelperRuntime).Assembly);
    Console.WriteLine($"publicUnsignedPreview={policy.PublicUnsignedPreview.ToString().ToLowerInvariant()};writesAllowed={policy.WritesAllowed.ToString().ToLowerInvariant()}");
    return;
}

if (args.Length > 0 && args[0] == "--install-operators-group")
{
    // Never fall through to the service host when invoked as a one-shot tool,
    // even with a malformed argument list.
    Environment.ExitCode = args is [_, string operatorSid]
        ? OperatorGroupInstaller.EnsureGroupAndMember(operatorSid)
        : 2;
    return;
}

if (WindowsServiceHelpers.IsWindowsService())
{
    OperatorGroupInstaller.EnsureGroup();
}

if (!ServiceStartupArguments.TryParse(args, out ServiceStartupOptions startupOptions, out string? startupError))
{
    Console.Error.WriteLine(startupError);
    Environment.ExitCode = 2;
    return;
}

if (startupOptions.DataDirectory is not null)
{
    Environment.SetEnvironmentVariable("PCHELPER_DATA_DIR", startupOptions.DataDirectory, EnvironmentVariableTarget.Process);
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string dataDirectory = startupOptions.DataDirectory
    ?? Environment.GetEnvironmentVariable("PCHELPER_DATA_DIR")
    ?? DataPaths.GetDefaultDataDirectory();
string logDirectory = Path.Combine(dataDirectory, "logs");
try
{
    builder.Logging.AddProvider(new BoundedJsonFileLoggerProvider(logDirectory));
}
catch (UnauthorizedAccessException) when (Environment.UserInteractive)
{
    string fallback = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCHelper",
        "Development",
        "logs");
    builder.Logging.AddProvider(new BoundedJsonFileLoggerProvider(fallback));
}

builder.Services.AddWindowsService(options => options.ServiceName = "PC Helper Service");
builder.Services.AddSingleton<PCHelperRuntime>();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
await host.RunAsync();
