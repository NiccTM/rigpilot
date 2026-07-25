namespace PCHelper.Core;

/// <summary>
/// Recognises whether mainstream anti-cheat software is currently running, so features
/// that synthesize input can refuse rather than risk the user an anti-cheat action or
/// the program a behavioural false-positive flag. RigPilot never injects into a game or
/// reads its memory; this is the belt-and-braces guard for the one thing that could still
/// look like a cheat — synthesizing keyboard and mouse input while a protected game is up.
///
/// The classification is pure: the caller supplies the running process names, so the rule
/// is unit-testable without a live process list. Matching is by name against a curated set
/// of documented anti-cheat client and kernel-service processes, case-insensitively and
/// ignoring a single trailing extension (.exe/.sys/.des).
/// </summary>
public static class AntiCheatDetection
{
    // Publicly documented anti-cheat client/service/driver process names. Stored without
    // an extension; the matcher strips one before comparing.
    private static readonly HashSet<string> ProtectionProcessStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Setup", // Epic Online Services EAC
        "BEService", "BEDaisy",                                      // BattlEye
        "vgc", "vgtray", "vgk",                                      // Riot Vanguard
        "GameMon", "npggNT", "GameGuard",                            // nProtect GameGuard
        "xhunter1", "XIGNCODE", "xigncode3",                         // Wellbia Xigncode3
        "FACEITService", "faceitclient",                            // FACEIT Anti-cheat
        "ESEAClient",                                                // ESEA
        "mhyprot", "mhyprot2", "mhyprot3",                           // HoYoverse mhyprot
        "anticheatexpert", "ACE-Tray", "SGuard", "SGuard64",         // Tencent ACE
    };

    /// <summary>
    /// Returns the name of an active anti-cheat process if one is running, otherwise null.
    /// The returned name is the matched stem, suitable for a user-facing message.
    /// </summary>
    public static string? FindActiveProtection(IEnumerable<string> runningProcessNames)
    {
        ArgumentNullException.ThrowIfNull(runningProcessNames);
        foreach (string raw in runningProcessNames)
        {
            string stem = Stem(raw);
            if (stem.Length > 0 && ProtectionProcessStems.TryGetValue(stem, out string? canonical))
            {
                return canonical;
            }
        }

        return null;
    }

    private static string Stem(string? processName)
    {
        string trimmed = (processName ?? string.Empty).Trim();
        int dot = trimmed.LastIndexOf('.');
        if (dot > 0 && trimmed.Length - dot <= 4)
        {
            trimmed = trimmed[..dot];
        }

        return trimmed;
    }
}
