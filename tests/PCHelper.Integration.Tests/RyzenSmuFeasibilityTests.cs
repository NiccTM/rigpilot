using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the read-only Ryzen SMU feasibility decode against synthetic PM
/// tables (Vermeer layout: floats 0/1 PPT, 2/3 TDC, 4/5 THM, 8/9 EDC, per the
/// ryzen_monitor mapping) and a fake PawnIO module session. No driver, admin
/// rights, or hardware is touched.
/// </summary>
public sealed class RyzenSmuFeasibilityTests
{
    private const uint VermeerTableVersion = 0x380805;

    [Fact]
    public void ParseDecodesVermeerLimitsFromTheDocumentedFloatOffsets()
    {
        ReadOnlySpan<ulong> table = PackFloats([142f, 54.5f, 95f, 33.2f, 90f, 62.25f, 0f, 0f, 140f, 41.7f]);

        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.Parse(
            codeNameId: 4,
            smuVersion: (56u << 16) | (53u << 8) | 0u,
            pmTableVersion: VermeerTableVersion,
            table);

        Assert.Equal(RyzenSmuFeasibilityOutcome.Succeeded, result.Outcome);
        Assert.Equal("56.53.0", result.SmuFirmwareVersion);
        Assert.Equal("0x380805", result.PmTableVersion);
        Assert.Equal(142, result.PptLimitWatts);
        Assert.Equal(54.5, result.PptValueWatts);
        Assert.Equal(95, result.TdcLimitAmperes);
        Assert.Equal(33.2, result.TdcValueAmperes!.Value, precision: 2);
        Assert.Equal(90, result.ThmLimitCelsius);
        Assert.Equal(62.25, result.ThmValueCelsius);
        Assert.Equal(140, result.EdcLimitAmperes);
        Assert.Equal(41.7, result.EdcValueAmperes!.Value, precision: 2);
        Assert.Contains("writes remain Blocked", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRefusesToDecodeAnUnauditedPmTableVersion()
    {
        ReadOnlySpan<ulong> table = PackFloats([142f, 54.5f, 95f, 33.2f, 90f, 62.25f, 0f, 0f, 140f, 41.7f]);

        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.Parse(4, 0x00543200, 0x540104, table);

        Assert.Equal(RyzenSmuFeasibilityOutcome.UnrecognisedPmTable, result.Outcome);
        Assert.Equal("0x540104", result.PmTableVersion);
        Assert.NotNull(result.SmuFirmwareVersion);
        Assert.Null(result.PptLimitWatts);
        Assert.Null(result.EdcLimitAmperes);
    }

    [Fact]
    public void ParseFailsOnATruncatedTableInsteadOfInventingValues()
    {
        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.Parse(4, 0x00383500, VermeerTableVersion, PackFloats([142f, 54.5f]));

        Assert.Equal(RyzenSmuFeasibilityOutcome.Failed, result.Outcome);
        Assert.Null(result.PptLimitWatts);
    }

    [Fact]
    public void SessionPathInvokesOnlyReadClassModuleFunctions()
    {
        FakeModuleSession session = new();

        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.ReadWithSession(session);

        Assert.Equal(RyzenSmuFeasibilityOutcome.Succeeded, result.Outcome);
        // The complete set of module functions this pass may ever call. A new name
        // appearing here must be reviewed against the write policy first.
        Assert.Equal(
            ["ioctl_get_code_name", "ioctl_get_smu_version", "ioctl_resolve_pm_table", "ioctl_update_pm_table", "ioctl_read_pm_table"],
            session.Calls);
    }

    [Fact]
    public void SessionFailureIsContainedAsANonSuccessOutcome()
    {
        FakeModuleSession session = new() { ThrowOn = "ioctl_get_smu_version" };

        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.ReadWithSession(session);

        Assert.Equal(RyzenSmuFeasibilityOutcome.Failed, result.Outcome);
        Assert.Null(result.PptLimitWatts);
    }

    [Fact]
    public void TransientPmTableUpdateFailureIsRetriedOnceAndSucceeds()
    {
        // Models the live 0x8007054F mailbox-contention transient: the first
        // ioctl_update_pm_table fails, the immediate retry succeeds.
        FakeModuleSession session = new() { ThrowOn = "ioctl_update_pm_table", ThrowAtMost = 1 };

        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.ReadWithSession(session);

        Assert.Equal(RyzenSmuFeasibilityOutcome.Succeeded, result.Outcome);
        Assert.Equal(142, result.PptLimitWatts);
        Assert.Equal(2, session.Calls.Count(call => call == "ioctl_update_pm_table"));
    }

    [Fact]
    public void PersistentPmTableUpdateFailureStopsAfterASingleBoundedRetry()
    {
        FakeModuleSession session = new() { ThrowOn = "ioctl_update_pm_table" };

        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.ReadWithSession(session);

        Assert.Equal(RyzenSmuFeasibilityOutcome.Failed, result.Outcome);
        Assert.Null(result.PptLimitWatts);
        Assert.Equal(2, session.Calls.Count(call => call == "ioctl_update_pm_table"));
    }

    [Fact]
    public void LiveReadFailsSafeWithoutAdminRightsOrPawnIo()
    {
        // On CI PawnIO is absent; on the reference machine this test runs without
        // administrator rights, so the executor open is refused. Both must degrade
        // to a clean non-success outcome without throwing.
        RyzenSmuFeasibilityV1 result = RyzenSmuFeasibilityReader.Read();

        Assert.NotEqual(RyzenSmuFeasibilityOutcome.Succeeded, result.Outcome);
        Assert.Null(result.PptLimitWatts);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    private static ulong[] PackFloats(float[] values)
    {
        ulong[] qwords = new ulong[(values.Length + 1) / 2];
        for (int index = 0; index < values.Length; index++)
        {
            ulong bits = BitConverter.SingleToUInt32Bits(values[index]);
            qwords[index / 2] |= index % 2 == 0 ? bits : bits << 32;
        }

        return qwords;
    }

    private sealed class FakeModuleSession : IPawnIoModuleSession
    {
        public List<string> Calls { get; } = [];

        public string? ThrowOn { get; init; }

        /// <summary>How many times <see cref="ThrowOn"/> throws before succeeding; default is always.</summary>
        public int ThrowAtMost { get; init; } = int.MaxValue;

        private int thrown;

        public ulong[] Execute(string functionName, ReadOnlySpan<ulong> input, int maximumOutputCount)
        {
            Calls.Add(functionName);
            if (string.Equals(functionName, ThrowOn, StringComparison.Ordinal) && thrown < ThrowAtMost)
            {
                thrown++;
                throw new PawnIoException("Injected failure.", unchecked((int)0x8007054F));
            }

            return functionName switch
            {
                "ioctl_get_code_name" => [4],
                "ioctl_get_smu_version" => [(56u << 16) | (53u << 8) | 0u],
                "ioctl_resolve_pm_table" => [VermeerTableVersion, 0xDEAD0000],
                "ioctl_update_pm_table" => [],
                "ioctl_read_pm_table" => PackFloats([142f, 54.5f, 95f, 33.2f, 90f, 62.25f, 0f, 0f, 140f, 41.7f]),
                _ => throw new PawnIoException($"Unexpected function '{functionName}'.", unchecked((int)0x80004005)),
            };
        }

        public void Dispose()
        {
        }
    }
}
