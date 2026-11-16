// SPDX-License-Identifier: GPL-2.0-or-later
// One-click password connection trust policy for the standalone Preview.
// TLS is mandatory. No unencrypted RFB or password channel exists.
// First-use self-signed Server certificate can complete the TLS handshake,
// but it MUST be shown to the operator and explicitly approved BEFORE
// a PasswordLogin packet or any other application-level secret is sent.
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace TJ.RemoteAccess.Lab;

internal sealed record LabQuickPasswordRequest(string Host, int Port, string Password);

internal static class LabQuickPasswordTrust
{
    internal static string? ExamineServer(X509Certificate? certificate, string? knownPin)
    {
        if (certificate is null) return null;
        string actual = LabCertificates.Hash(certificate);
        string expected = string.IsNullOrEmpty(knownPin) ? actual : knownPin;
        return LabCertificates.ValidatePinned(certificate, expected, LabCertificates.ServerEku)
            ? actual : null;
    }

    // First-use preflight: establish an encrypted TLS channel, inspect the
    // structurally valid public Server certificate, then CLOSE the connection.
    // We never send PasswordLogin (or ANY application packet) in this phase.
    // The operator may compare SHA-256 at leisure; the subsequent real TLS
    // connection MUST enforce that exact pin, protecting against certificate
    // swaps between preflight and password submission.
    internal static async Task<string> ProbeServerIdentityAsync(
        string host, int port, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, token).AsTask().ConfigureAwait(false);
        using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        string? observed = null;
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", // TJRA self-signed Server certificate uses localhost SAN
            ClientCertificates = null, // password port never requires a Viewer certificate
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                observed = ExamineServer(cert, knownPin: null);
                return observed is not null;
            }
        };
        await tls.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
        // A successful certificate probe does NOT grant permission to send a
        // password. The UI must obtain independent human approval first.
        if (!tls.IsAuthenticated || !tls.IsEncrypted || observed is null)
            throw new AuthenticationException("First-use Server TLS identity is invalid.");
        return observed!;
    }

    internal static bool MustConfirmFirstUse(string? knownPin) =>
        string.IsNullOrEmpty(knownPin);

    internal static bool MaySendPassword(bool tlsAuthenticated, bool tlsEncrypted,
                                         string? observedPin, bool confirmedOrPreviouslyPinned) =>
        tlsAuthenticated && tlsEncrypted &&
        confirmedOrPreviouslyPinned &&
        observedPin is { Length: 64 } &&
        observedPin.All(Uri.IsHexDigit);
}
