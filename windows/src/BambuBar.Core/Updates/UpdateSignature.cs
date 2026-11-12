using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace BambuBar.Core.Updates;

/// <summary>
/// Checks a download against its appcast item's <c>sparkle:edSignature</c>: Ed25519 (RFC 8032, not prehashed) over
/// the whole file, made on the Mac by Sparkle's <c>sign_update</c> in scripts/appcast.sh. Windows trusts the same key
/// as the Mac (SUPublicEDKey in Resources/Sparkle-Info.plist); its private half never leaves the Mac's keychain.
/// .NET has no Ed25519, so BouncyCastle (MIT) verifies.
/// </summary>
public sealed class UpdateSignature
{
    /// <summary>The Mac's SUPublicEDKey: 32 bytes, Base64.</summary>
    public const string SparklePublicKey = "TwKjQ7USQKbkf0Q0zq6N+17GXDxhmxXlxteHJn0T4jE=";

    private readonly Ed25519PublicKeyParameters _key;

    /// <param name="publicKey">A raw 32-byte Ed25519 public key in Base64 (tests pass a throwaway one).</param>
    public UpdateSignature(string publicKey = SparklePublicKey)
    {
        var bytes = Convert.FromBase64String(publicKey);
        if (bytes.Length != Ed25519PublicKeyParameters.KeySize) throw new ArgumentException("not an Ed25519 public key", nameof(publicKey));
        _key = new Ed25519PublicKeyParameters(bytes);
    }

    /// <summary>True when <paramref name="content"/>, read to its end, carries <paramref name="signature"/> (Base64).
    /// A malformed signature is false. The reads resume off the caller's context, so hashing a large installer doesn't
    /// hold up the UI thread.</summary>
    public async Task<bool> VerifyAsync(Stream content, string signature, CancellationToken cancellation = default)
    {
        if (Decode(signature) is not { } expected) return false;
        var signer = new Ed25519Signer();
        signer.Init(false, _key);
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0) signer.BlockUpdate(buffer, 0, read);
        return signer.VerifySignature(expected);
    }

    /// <summary>True when the file at <paramref name="path"/> is exactly <paramref name="length"/> bytes long and
    /// carries <paramref name="signature"/>. A missing file is false.</summary>
    public async Task<bool> VerifyFileAsync(string path, long length, string signature, CancellationToken cancellation = default)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        await using (file.ConfigureAwait(false))
        {
            return file.Length == length && await VerifyAsync(file, signature, cancellation).ConfigureAwait(false);
        }
    }

    private static byte[]? Decode(string signature)
    {
        try
        {
            var bytes = Convert.FromBase64String(signature);
            return bytes.Length == 64 ? bytes : null; // R and S, 32 bytes each
        }
        catch (FormatException) { return null; }
    }
}
