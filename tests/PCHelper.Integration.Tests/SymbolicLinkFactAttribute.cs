namespace PCHelper.Integration.Tests;

/// <summary>
/// Marks a test that needs to create a file symbolic link.
///
/// <para>Windows requires Developer Mode or elevation for that, and neither is
/// guaranteed on a CI agent or a developer machine. Probing once at discovery and
/// reporting <b>Skipped</b> keeps the result honest: a test that quietly returned
/// would report Passed for a reparse-point check it never performed, which is the
/// same defect the live-hardware gate exists to prevent.</para>
///
/// <para>Directory junctions need no privilege, so the junction cases cover the
/// destructive path unconditionally; this only gates the file-symlink variant.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Supported = new(Probe);

    public SymbolicLinkFactAttribute()
    {
        if (!Supported.Value)
        {
            Skip = "NOT EXECUTED — creating a file symbolic link requires Developer Mode or elevation on this machine.";
        }
    }

    private static bool Probe()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"rigpilot-symlink-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "target.txt");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(directory, "link.txt"), target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
