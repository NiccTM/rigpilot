namespace PCHelper.Integration.Tests;

/// <summary>
/// Proves the live-hardware gate reports honestly in all three states without
/// running any of the hardware tests themselves: the attribute is constructed
/// directly, so no fan spins and no GPU workload starts.
/// </summary>
public sealed class LiveHardwareFactAttributeTests : IDisposable
{
    private const string Gate = "PCHELPER_TEST_ONLY_LIVE_GATE";

    [Fact]
    public void ClosedGateSkipsTheTestWithAnActionableReason()
    {
        Environment.SetEnvironmentVariable(Gate, null);

        LiveHardwareFactAttribute attribute = new(Gate, "a reference cooling rig");

        Assert.False(string.IsNullOrWhiteSpace(attribute.Skip));
        Assert.Contains("NOT EXECUTED", attribute.Skip, StringComparison.Ordinal);
        // The reason has to say what is missing and how to run it, or a skipped
        // test is indistinguishable from a forgotten one.
        Assert.Contains("a reference cooling rig", attribute.Skip, StringComparison.Ordinal);
        Assert.Contains(Gate, attribute.Skip, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenGateLetsTheTestExecute()
    {
        Environment.SetEnvironmentVariable(Gate, "1");

        LiveHardwareFactAttribute attribute = new(Gate, "a reference cooling rig");

        Assert.Null(attribute.Skip);
        Assert.True(LiveHardwareFactAttribute.IsGateOpen(Gate));
    }

    /// <summary>Only an exact "1" opens the gate; nothing ambiguous does.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData(" 1")]
    public void AmbiguousGateValuesKeepTheTestSkipped(string value)
    {
        Environment.SetEnvironmentVariable(Gate, value);

        LiveHardwareFactAttribute attribute = new(Gate, "a reference cooling rig");

        Assert.False(LiveHardwareFactAttribute.IsGateOpen(Gate));
        Assert.False(string.IsNullOrWhiteSpace(attribute.Skip));
    }

    /// <summary>
    /// Every live-hardware test must carry the gate. A future test added with a
    /// plain [Fact] would silently report Passed for work it never did — the exact
    /// defect this attribute exists to remove.
    /// </summary>
    [Fact]
    public void EveryLiveHardwareTraitTestIsGated()
    {
        // xUnit v2's TraitAttribute keeps its name/value as constructor arguments
        // rather than properties, so they are read from the attribute metadata.
        string[] ungated = typeof(LiveHardwareFactAttributeTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method => method
                .GetCustomAttributesData()
                .Any(attribute => attribute.AttributeType == typeof(TraitAttribute)
                    && attribute.ConstructorArguments.Count == 2
                    && (string?)attribute.ConstructorArguments[0].Value == "Category"
                    && (string?)attribute.ConstructorArguments[1].Value == "LiveHardware"))
            .Where(method => method.GetCustomAttributes(typeof(LiveHardwareFactAttribute), inherit: false).Length == 0)
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToArray();

        Assert.True(
            ungated.Length == 0,
            $"LiveHardware tests without a [LiveHardwareFact] gate: {string.Join(", ", ungated)}");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(Gate, null);
}
