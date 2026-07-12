using System.Security.Cryptography;
using System.Text;

namespace PCHelper.Adapters;

internal static class StableIds
{
    public static string Create(string prefix, params string?[] parts)
    {
        string source = string.Join('|', parts.Select(part => part?.Trim().ToUpperInvariant() ?? string.Empty));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }
}
