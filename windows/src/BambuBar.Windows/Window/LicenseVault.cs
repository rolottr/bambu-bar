using System.ComponentModel;
using System.Runtime.InteropServices;
using BambuBar.Core.Licensing;
using BambuBar.Platform;

namespace BambuBar.AppWindow;

/// <summary>
/// The supporter license record in Windows Credential Manager (the Mac's Keychain item): one generic credential,
/// "Bambu Bar License", for this user on this PC, holding <see cref="LicenseRecord.ToJson"/>. Belongs in Platform/
/// beside <see cref="CredentialStore"/> (P5.3); here so the About page's license section works now.
/// </summary>
public sealed partial class CredentialLicenseVault : ILicenseVault
{
    private const string Target = "Bambu Bar License";
    private const int CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;

    public LicenseRecord? Load() => CredentialStore.Read(Target) is { Length: > 0 } json ? LicenseRecord.FromJson(json) : null;

    public void Save(LicenseRecord? record)
    {
        if (record is not null)
        {
            CredentialStore.Write(Target, "Bambu Bar", record.ToJson());
            return;
        }
        if (!CredDelete(Target, CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() != ErrorNotFound)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);
}
