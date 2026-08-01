namespace PCHelper.Integration.Tests;

/// <summary>
/// Marks a test that only means anything against real hardware, and that must be
/// opted into with an environment variable.
///
/// <para>These tests used to gate themselves with an early <c>return</c>. The
/// operation never ran, and the run still reported <c>Passed</c> — so a green
/// suite read as though live hardware qualification had been exercised when it
/// had not. On a project whose whole discipline is refusing to call unproven
/// things proven, a test that reports success for work it did not do is the one
/// kind of dishonesty that matters most.</para>
///
/// <para>Setting <see cref="Xunit.FactAttribute.Skip"/> during construction makes
/// xUnit report the test as <b>Skipped</b> instead. xUnit v2 reads the property
/// off the constructed attribute at discovery time, so this needs no framework
/// upgrade and no extra package: the default suite reports these as skipped, and
/// setting the gate variable makes them run and be counted as executed.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LiveHardwareFactAttribute : FactAttribute
{
    /// <param name="gateVariable">
    /// Environment variable that must equal "1" for the test to run.
    /// </param>
    /// <param name="requires">
    /// What the test physically needs, named in the skip reason so the reason is
    /// actionable rather than just "skipped".
    /// </param>
    public LiveHardwareFactAttribute(string gateVariable, string requires)
    {
        GateVariable = gateVariable;
        Requires = requires;
        if (!IsGateOpen(gateVariable))
        {
            Skip = $"NOT EXECUTED — live-hardware gate. Requires {requires}. Set {gateVariable}=1 to run it.";
        }
    }

    public string GateVariable { get; }

    public string Requires { get; }

    public static bool IsGateOpen(string gateVariable) => string.Equals(
        Environment.GetEnvironmentVariable(gateVariable),
        "1",
        StringComparison.Ordinal);
}
