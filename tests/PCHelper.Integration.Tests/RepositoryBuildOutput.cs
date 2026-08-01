namespace PCHelper.Integration.Tests;

/// <summary>
/// Locates a sibling project's build output for the tests that drive a real
/// child process (the Effect Host, the UI snapshot tool).
///
/// <para>Those paths used to name <c>bin\Release</c> outright, which tied the
/// tests to a configuration they are not necessarily running in: a clean tree
/// built and tested in Debug alone failed them, because the Release output they
/// looked for had never been produced. The configuration the test assembly was
/// itself built in is the one whose output is guaranteed to exist, so it is
/// preferred, with the other configuration as a fallback for a mixed tree.</para>
/// </summary>
internal static class RepositoryBuildOutput
{
    private static readonly string[] AllConfigurations = ["Debug", "Release"];

    /// <summary>
    /// The configuration this test assembly was built in, read from its own
    /// output path. Falls back to Debug, which is the default configuration.
    /// </summary>
    public static string CurrentConfiguration { get; } = ResolveConfiguration();

    public static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PCHelper.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the RigPilot repository root.");
    }

    /// <summary>
    /// Resolves an executable produced by another project in this repository.
    /// A copy sitting beside the test assembly wins; otherwise the current
    /// configuration is tried, then the other one.
    /// </summary>
    /// <param name="projectRelativeDirectory">
    /// Repository-relative project directory, for example <c>src\PCHelper.EffectHost</c>.
    /// </param>
    /// <param name="targetFramework">The project's target framework moniker.</param>
    /// <param name="executableName">The produced executable's file name.</param>
    /// <returns>The resolved path, or null when no configuration has been built.</returns>
    public static string? TryResolveExecutable(
        string projectRelativeDirectory,
        string targetFramework,
        string executableName)
    {
        // A project reference can drop a sibling project's apphost into this
        // output directory without its managed assembly, and that apphost exits
        // with "The application to execute does not exist". Requiring the
        // companion .dll is what tells a usable co-located copy from a stub.
        string colocated = Path.Combine(AppContext.BaseDirectory, executableName);
        string colocatedAssembly = Path.ChangeExtension(colocated, ".dll");
        if (File.Exists(colocated) && File.Exists(colocatedAssembly))
        {
            return colocated;
        }

        string repositoryRoot = FindRepositoryRoot();
        string[] configurations = CurrentConfiguration == "Release"
            ? ["Release", "Debug"]
            : ["Debug", "Release"];
        foreach (string configuration in configurations)
        {
            string candidate = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                projectRelativeDirectory,
                "bin",
                configuration,
                targetFramework,
                executableName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The searched locations, for a failure message that says where to look.</summary>
    public static string DescribeSearchedLocations(
        string projectRelativeDirectory,
        string targetFramework,
        string executableName)
    {
        string repositoryRoot = FindRepositoryRoot();
        IEnumerable<string> candidates = AllConfigurations
            .Select(configuration => Path.Combine(
                repositoryRoot,
                projectRelativeDirectory,
                "bin",
                configuration,
                targetFramework,
                executableName))
            .Prepend(Path.Combine(AppContext.BaseDirectory, executableName));
        return string.Join(Environment.NewLine, candidates);
    }

    private static string ResolveConfiguration()
    {
        string[] segments = AppContext.BaseDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = segments.Length - 1; index > 0; index--)
        {
            if (string.Equals(segments[index - 1], "bin", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(segments[index], "Release", StringComparison.OrdinalIgnoreCase)
                    ? "Release"
                    : "Debug";
            }
        }

        return "Debug";
    }
}
