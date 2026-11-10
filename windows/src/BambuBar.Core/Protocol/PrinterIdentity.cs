using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace BambuBar.Core.Protocol;

/// <summary>
/// Finds out which printer answers at an address, for a printer added by its IP address alone: its certificate chains
/// to Bambu Lab and is named after its serial. Only the TLS handshake happens; the access code isn't sent.
/// </summary>
public static class PrinterIdentity
{
    public abstract record Failure
    {
        public sealed record NotBambu(string Reason) : Failure;
        public sealed record Unreachable(string Reason) : Failure;
        public sealed record TimedOut : Failure;

        public string Message => this switch
        {
            NotBambu n => Loc.F("That address didn't answer as a Bambu Lab printer: %@", n.Reason),
            Unreachable u => Loc.F("%@ Check the IP address on the printer's screen (Settings → Network).", u.Reason),
            _ => Loc.T("Nothing answered at that address. Check the IP address on the printer's screen (Settings → Network)."),
        };
    }

    /// <summary>The serial (uppercase) of the printer at <paramref name="host"/>, or why there isn't one.</summary>
    public static async Task<(string? Serial, Failure? Failure)> SerialAsync(string host, int port, bool insecure,
        TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        overall.CancelAfter(limit + TimeSpan.FromSeconds(4));
        BambuTrust.Identity? identity = null;
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
            {
                connect.CancelAfter(limit);
                try { await socket.ConnectAsync(host, port, connect.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!overall.IsCancellationRequested)
                {
                    return (null, new Failure.Unreachable(Loc.T("The printer didn't answer.")));
                }
            }
            await using var stream = new SslStream(new NetworkStream(socket, ownsSocket: false));
            var options = MqttClient.TlsOptions(host, (leaf, presented) =>
            {
                if (leaf is null) { identity = new BambuTrust.Identity.Rejected(Loc.T("The printer sent no certificate.")); return false; }
                // The emulators' certificates come from their own CA; their names are still the serials.
                identity = insecure ? new BambuTrust.Identity.Genuine(BambuTrust.CommonName(leaf)) : BambuTrust.Identify(leaf, presented);
                return identity is BambuTrust.Identity.Genuine;
            });
            await stream.AuthenticateAsClientAsync(options, overall.Token).ConfigureAwait(false);
            return identity is BambuTrust.Identity.Genuine { Name: { Length: > 0 } name }
                ? (name.ToUpperInvariant(), null)
                : (null, new Failure.NotBambu(Loc.T("its certificate doesn't name it.", "After “That address didn't answer as a Bambu Lab printer:”")));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return (null, new Failure.TimedOut());
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            if (identity is BambuTrust.Identity.Rejected rejected) return (null, new Failure.NotBambu(rejected.Reason));
            var socketError = (e as SocketException ?? e.InnerException as SocketException)?.SocketErrorCode;
            return (null, new Failure.Unreachable(socketError is { } code ? MqttClient.Describe(code, e.Message) : e.Message));
        }
        catch (SocketException e)
        {
            return (null, new Failure.Unreachable(MqttClient.Describe(e.SocketErrorCode, e.Message)));
        }
    }
}
