using BambuBar.Core.Updates;

namespace BambuBar.Core.Tests.Updates;

/// <summary>
/// Ed25519 as Sparkle's sign_update makes it. The key here is a throwaway made for these tests with
/// <c>openssl genpkey -algorithm ed25519</c>; its private half was discarded after signing <see cref="Payload"/> with
/// <c>openssl pkeyutl -sign -rawin</c>. The release key never leaves the Mac's keychain.
/// </summary>
public class UpdateSignatureTests
{
    public const string TestPublicKey = "T1JMNx2eBWtFirlVLjcov5TfmPjChZwZmhjpffNrJrI=";
    public const string PayloadSignature = "SYKdTXO6F3iu/zRq7SZvSS5TMfqE9SZJrb2JcEpx1eAz5algCSCqpCRe/SUqY6d6q/+hdWmUqc245AX28yZvCA==";

    /// <summary>The signed "installer": 100,000 bytes, byte i = (i × 31 + 7) mod 256.</summary>
    public static byte[] Payload()
    {
        var bytes = new byte[100_000];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);
        return bytes;
    }

    private static string Write(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bambubar-signature-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task SignedFileVerifies()
    {
        var path = Write(Payload());
        try
        {
            Assert.True(await new UpdateSignature(TestPublicKey).VerifyFileAsync(path, 100_000, PayloadSignature, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TamperedFileWrongLengthAndOtherKeysFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var signature = new UpdateSignature(TestPublicKey);
        var tampered = Payload();
        tampered[54_321] ^= 1;
        var paths = new[] { Write(tampered), Write(Payload()), Write([.. Payload(), 0]) };
        try
        {
            Assert.False(await signature.VerifyFileAsync(paths[0], 100_000, PayloadSignature, ct));
            // The enclosure's length is checked too, before any hashing.
            Assert.False(await signature.VerifyFileAsync(paths[1], 99_999, PayloadSignature, ct));
            Assert.False(await signature.VerifyFileAsync(paths[2], 100_001, PayloadSignature, ct));
            // The release key didn't sign the test payload.
            Assert.False(await new UpdateSignature().VerifyFileAsync(paths[1], 100_000, PayloadSignature, ct));
            Assert.False(await signature.VerifyFileAsync(paths[1] + ".missing", 100_000, PayloadSignature, ct));
            // Malformed signatures are just "not signed".
            Assert.False(await signature.VerifyFileAsync(paths[1], 100_000, "not base64!", ct));
            Assert.False(await signature.VerifyFileAsync(paths[1], 100_000, Convert.ToBase64String(new byte[63]), ct));
            var flipped = Convert.FromBase64String(PayloadSignature);
            flipped[0] ^= 1;
            Assert.False(await signature.VerifyFileAsync(paths[1], 100_000, Convert.ToBase64String(flipped), ct));
        }
        finally
        {
            foreach (var path in paths) File.Delete(path);
        }
    }

    [Fact]
    public void ReleaseKeyIsTheMacsSparkleKey()
    {
        var plist = File.ReadAllText(Path.Combine(Repo.Root, "Resources", "Sparkle-Info.plist"));
        Assert.Contains($"<string>{UpdateSignature.SparklePublicKey}</string>", plist);
        _ = new UpdateSignature();
        Assert.Throws<ArgumentException>(() => new UpdateSignature(Convert.ToBase64String(new byte[31])));
    }
}
