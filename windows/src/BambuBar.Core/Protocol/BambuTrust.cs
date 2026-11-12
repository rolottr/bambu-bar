using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BambuBar.Core.Protocol;

/// <summary>
/// Decides whether a printer's TLS certificate is genuine. Bambu printers present a leaf named after their serial
/// number, issued by a per-model "BBL Device CA" that chains to Bambu's own roots ("BBL CA", "BBL CA2 RSA/ECC").
/// Those ship with the app (Assets\Certificates); the host name isn't checked because the certificate names the
/// serial, not the IP address. Revocation isn't checked either: printers on a LAN have nothing to ask.
/// </summary>
public static class BambuTrust
{
    public abstract record Verdict
    {
        /// <summary>Chains to a bundled Bambu root and is named after the expected printer.</summary>
        public sealed record Trusted : Verdict;
        public sealed record Rejected(string Reason) : Verdict;

        public bool IsRejected => this is Rejected;
    }

    public abstract record Identity
    {
        /// <summary>Chains to a bundled Bambu root; the leaf's common name is the printer's serial.</summary>
        public sealed record Genuine(string? Name) : Identity;
        public sealed record Rejected(string Reason) : Identity;
    }

    private static readonly Lazy<(X509Certificate2Collection Roots, X509Certificate2Collection Intermediates)> Bundled = new(Load);

    /// <summary>The most recent verdict, for <c>--probe</c> diagnostics.</summary>
    public static Verdict? LastVerdict { get; private set; }

    /// <summary>The bundled self-signed roots and the certificates that chain to them.</summary>
    public static int BundledCount => Bundled.Value.Roots.Count + Bundled.Value.Intermediates.Count;

    private static (X509Certificate2Collection, X509Certificate2Collection) Load()
    {
        var roots = new X509Certificate2Collection();
        var intermediates = new X509Certificate2Collection();
        var folder = Assets.PathOf("Certificates");
        if (!Directory.Exists(folder)) return (roots, intermediates);
        foreach (var file in Directory.GetFiles(folder, "*.pem").Order(StringComparer.Ordinal))
        {
            var all = new X509Certificate2Collection();
            try { all.ImportFromPemFile(file); }
            catch (CryptographicException) { continue; }
            foreach (var cert in all)
            {
                // Self-signed ones anchor the chain; cross-signed and per-model CAs only help build it.
                (cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData) ? roots : intermediates).Add(cert);
            }
        }
        return (roots, intermediates);
    }

    /// <summary>Both conditions matter: the chain proves it's a Bambu device, the name proves it's <i>this</i> printer
    /// (another Bambu printer on the network must not be able to collect the access code).</summary>
    public static Verdict Evaluate(X509Certificate2 leaf, IEnumerable<X509Certificate2> presented, string serial)
    {
        Verdict verdict = Identify(leaf, presented) switch
        {
            Identity.Rejected r => new Verdict.Rejected(r.Reason),
            Identity.Genuine { Name: { } name } when string.Equals(name, serial, StringComparison.OrdinalIgnoreCase) => new Verdict.Trusted(),
            Identity.Genuine g => new Verdict.Rejected(
                Loc.F("The certificate belongs to another device (%@).", g.Name ?? Loc.T("unnamed"))),
            _ => new Verdict.Rejected(Loc.T("The certificate isn't from Bambu Lab.")),
        };
        LastVerdict = verdict;
        return verdict;
    }

    /// <summary>Checks the chain alone and reads the name, which is how a printer added by its address alone is told
    /// apart. <paramref name="presented"/> are the intermediates the printer sent with its leaf.</summary>
    public static Identity Identify(X509Certificate2? leaf, IEnumerable<X509Certificate2> presented)
    {
        if (leaf is null) return new Identity.Rejected(Loc.T("The printer sent no certificate."));
        var (roots, intermediates) = Bundled.Value;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        foreach (var cert in presented) chain.ChainPolicy.ExtraStore.Add(cert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (!chain.Build(leaf))
        {
            var reason = chain.ChainStatus.Select(s => s.StatusInformation.Trim()).FirstOrDefault(s => s.Length > 0);
            return new Identity.Rejected(reason ?? Loc.T("The certificate isn't from Bambu Lab."));
        }
        return new Identity.Genuine(CommonName(leaf));
    }

    /// <summary>The leaf's common name, whether or not the chain checks out.</summary>
    public static string? CommonName(X509Certificate2 leaf)
    {
        var name = leaf.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
