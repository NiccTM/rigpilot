using System.Security.Cryptography;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static class ScriptActionValidator
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(1);

    public static SuiteValidationResult Validate(ScriptActionV1 action)
    {
        List<string> errors = [];
        List<string> warnings = [];
        if (action.SchemaVersion != ScriptActionV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported script schema {action.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(action.Id) || string.IsNullOrWhiteSpace(action.Name))
        {
            errors.Add("Script ID and name are required.");
        }
        if (!Path.IsPathFullyQualified(action.ScriptPath))
        {
            errors.Add("Script path must be absolute.");
        }
        if (!Path.IsPathFullyQualified(action.Interpreter))
        {
            errors.Add("Interpreter path must be absolute.");
        }
        if (action.Sha256.Length != 64 || !action.Sha256.All(Uri.IsHexDigit))
        {
            errors.Add("Script trust hash must be a SHA-256 value.");
        }
        if (action.Timeout <= TimeSpan.Zero || action.Timeout > MaximumTimeout)
        {
            errors.Add("Script timeout must be between 1 tick and 1 hour.");
        }
        if (!action.Trusted)
        {
            errors.Add("Script is not individually trusted.");
        }
        if (action.RequestElevation)
        {
            warnings.Add("This run requires a fresh UAC prompt and cannot reuse stored credentials.");
        }
        return new SuiteValidationResult(errors.Count == 0, errors, warnings);
    }

    public static async Task<SuiteValidationResult> ValidateFileAsync(
        ScriptActionV1 action,
        CancellationToken cancellationToken)
    {
        SuiteValidationResult structural = Validate(action);
        List<string> errors = [.. structural.Errors];
        if (errors.Count == 0)
        {
            if (!File.Exists(action.ScriptPath))
            {
                errors.Add("Trusted script file no longer exists.");
            }
            else if (!File.Exists(action.Interpreter))
            {
                errors.Add("Selected script interpreter no longer exists.");
            }
            else
            {
                await using FileStream input = new(
                    action.ScriptPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                string currentHash = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(currentHash, action.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Script contents changed; trust was invalidated.");
                }
            }
        }
        return new SuiteValidationResult(errors.Count == 0, errors, structural.Warnings);
    }
}

public static class MacroValidator
{
    private const int MaximumSteps = 100_000;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(1);

    public static SuiteValidationResult Validate(MacroV1 macro)
    {
        List<string> errors = [];
        List<string> warnings = [];
        if (macro.SchemaVersion != MacroV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported macro schema {macro.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(macro.Id) || string.IsNullOrWhiteSpace(macro.Name))
        {
            errors.Add("Macro ID and name are required.");
        }
        if (macro.Steps.Count is 0 or > MaximumSteps)
        {
            errors.Add($"Macro must contain 1-{MaximumSteps} steps.");
        }

        TimeSpan duration = TimeSpan.Zero;
        HashSet<int> pressedKeys = [];
        HashSet<int> pressedButtons = [];
        foreach (MacroStepV1 step in macro.Steps)
        {
            if (step.Delay < TimeSpan.Zero || step.Delay > TimeSpan.FromMinutes(5))
            {
                errors.Add("Each macro step delay must be 0-5 minutes.");
                break;
            }
            duration += step.Delay;
            if (duration > MaximumDuration)
            {
                errors.Add("Macro duration exceeds 1 hour.");
                break;
            }

            switch (step.Kind)
            {
                case MacroStepKind.KeyDown:
                    ValidatePress(step.Code, pressedKeys, "key", errors);
                    break;
                case MacroStepKind.KeyUp:
                    ValidateRelease(step.Code, pressedKeys, "key", errors);
                    break;
                case MacroStepKind.MouseButtonDown:
                    ValidatePress(step.Code, pressedButtons, "mouse button", errors);
                    break;
                case MacroStepKind.MouseButtonUp:
                    ValidateRelease(step.Code, pressedButtons, "mouse button", errors);
                    break;
            }
        }
        if (pressedKeys.Count > 0 || pressedButtons.Count > 0)
        {
            errors.Add("Macro leaves one or more keys or mouse buttons pressed.");
        }
        return new SuiteValidationResult(errors.Count == 0, errors.Distinct(StringComparer.Ordinal).ToArray(), warnings);
    }

    private static void ValidatePress(int code, HashSet<int> pressed, string label, List<string> errors)
    {
        if (code <= 0 || !pressed.Add(code))
        {
            errors.Add($"Macro contains an invalid or duplicate {label} press.");
        }
    }

    private static void ValidateRelease(int code, HashSet<int> pressed, string label, List<string> errors)
    {
        if (code <= 0 || !pressed.Remove(code))
        {
            errors.Add($"Macro releases a {label} that is not pressed.");
        }
    }
}

public static class EffectGraphValidator
{
    public static SuiteValidationResult Validate(EffectGraphV1 graph)
    {
        List<string> errors = [];
        List<string> warnings = [];
        if (graph.SchemaVersion != EffectGraphV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported effect graph schema {graph.SchemaVersion}.");
        }
        if (graph.FramesPerSecond is < 1 or > 120)
        {
            errors.Add("Effect frame rate must be 1-120 FPS.");
        }
        if (graph.Nodes.Count is 0 or > 128)
        {
            errors.Add("Effect graph must contain 1-128 nodes.");
        }
        Dictionary<string, EffectNodeV1> nodes = new(StringComparer.Ordinal);
        foreach (EffectNodeV1 node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node))
            {
                errors.Add("Effect node IDs must be non-empty and unique.");
            }
        }
        if (!nodes.ContainsKey(graph.OutputNodeId))
        {
            errors.Add("Effect output node does not exist.");
        }
        foreach (EffectNodeV1 node in graph.Nodes)
        {
            foreach (string input in node.InputNodeIds)
            {
                if (!nodes.ContainsKey(input))
                {
                    errors.Add($"Effect node '{node.Id}' references missing node '{input}'.");
                }
            }
            if (node.Kind == EffectNodeKind.Script && !node.TextParameters.ContainsKey("manifestId"))
            {
                errors.Add($"Script effect node '{node.Id}' requires a trusted manifest reference.");
            }
            if (node.Kind is EffectNodeKind.AudioSpectrum or EffectNodeKind.ScreenAmbience)
            {
                warnings.Add($"Effect '{node.Id}' requires a user-session capture source.");
            }
        }
        DetectCycles(nodes, errors);
        return new SuiteValidationResult(errors.Count == 0, errors.Distinct(StringComparer.Ordinal).ToArray(), warnings);
    }

    private static void DetectCycles(Dictionary<string, EffectNodeV1> nodes, List<string> errors)
    {
        HashSet<string> visiting = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach (string id in nodes.Keys)
        {
            Visit(id);
        }

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }
            if (!visiting.Add(id))
            {
                errors.Add($"Effect graph contains a cycle at '{id}'.");
                return;
            }
            foreach (string input in nodes[id].InputNodeIds.Where(nodes.ContainsKey))
            {
                Visit(input);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
    }
}

