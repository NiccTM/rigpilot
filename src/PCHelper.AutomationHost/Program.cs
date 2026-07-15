using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

return await AutomationHost.RunAsync(args).ConfigureAwait(false);

internal static class AutomationHost
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private const int MaximumCapturedCharacters = 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        ScriptActionV1? action = null;
        try
        {
            if (WindowsIdentity.GetCurrent().IsSystem)
            {
                throw new InvalidOperationException("Automation Host refuses to execute as LocalSystem.");
            }
            int requestIndex = Array.FindIndex(args, item => item.Equals("--request", StringComparison.OrdinalIgnoreCase));
            if (requestIndex < 0 || requestIndex + 1 >= args.Length)
            {
                throw new ArgumentException("Usage: PCHelper.AutomationHost --request <request.json>");
            }
            string requestPath = Path.GetFullPath(args[requestIndex + 1]);
            FileInfo requestFile = new(requestPath);
            if (!requestFile.Exists || requestFile.Length is <= 0 or > MaximumRequestBytes)
            {
                throw new InvalidDataException("Automation request must be 1 byte to 1 MB.");
            }
            ScriptExecutionRequestV1 request = JsonSerializer.Deserialize<ScriptExecutionRequestV1>(
                await File.ReadAllTextAsync(requestPath).ConfigureAwait(false),
                JsonDefaults.Options) ?? throw new InvalidDataException("Automation request is empty.");
            if (request.SchemaVersion != ScriptExecutionRequestV1.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported automation request schema {request.SchemaVersion}.");
            }
            action = request.Action;
            SuiteValidationResult validation = await ScriptActionValidator.ValidateFileAsync(action, CancellationToken.None).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(string.Join(" ", validation.Errors));
            }

            ScriptExecutionResultV1 result = await ExecuteAsync(action, started).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
            return result.Completed && result.ExitCode == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ScriptExecutionResultV1 failed = new(
                ScriptExecutionResultV1.CurrentSchemaVersion,
                action?.Id ?? "unknown",
                Started: false,
                Completed: false,
                TimedOut: false,
                Elevated: action?.RequestElevation ?? false,
                ExitCode: null,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                Error: exception.Message,
                started,
                DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(failed, JsonDefaults.Options));
            return 1;
        }
    }

    private static async Task<ScriptExecutionResultV1> ExecuteAsync(ScriptActionV1 action, DateTimeOffset started)
    {
        using Process process = new();
        ProcessStartInfo startInfo = new()
        {
            FileName = action.Interpreter,
            WorkingDirectory = Path.GetDirectoryName(action.ScriptPath)!,
            UseShellExecute = action.RequestElevation,
            CreateNoWindow = !action.RequestElevation,
            RedirectStandardOutput = !action.RequestElevation,
            RedirectStandardError = !action.RequestElevation,
            Verb = action.RequestElevation ? "runas" : string.Empty
        };
        startInfo.ArgumentList.Add(action.ScriptPath);
        foreach (string argument in SplitArguments(action.Arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }
        process.StartInfo = startInfo;
        StringBuilder output = new();
        StringBuilder error = new();
        if (!action.RequestElevation)
        {
            process.OutputDataReceived += (_, eventArgs) => AppendBounded(output, eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => AppendBounded(error, eventArgs.Data);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Interpreter did not start.");
        }
        if (!action.RequestElevation)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        using CancellationTokenSource timeout = new(action.Timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }
        return new ScriptExecutionResultV1(
            ScriptExecutionResultV1.CurrentSchemaVersion,
            action.Id,
            Started: true,
            Completed: !timedOut && process.HasExited,
            timedOut,
            action.RequestElevation,
            process.HasExited ? process.ExitCode : null,
            output.ToString(),
            error.ToString(),
            timedOut ? $"Script exceeded its {action.Timeout} timeout and its process tree was terminated." : null,
            started,
            DateTimeOffset.UtcNow);
    }

    private static List<string> SplitArguments(string commandLine)
    {
        List<string> arguments = [];
        StringBuilder current = new();
        bool quoted = false;
        for (int index = 0; index < commandLine.Length; index++)
        {
            char character = commandLine[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            if (character == '\\' && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
            {
                current.Append('"');
                index++;
                continue;
            }
            current.Append(character);
        }
        if (quoted)
        {
            throw new InvalidDataException("Script arguments contain an unmatched quote.");
        }
        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }
        return arguments;
    }

    private static void AppendBounded(StringBuilder target, string? line)
    {
        if (line is null || target.Length >= MaximumCapturedCharacters)
        {
            return;
        }
        int remaining = MaximumCapturedCharacters - target.Length;
        target.AppendLine(line.Length <= remaining ? line : line[..remaining]);
    }
}
