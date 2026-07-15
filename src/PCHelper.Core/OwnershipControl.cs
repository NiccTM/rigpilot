using System.Security.Cryptography;
using System.Text;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static class TakeoverConsentValidator
{
    public static OwnershipConsentV1 Create(
        TakeoverProcessIdentity identity,
        bool allowForceTermination,
        bool disableStartup,
        DateTimeOffset grantedAt) => new(
            OwnershipConsentV1.CurrentSchemaVersion,
            $"consent.{Sanitise(identity.ProcessName)}.{identity.Sha256.ToLowerInvariant()}",
            identity.ProcessName,
            NormalisePath(identity.ExecutablePath),
            identity.ProductName,
            identity.Publisher,
            identity.SignerThumbprint,
            NormaliseHash(identity.Sha256),
            allowForceTermination,
            disableStartup,
            grantedAt);

    public static TakeoverAuthorizationResult Validate(
        TakeoverProcessIdentity current,
        OwnershipConsentV1 consent,
        bool requireForceTermination,
        bool requireStartupDisable)
    {
        List<string> errors = [];
        if (consent.SchemaVersion != OwnershipConsentV1.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported ownership consent schema {consent.SchemaVersion}.");
        }

        Compare("executable path", NormalisePath(current.ExecutablePath), NormalisePath(consent.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        Compare("process name", current.ProcessName, consent.ProcessName, StringComparison.OrdinalIgnoreCase);
        Compare("product identity", current.ProductName, consent.ProductName, StringComparison.Ordinal);
        Compare("publisher", current.Publisher, consent.Publisher, StringComparison.Ordinal);
        Compare("signer", current.SignerThumbprint ?? string.Empty, consent.SignerThumbprint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!HashEquals(current.Sha256, consent.Sha256))
        {
            errors.Add("Executable hash changed; stored consent is invalid.");
        }
        if (requireForceTermination && !consent.AllowForceTermination)
        {
            errors.Add("Stored consent does not permit force termination.");
        }
        if (requireStartupDisable && !consent.DisableStartup)
        {
            errors.Add("Stored consent does not permit changing startup entries.");
        }

        return new TakeoverAuthorizationResult(errors.Count == 0, errors);

        void Compare(string field, string currentValue, string consentValue, StringComparison comparison)
        {
            if (!string.Equals(currentValue, consentValue, comparison))
            {
                errors.Add($"Executable {field} changed; stored consent is invalid.");
            }
        }
    }

    public static async Task<string> ComputeSha256Async(string executablePath, CancellationToken cancellationToken)
    {
        string fullPath = NormalisePath(executablePath);
        await using FileStream input = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static bool HashEquals(string left, string right)
    {
        string normalLeft = NormaliseHash(left);
        string normalRight = NormaliseHash(right);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(normalLeft),
            Encoding.ASCII.GetBytes(normalRight));
    }

    private static string NormaliseHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string hash = value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Executable SHA-256 must contain 64 hexadecimal characters.", nameof(value));
        }
        return hash;
    }

    private static string NormalisePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string Sanitise(string value) => new(value.ToLowerInvariant()
        .Select(character => char.IsLetterOrDigit(character) ? character : '-')
        .ToArray());
}

public sealed class OwnershipLeaseManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, OwnershipLeaseV1> _leases = new(StringComparer.Ordinal);

    public OwnershipLeaseV1 Acquire(
        string owner,
        IReadOnlyList<string> resourceFamilies,
        TimeSpan duration,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (resourceFamilies.Count == 0 || resourceFamilies.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty resource family is required.", nameof(resourceFamilies));
        }
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Ownership duration must be 1 tick to 24 hours.");
        }

        string[] resources = resourceFamilies.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_sync)
        {
            RemoveExpired(now);
            OwnershipLeaseV1? conflict = _leases.Values.FirstOrDefault(lease =>
                !string.Equals(lease.Owner, owner, StringComparison.Ordinal)
                && lease.ResourceFamilies.Intersect(resources, StringComparer.OrdinalIgnoreCase).Any());
            if (conflict is not null)
            {
                throw new InvalidOperationException($"Resource ownership is held by '{conflict.Owner}' until {conflict.ExpiresAt:O}.");
            }

            OwnershipLeaseV1 lease = new(
                OwnershipLeaseV1.CurrentSchemaVersion,
                $"lease.{Guid.NewGuid():N}",
                owner,
                resources,
                now,
                now.Add(duration),
                OwnershipState.OwnedByPcHelper,
                "Ownership acquired after verified hardware reset.");
            _leases[lease.Id] = lease;
            return lease;
        }
    }

    public void Release(OwnershipLeaseV1 lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            _leases.Remove(lease.Id);
        }
    }

    /// <summary>
    /// Restores a non-expired persisted lease after a service restart. The
    /// normal conflict check is retained so a corrupt database cannot silently
    /// create overlapping ownership.
    /// </summary>
    public void Restore(OwnershipLeaseV1 lease, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.SchemaVersion != OwnershipLeaseV1.CurrentSchemaVersion
            || lease.State != OwnershipState.OwnedByPcHelper
            || lease.ExpiresAt <= now
            || lease.ResourceFamilies.Count == 0)
        {
            return;
        }
        lock (_sync)
        {
            RemoveExpired(now);
            OwnershipLeaseV1? conflict = _leases.Values.FirstOrDefault(existing =>
                !string.Equals(existing.Id, lease.Id, StringComparison.Ordinal)
                && !string.Equals(existing.Owner, lease.Owner, StringComparison.Ordinal)
                && existing.ResourceFamilies.Intersect(lease.ResourceFamilies, StringComparer.OrdinalIgnoreCase).Any());
            if (conflict is not null)
            {
                throw new InvalidOperationException($"Persisted ownership lease '{lease.Id}' conflicts with '{conflict.Id}'.");
            }
            _leases[lease.Id] = lease;
        }
    }

    public IReadOnlyList<OwnershipLeaseV1> GetActive(DateTimeOffset now)
    {
        lock (_sync)
        {
            RemoveExpired(now);
            return _leases.Values.OrderBy(lease => lease.ExpiresAt).ToArray();
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string id in _leases.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            _leases.Remove(id);
        }
    }
}
