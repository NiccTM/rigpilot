using Microsoft.Extensions.Hosting.WindowsServices;
using PCHelper.Core;
using PCHelper.Service;

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

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string dataDirectory = Environment.GetEnvironmentVariable("PCHELPER_DATA_DIR") ?? DataPaths.GetDefaultDataDirectory();
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
