using System.Reflection;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// The version every built-in adapter manifest reports.
///
/// <para>Each adapter used to carry its own literal, and they had all drifted to
/// the same stale value while the build moved on. Adapter manifests are not
/// decoration: they travel in probe output and hardware-evidence exports, where
/// a version names the build an observation came from. Reading it from the
/// assembly makes that one fact impossible to state wrongly.</para>
/// </summary>
public static class AdapterVersion
{
    public static string Current { get; } = RuntimeVersion.Get(typeof(AdapterVersion).Assembly);
}
