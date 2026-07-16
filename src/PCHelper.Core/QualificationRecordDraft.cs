using System.Security.Cryptography;
using System.Text;
using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>The exact hardware identity a qualification draft is built from.</summary>
public sealed record QualificationSystemIdentity(
    string CpuName,
    string GpuName,
    string BoardVendor,
    string BoardProduct);

/// <summary>
/// The four witnessed-safety attestations a human operator must explicitly make
/// before a draft record can exist. Each one asserts an observed absence of a
/// failure during real use of the build on this exact system; none of them can
/// be defaulted, inferred, or machine-generated.
/// </summary>
public sealed record QualificationAttestations(
    bool NoBsodOrUnexpectedReboot,
    bool NoStuckFan,
    bool NoUnauthorisedWrite,
    bool RollbackPassed)
{
    public bool AllAttested => NoBsodOrUnexpectedReboot && NoStuckFan && NoUnauthorisedWrite && RollbackPassed;
}

/// <summary>
/// Builds an UNSIGNED DRAFT <see cref="HardwareQualificationRecordV1"/> from the
/// real, observed identity of the local system plus explicit operator
/// attestations. Two invariants keep this flow honest:
///  1. <c>SignedProductionBuild</c> is hard-coded false — a draft can never
///     count toward the 18-system 1.0 gate; only the signed-build pipeline may
///     produce a record with that flag set.
///  2. Building refuses outright when any attestation is missing or when the
///     CPU/GPU family cannot be classified from the real device name — an
///     unclassifiable system is reported, not guessed.
/// </summary>
public static class QualificationRecordDraftBuilder
{
    public const string DraftNotePrefix =
        "UNSIGNED DRAFT — produced on an unsigned build; not valid 1.0 evidence until re-captured on a signed production build.";

    public static HardwareQualificationRecordV1 Build(
        QualificationSystemIdentity identity,
        QualificationAttestations attestations,
        DateTimeOffset capturedAt,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(attestations);
        if (!attestations.AllAttested)
        {
            throw new InvalidOperationException(
                "A qualification draft requires all four witnessed attestations (no BSOD/reboot, no stuck fan, "
                + "no unauthorised write, rollback passed). Refusing to fabricate an unwitnessed record.");
        }

        ProcessorQualificationFamily processor = TryClassifyProcessor(identity.CpuName)
            ?? throw new InvalidOperationException(
                $"CPU '{identity.CpuName}' does not map to a qualification family (Zen 3/4/5, Intel 12th/13th-14th/Core Ultra 200). "
                + "Unrecognised systems cannot be drafted.");
        GraphicsQualificationFamily graphics = TryClassifyGraphics(identity.GpuName)
            ?? throw new InvalidOperationException(
                $"GPU '{identity.GpuName}' does not map to a qualification family (RTX 30/40/50, RX 6000/7000/9000, Arc A/B). "
                + "Unrecognised systems cannot be drafted.");
        if (string.IsNullOrWhiteSpace(identity.BoardVendor))
        {
            throw new InvalidOperationException("A motherboard vendor is required for a qualification draft.");
        }

        PlatformQualificationFamily platform = processor is ProcessorQualificationFamily.RyzenZen3
            or ProcessorQualificationFamily.RyzenZen4
            or ProcessorQualificationFamily.RyzenZen5
            ? PlatformQualificationFamily.Amd
            : PlatformQualificationFamily.Intel;

        return new HardwareQualificationRecordV1(
            HardwareQualificationRecordV1.CurrentSchemaVersion,
            $"draft-{Guid.NewGuid():N}",
            CreateSystemId(identity),
            capturedAt,
            processor,
            graphics,
            platform,
            NormaliseBoardVendor(identity.BoardVendor),
            SignedProductionBuild: false,
            attestations.NoBsodOrUnexpectedReboot,
            attestations.NoStuckFan,
            attestations.NoUnauthorisedWrite,
            attestations.RollbackPassed,
            ControllerEvidence: [],
            string.IsNullOrWhiteSpace(notes) ? DraftNotePrefix : $"{DraftNotePrefix} {notes.Trim()}");
    }

    /// <summary>
    /// Privacy-preserving stable system ID: a truncated SHA-256 over the exact
    /// hardware identity. No serial numbers, user names, or machine names are
    /// hashed, so the same build on the same parts reproduces the same ID
    /// without identifying its owner.
    /// </summary>
    public static string CreateSystemId(QualificationSystemIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string canonical = string.Join(
            "|",
            identity.CpuName.Trim().ToUpperInvariant(),
            identity.GpuName.Trim().ToUpperInvariant(),
            identity.BoardVendor.Trim().ToUpperInvariant(),
            identity.BoardProduct.Trim().ToUpperInvariant());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"system-{Convert.ToHexStringLower(hash.AsSpan(0, 8).ToArray())}";
    }

