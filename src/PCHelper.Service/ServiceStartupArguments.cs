namespace PCHelper.Service;

/// <summary>
/// Parses the small, service-owned startup surface.  In particular, a staged
/// service can use an isolated state directory without changing the machine
/// environment inherited by unrelated services.
/// </summary>
public sealed record ServiceStartupOptions(string? DataDirectory);

public static class ServiceStartupArguments
{
    public const string DataDirectoryArgument = "--data-dir";

    public static bool TryParse(
        IReadOnlyList<string>? args,
        out ServiceStartupOptions options,
        out string? error)
    {
        string? dataDirectory = null;
        IReadOnlyList<string> values = args ?? Array.Empty<string>();

        for (int index = 0; index < values.Count; index++)
        {
            if (!string.Equals(values[index], DataDirectoryArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dataDirectory is not null)
            {
                options = new ServiceStartupOptions(null);
                error = $"{DataDirectoryArgument} may be supplied only once.";
                return false;
            }

            if (++index >= values.Count || string.IsNullOrWhiteSpace(values[index]))
            {
                options = new ServiceStartupOptions(null);
                error = $"{DataDirectoryArgument} requires an absolute directory path.";
                return false;
            }

            string candidate = values[index].Trim();
            if (!Path.IsPathFullyQualified(candidate))
            {
                options = new ServiceStartupOptions(null);
                error = $"{DataDirectoryArgument} requires an absolute directory path.";
                return false;
            }

            try
            {
                dataDirectory = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                options = new ServiceStartupOptions(null);
                error = $"{DataDirectoryArgument} is not a valid directory path.";
                return false;
            }
        }

        options = new ServiceStartupOptions(dataDirectory);
        error = null;
        return true;
    }
}
