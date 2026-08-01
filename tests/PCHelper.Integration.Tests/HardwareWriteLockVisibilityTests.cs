using System.Reflection;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The failed-rollback write lock is the flag that stops the service accepting a
/// hardware mutation after it could not prove a default state. It is read on IPC
/// handler threads that hold no lock, read under the snapshot gate by the status
/// path, and written under the hardware mutation gate — and, on the boot-recovery
/// and shutdown paths, under neither. No single lock spans a writer and the IPC
/// reader, so the field must carry its own ordering.
///
/// <para>This is an architecture guard in the same vein as
/// <see cref="PrivilegedServiceBoundaryTests"/>: a timing test could not prove the
/// absence of a stale read, but dropping the modifier is exactly the regression
/// worth catching, and it is caught at compile-to-metadata level rather than by
/// hoping a race reproduces.</para>
/// </summary>
public sealed class HardwareWriteLockVisibilityTests
{
    private const string FieldName = "_rollbackBlocked";

    private static FieldInfo WriteLockField =>
        typeof(PCHelperRuntime).GetField(FieldName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{FieldName} was renamed; update this guard with it.");

    [Fact]
    public void HardwareWriteLockIsDeclaredVolatile()
    {
        Type[] modifiers = WriteLockField.GetRequiredCustomModifiers();

        Assert.Contains(typeof(System.Runtime.CompilerServices.IsVolatile), modifiers);
    }

    [Fact]
    public void HardwareWriteLockIsStillABooleanLatch()
    {
        // The volatile guarantee above applies to a field of this shape. A widening
        // to a struct or a reference type would silently make the guard meaningless.
        Assert.Equal(typeof(bool), WriteLockField.FieldType);
    }

    /// <summary>
    /// Any future flag that gates hardware writes needs the same treatment. This
    /// catches a sibling latch being added without ordering.
    /// </summary>
    [Fact]
    public void EveryBooleanWriteGateOnTheRuntimeCarriesOrdering()
    {
        string[] gateNames = ["_rollbackBlocked", "_writesBlocked", "_recoveryRequired", "_writeLocked"];
        string[] unordered = typeof(PCHelperRuntime)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(bool))
            .Where(field => gateNames.Contains(field.Name, StringComparer.Ordinal))
            .Where(field => !field.GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsVolatile)))
            .Select(field => field.Name)
            .ToArray();

        Assert.True(
            unordered.Length == 0,
            $"Hardware write-gate flags without volatile ordering: {string.Join(", ", unordered)}");
    }
}
