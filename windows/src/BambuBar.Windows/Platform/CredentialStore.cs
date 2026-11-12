using System.Runtime.InteropServices;
using System.Text;
using BambuBar.Core.App;

namespace BambuBar.Platform;

/// <summary>
/// Access codes in Windows Credential Manager, as generic credentials named "Bambu Bar/&lt;serial&gt;" for the current
/// user (the Mac keeps them in the Keychain). Visible and removable in Control Panel → Credential Manager.
/// </summary>
public sealed partial class CredentialStore : ISecretStore
{
    private const string Prefix = "Bambu Bar/";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private static string Target(string serial) => Prefix + serial.ToUpperInvariant();

    public string? AccessCode(string serial) => Read(Target(serial));

    public void SetAccessCode(string code, string serial) => Write(Target(serial), serial.ToUpperInvariant(), code);

    public void RemoveAccessCode(string serial)
    {
        if (!CredDelete(Target(serial), CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() != ErrorNotFound)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>Any generic credential of this user under <paramref name="target"/>, e.g. the license record.</summary>
    public static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Write(string target, string userName, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        var targetPointer = Marshal.StringToHGlobalUni(target);
        var userPointer = Marshal.StringToHGlobalUni(userName);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPointer,
                UserName = userPointer,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPointer,
                // Local to this computer: an access code means nothing on another one's network.
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.FreeHGlobal(blobPointer);
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(userPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint buffer);
}
