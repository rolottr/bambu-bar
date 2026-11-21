using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BambuBar.Core.App;

namespace BambuBar.Platform;

/// <summary>
/// Access codes in Windows Credential Manager, as generic credentials named "Bambu Bar/&lt;serial&gt;" for the current
/// user (the Mac keeps them in the Keychain). Visible and removable in Control Panel → Credential Manager.
/// When Credential Manager refuses a code, most often because it is full ("Not enough memory resources are available
/// to process this command", error 8: other apps' logins fill its per-user space), the code is kept in
/// <c>%LOCALAPPDATA%\Bambu Bar\access-codes.json</c> instead, encrypted for this Windows user (DPAPI, the protection
/// Credential Manager itself uses). A later save that Credential Manager accepts moves it back there.
/// </summary>
public sealed partial class CredentialStore : ISecretStore
{
    private const string Prefix = "Bambu Bar/";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private static readonly object FallbackLock = new();

    /// <summary>Where codes go when Credential Manager refuses them (tests point it elsewhere).</summary>
    public string FallbackPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bambu Bar", "access-codes.json");

    private static string Target(string serial) => Prefix + serial.ToUpperInvariant();

    public string? AccessCode(string serial) => Read(Target(serial)) ?? FallbackCode(serial.ToUpperInvariant());

    public void SetAccessCode(string code, string serial)
    {
        var key = serial.ToUpperInvariant();
        try
        {
            Write(Target(serial), key, code);
        }
        catch (System.ComponentModel.Win32Exception refused)
        {
            // Credential Manager is full or refuses: keep the code ourselves. Only when that fails too is it reported.
            try { UpdateFallback(key, code); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(refused);
            }
            return;
        }
        // Saved where it belongs: drop a copy kept while Credential Manager was full.
        if (FallbackCode(key) is not null) TryUpdateFallback(key, null);
    }

    public void RemoveAccessCode(string serial)
    {
        TryUpdateFallback(serial.ToUpperInvariant(), null);
        if (!CredDelete(Target(serial), CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() != ErrorNotFound)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    // MARK: The fallback file: { "SERIAL": "<base64 of the DPAPI-protected UTF-16 code>" }

    private string? FallbackCode(string key)
    {
        lock (FallbackLock)
        {
            if (!LoadFallback().TryGetValue(key, out var sealed_)) return null;
            try { return Encoding.Unicode.GetString(Unprotect(Convert.FromBase64String(sealed_))); }
            catch (Exception e) when (e is FormatException or System.ComponentModel.Win32Exception) { return null; }
        }
    }

    private void TryUpdateFallback(string key, string? code)
    {
        try { UpdateFallback(key, code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    private void UpdateFallback(string key, string? code)
    {
        lock (FallbackLock)
        {
            var codes = LoadFallback();
            if (code is null)
            {
                if (!codes.Remove(key)) return;
            }
            else
            {
                codes[key] = Convert.ToBase64String(Protect(Encoding.Unicode.GetBytes(code)));
            }
            if (codes.Count == 0)
            {
                File.Delete(FallbackPath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
            FileWrite.Atomic(FallbackPath, System.Text.Json.JsonSerializer.Serialize(codes));
        }
    }

    private Dictionary<string, string> LoadFallback()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FallbackPath))
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static byte[] Protect(byte[] data) => Crypt(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    /// <summary>DPAPI for the current user: only this Windows account can read it back.</summary>
    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = default(DataBlob);
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            const int uiForbidden = 0x1;
            var ok = protect
                ? CryptProtectData(ref input, null, 0, 0, 0, uiForbidden, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, uiForbidden, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0) LocalFree(output.Data);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, nint entropy, nint reserved,
        nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, nint entropy, nint reserved,
        nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