    public static ProcessorQualificationFamily? TryClassifyProcessor(string cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName))
        {
            return null;
        }

        string name = cpuName.ToUpperInvariant();
        if (name.Contains("RYZEN", StringComparison.Ordinal) || name.Contains("THREADRIPPER", StringComparison.Ordinal))
        {
            // Model series: Zen 3 = 5xxx desktop, Zen 4 = 7xxx, Zen 5 = 9xxx.
            int? series = FirstModelSeries(name);
            return series switch
            {
                5 => ProcessorQualificationFamily.RyzenZen3,
                7 => ProcessorQualificationFamily.RyzenZen4,
                9 => ProcessorQualificationFamily.RyzenZen5,
                _ => null
            };
        }

        if (name.Contains("INTEL", StringComparison.Ordinal) || name.Contains("CORE", StringComparison.Ordinal))
        {
            if (name.Contains("ULTRA", StringComparison.Ordinal))
            {
                int? series = FirstModelSeries(name);
                return series == 2 ? ProcessorQualificationFamily.IntelCoreUltra200 : null;
            }

            // Classic Core iN-Gxxx naming: generation from the leading digits
            // of the 5-digit model number (12700 → 12, 14900 → 14).
            int? generation = CoreGeneration(name);
            return generation switch
            {
                12 => ProcessorQualificationFamily.Intel12th,
                13 or 14 => ProcessorQualificationFamily.Intel13th14th,
                _ => null
            };
        }

        return null;
    }

    public static GraphicsQualificationFamily? TryClassifyGraphics(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
        {
            return null;
        }

        string name = gpuName.ToUpperInvariant();
        if (name.Contains("RTX", StringComparison.Ordinal) || name.Contains("GEFORCE", StringComparison.Ordinal))
        {
            return FirstModelSeries(name) switch
            {
                3 => GraphicsQualificationFamily.Rtx30,
                4 => GraphicsQualificationFamily.Rtx40,
                5 => GraphicsQualificationFamily.Rtx50,
                _ => null
            };
        }

        if (name.Contains("RADEON", StringComparison.Ordinal) || name.Contains(" RX ", StringComparison.Ordinal)
            || name.StartsWith("RX ", StringComparison.Ordinal))
        {
            return FirstModelSeries(name) switch
            {
                6 => GraphicsQualificationFamily.Rx6000,
                7 => GraphicsQualificationFamily.Rx7000,
                9 => GraphicsQualificationFamily.Rx9000,
                _ => null
            };
        }

        if (name.Contains("ARC", StringComparison.Ordinal))
        {
            // Arc A-series (A750/A770) vs B-series (B570/B580).
            foreach (string token in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length >= 2 && token[0] is 'A' or 'B' && char.IsAsciiDigit(token[1]))
                {
                    return token[0] == 'A' ? GraphicsQualificationFamily.ArcA : GraphicsQualificationFamily.ArcB;
                }
            }
        }

        return null;
    }

    private static string NormaliseBoardVendor(string vendor)
    {
        string trimmed = vendor.Trim();
        if (trimmed.Contains("ASUS", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("ASUSTEK", StringComparison.OrdinalIgnoreCase))
        {
            return "ASUS";
        }
        if (trimmed.Contains("MICRO-STAR", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("MSI", StringComparison.OrdinalIgnoreCase))
        {
            return "MSI";
        }
        if (trimmed.Contains("GIGABYTE", StringComparison.OrdinalIgnoreCase))
        {
            return "Gigabyte";
        }
        if (trimmed.Contains("ASROCK", StringComparison.OrdinalIgnoreCase))
        {
            return "ASRock";
        }

        return trimmed;
    }

    /// <summary>First digit of the first 3-5 digit model number in the name (5800 → 5, 4090 → 4, 285K → 2).</summary>
    private static int? FirstModelSeries(string upperName)
    {
        for (int index = 0; index < upperName.Length; index++)
        {
            if (!char.IsAsciiDigit(upperName[index]) || (index > 0 && char.IsAsciiDigit(upperName[index - 1])))
            {
                continue;
            }

            int length = 0;
            while (index + length < upperName.Length && char.IsAsciiDigit(upperName[index + length]))
            {
                length++;
            }

            if (length is >= 3 and <= 5)
            {
                return upperName[index] - '0';
            }

            index += length;
        }

        return null;
    }

    /// <summary>Generation prefix of a classic 5-digit Intel Core model (12700K → 12).</summary>
    private static int? CoreGeneration(string upperName)
    {
        for (int index = 0; index < upperName.Length; index++)
        {
            if (!char.IsAsciiDigit(upperName[index]) || (index > 0 && char.IsAsciiDigit(upperName[index - 1])))
            {
                continue;
            }

            int length = 0;
            while (index + length < upperName.Length && char.IsAsciiDigit(upperName[index + length]))
            {
                length++;
            }

            if (length == 5)
            {
                return (upperName[index] - '0') * 10 + (upperName[index + 1] - '0');
            }

            index += length;
        }

        return null;
    }
}
