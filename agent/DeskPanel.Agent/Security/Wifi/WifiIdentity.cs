using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DeskPanel.Agent.Core;

namespace DeskPanel.Agent.Security.Wifi;

/// <summary>
/// The agent's Wi-Fi key (docs/design/WIFI.md §3.3): ECDSA P-256, made on first use, kept in <c>wifi-key.pem</c>
/// (owner-only; on Windows also DPAPI-encrypted for the user). Phones pin <see cref="Pin"/>, the hash of the public key,
/// so the certificate around it can be re-made at every start without touching phones.
/// </summary>
public sealed class WifiIdentity
{
    public const string FileName = "wifi-key.pem";
    private const string DpapiLabel = "DESKPANEL DPAPI PRIVATE KEY";

    private readonly ECDsa _key;
    private readonly Lazy<X509Certificate2> _certificate;

    public WifiIdentity(AgentPaths paths)
    {
        var path = Path.Combine(paths.Home, FileName);
        _key = File.Exists(path) ? Load(path) : Create(path);
        Pin = SpkiPin(_key.ExportSubjectPublicKeyInfo());
        _certificate = new Lazy<X509Certificate2>(MakeCertificate);
    }

    /// <summary>base64url(SHA-256(SubjectPublicKeyInfo)): what a phone pins.</summary>
    public string Pin { get; }

    /// <summary>A self-signed server certificate around the key, valid for a year from this start.</summary>
    public X509Certificate2 Certificate => _certificate.Value;

    /// <summary>The pin of any certificate's public key (the phone's client certificate, or ours).</summary>
    public static string SpkiPin(X509Certificate2 certificate) => SpkiPin(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    public static string SpkiPin(byte[] subjectPublicKeyInfo) => DeviceStore.Base64Url(SHA256.HashData(subjectPublicKeyInfo));

    private X509Certificate2 MakeCertificate()
    {
        // no computer name in the certificate: a network scan learns nothing about the machine (WIFI.md §3.3)
        var request = new CertificateRequest("CN=DeskPanel", _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // the TLS server on macOS (and Windows) needs the key behind the certificate in its own key store:
        // a PKCS#12 round trip gets it there (W0 finding 2). The password only lives for this call.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12, password), password);
    }

    private static ECDsa Create(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var pem = OperatingSystem.IsWindows()
            ? PemEncoding.WriteString(DpapiLabel, Dpapi.Protect(pkcs8))
            : PemEncoding.WriteString("PRIVATE KEY", pkcs8);
        CryptographicOperations.ZeroMemory(pkcs8);
        PrivateFile.WriteAtomic(path, pem + "\n");
        return key;
    }

    private static ECDsa Load(string path)
    {
        var text = File.ReadAllText(path);
        var fields = PemEncoding.Find(text);
        var label = text[fields.Label];
        var data = Convert.FromBase64String(text[fields.Base64Data]);
        var pkcs8 = label switch
        {
            DpapiLabel when OperatingSystem.IsWindows() => Dpapi.Unprotect(data),
            "PRIVATE KEY" => data,
            _ => throw new CryptographicException($"{FileName}: unexpected key type '{label}'"),
        };
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        CryptographicOperations.ZeroMemory(pkcs8);
        if (key.KeySize != 256) throw new CryptographicException($"{FileName}: not a P-256 key");
        return key;
    }
}

/// <summary>Windows DPAPI for the current user, through crypt32 directly (no extra package).</summary>
internal static partial class Dpapi
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("deskpanel|wifi-key|v1");

    [SupportedOSPlatform("windows")]
    public static byte[] Protect(byte[] data) => Run(data, protect: true);

    [SupportedOSPlatform("windows")]
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static unsafe byte[] Run(byte[] data, bool protect)
    {
        fixed (byte* pData = data)
        fixed (byte* pEntropy = Entropy)
        {
            var input = new Blob { Size = (uint)data.Length, Data = (nint)pData };
            var entropy = new Blob { Size = (uint)Entropy.Length, Data = (nint)pEntropy };
            const uint UiForbidden = 0x1;
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropy, 0, 0, UiForbidden, out var output)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, UiForbidden, out output);
            if (!ok) throw new CryptographicException($"DPAPI failed: {Marshal.GetLastPInvokeError()}");
            try { return new ReadOnlySpan<byte>((void*)output.Data, (int)output.Size).ToArray(); }
            finally { LocalFree(output.Data); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public uint Size; public nint Data; }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint handle);
}
