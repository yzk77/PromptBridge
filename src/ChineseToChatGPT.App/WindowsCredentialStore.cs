using System.ComponentModel;
using System.Runtime.InteropServices;
using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

internal sealed class WindowsCredentialStore : ICredentialStore
{
    private const string TargetPrefix = "ChineseToChatGPT/";
    private const uint CredentialTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;

    public string? Get(string credentialName)
    {
        var targetName = GetTargetName(credentialName);
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            return credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(
                    credential.CredentialBlob,
                    checked((int)credential.CredentialBlobSize / sizeof(char)));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Set(string credentialName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var targetName = GetTargetName(credentialName);
        var bytes = checked((uint)(value.Length * sizeof(char)));
        var blob = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = bytes,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string credentialName)
    {
        var targetName = GetTargetName(credentialName);
        if (!CredDelete(targetName, CredentialTypeGeneric, 0) && Marshal.GetLastWin32Error() != 1168)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string GetTargetName(string credentialName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        if (credentialName.Any(static character =>
            !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Credential names may only contain letters, numbers, hyphens, and underscores.");
        }

        return TargetPrefix + credentialName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
