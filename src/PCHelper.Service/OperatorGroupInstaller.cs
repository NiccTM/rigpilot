using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PCHelper.Service;

internal static class OperatorGroupInstaller
{
    private const string GroupName = "PC Helper Operators";
    private const int Success = 0;
    private const int GroupExists = 2223;
    private const int MemberAlreadyPresent = 1378;
    private const int UserAlreadyInGroup = 2236;

    /// <summary>Creates the operators group if missing. Safe to call on every service start.</summary>
    public static int EnsureGroup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        LocalGroupInfo group = new()
        {
            Name = GroupName,
            Comment = "Users authorised to operate the local RigPilot service (legacy PCHelper identity)"
        };
        int result = NetLocalGroupAdd(null, 1, ref group, out _);
        return result is Success or GroupExists ? Success : result;
    }

    public static int EnsureGroupAndMember(string operatorSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(operatorSid) || operatorSid.Length > 184)
        {
            return 2;
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(operatorSid);
        }
        catch (ArgumentException)
        {
            return 3;
        }

        int result = EnsureGroup();
        if (result is not Success)
        {
            Console.Error.WriteLine(new Win32Exception(result).Message);
            return result;
        }

        byte[] sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        GCHandle sidHandle = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
        try
        {
            LocalGroupMemberInfo member = new() { Sid = sidHandle.AddrOfPinnedObject() };
            result = NetLocalGroupAddMembers(null, GroupName, 0, ref member, 1);
        }
        finally
        {
            sidHandle.Free();
        }
        if (result is Success or MemberAlreadyPresent or UserAlreadyInGroup)
        {
            return Success;
        }

        Console.Error.WriteLine(new Win32Exception(result).Message);
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMemberInfo
    {
        public IntPtr Sid;
    }

#pragma warning disable SYSLIB1054 // Struct marshalling is clearer and audited for this one-shot installer path.
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAdd(
        string? serverName,
        int level,
        ref LocalGroupInfo buffer,
        out int parameterError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(
        string? serverName,
        string groupName,
        int level,
        ref LocalGroupMemberInfo buffer,
        int totalEntries);
#pragma warning restore SYSLIB1054
}
