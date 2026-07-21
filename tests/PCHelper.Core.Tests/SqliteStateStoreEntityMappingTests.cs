using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class SqliteStateStoreEntityMappingTests
{
    [Fact]
    public void EverySuiteEntityKindMapsToAPersistedType()
    {
        // A SuiteEntityKind with no type mapping throws ArgumentOutOfRangeException the first
        // time the service loads or saves it, which crashes startup. This asserts the map is
        // exhaustive so a newly-added kind can never ship without its mapping.
        foreach (SuiteEntityKind kind in Enum.GetValues<SuiteEntityKind>())
        {
            Type mapped = SqliteStateStore.MapSuiteEntityType(kind);
            Assert.NotNull(mapped);
        }
    }
}