public sealed record UpdateValidationContext(
    IReadOnlySet<string> AllowedDownloadHosts,
    IReadOnlySet<string> ExactDeviceIds,
    string StagedPackageSha256,
    string StagedPackagePublisher,
    bool PackageSignatureValid,
    bool StablePower,
    bool BitLockerRecoveryKeyAvailable,
    bool DeveloperBuild);

public static class UpdatePlanValidator
{
    public static SuiteValidationResult Validate(UpdatePlanV1 plan, UpdateValidationContext context)
    {
        List<string> errors = [];
        List<string> warnings = [];
        UpdateCandidateV1 candidate = plan.Candidate;
        if (plan.SchemaVersion != UpdatePlanV1.CurrentSchemaVersion
            || candidate.SchemaVersion != UpdateCandidateV1.CurrentSchemaVersion)
        {
            errors.Add("Unsupported update plan or candidate schema.");
        }
        if (!plan.UserConfirmed)
        {
            errors.Add("Update requires an explicit user confirmation.");
        }
        if (!candidate.DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !context.AllowedDownloadHosts.Contains(candidate.DownloadUri.IdnHost))
        {
            errors.Add("Update origin is not an allowlisted HTTPS vendor endpoint.");
        }
        if (!context.ExactDeviceIds.Contains(candidate.DeviceId))
        {
            errors.Add("Update target does not match an exact detected device ID.");
        }
        if (!context.PackageSignatureValid
            || !string.Equals(context.StagedPackagePublisher, candidate.ExpectedPublisher, StringComparison.Ordinal))
        {
            errors.Add("Update package signature or publisher is invalid.");
        }
        if (!HashEquals(candidate.Sha256, context.StagedPackageSha256))
        {
            errors.Add("Staged update package hash does not match the candidate.");
        }
        if (!context.StablePower)
        {
            errors.Add("Stable external power is required for driver and firmware updates.");
        }
        if (candidate.RequiresBitLockerSuspension && !context.BitLockerRecoveryKeyAvailable)
        {
            errors.Add("BitLocker recovery-key availability must be verified before suspension.");
        }
        if (candidate.Kind is UpdateKind.Bios or UpdateKind.DeviceFirmware)
        {
            if (string.IsNullOrWhiteSpace(candidate.RecoveryMethod))
            {
                errors.Add("Firmware update is unsupported without a vendor recovery method.");
            }
            if (context.DeveloperBuild)
            {
                errors.Add("Developer builds cannot perform firmware updates.");
            }
        }
        if (candidate.RequiresReboot)
        {
            warnings.Add("A reboot sentinel and post-boot version verification are required.");
        }
        return new SuiteValidationResult(errors.Count == 0, errors, warnings);
    }

    public static bool CanTransition(UpdateTransactionState from, UpdateTransactionState to) => from switch
    {
        UpdateTransactionState.Planned => to is UpdateTransactionState.Validated or UpdateTransactionState.Failed,
        UpdateTransactionState.Validated => to is UpdateTransactionState.Staged or UpdateTransactionState.Failed,
        UpdateTransactionState.Staged => to is UpdateTransactionState.Applying or UpdateTransactionState.Failed,
        UpdateTransactionState.Applying => to is UpdateTransactionState.PendingReboot or UpdateTransactionState.Verifying or UpdateTransactionState.Failed or UpdateTransactionState.RecoveryRequired,
        UpdateTransactionState.PendingReboot => to is UpdateTransactionState.Verifying or UpdateTransactionState.Failed or UpdateTransactionState.RecoveryRequired,
        UpdateTransactionState.Verifying => to is UpdateTransactionState.Completed or UpdateTransactionState.RolledBack or UpdateTransactionState.Failed or UpdateTransactionState.RecoveryRequired,
        UpdateTransactionState.Failed => to is UpdateTransactionState.RolledBack or UpdateTransactionState.RecoveryRequired,
        _ => false
    };

    private static bool HashEquals(string left, string right) => left.Length == 64
        && right.Length == 64
        && left.All(Uri.IsHexDigit)
        && right.All(Uri.IsHexDigit)
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            System.Text.Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
}
