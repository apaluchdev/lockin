using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace LockIn.Core.Lcu;

/// <summary>
/// The LCU serves HTTPS on 127.0.0.1 with a certificate signed by Riot's own local CA,
/// which Windows does not trust. Rather than accepting any certificate, we accept only
/// one issued by that CA. LockIn never sends LCU credentials anywhere but 127.0.0.1.
/// </summary>
internal static class LcuCertificate
{
    private const string RiotLocalCaCommonName = "CN=LoL Game Engineering Certificate Authority";

    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        return certificate.Issuer.Contains(RiotLocalCaCommonName, StringComparison.Ordinal);
    }

    public static RemoteCertificateValidationCallback Callback { get; } =
        (_, certificate, _, errors) => Validate(certificate, errors);
}
