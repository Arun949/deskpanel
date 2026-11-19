// W0 spike: can Kestrel on macOS require a client certificate (TLS 1.2 + 1.3) and identify the client by its key?
// Usage: dotnet run -- <out-dir> [allowed-client-spki]   (writes <out-dir>/server-pin.txt)
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

static string Spki(X509Certificate2? c) => c is null ? "none"
    : Convert.ToBase64String(SHA256.HashData(c.PublicKey.ExportSubjectPublicKeyInfo())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

var outDir = args[0];
var allowed = args.Length > 1 ? args[1] : null;

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var req = new CertificateRequest("CN=DeskPanel", key, HashAlgorithmName.SHA256);
req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
// round trip through PKCS#12: SslStream on macOS needs a key it can use from the keychain layer
var cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12, "w0"), "w0");
File.WriteAllText(Path.Combine(outDir, "server-pin.txt"), Spki(cert));
Console.WriteLine($"server pin {Spki(cert)}; allowed client: {allowed ?? "(any, logged)"}");

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Any, 8766, lo => lo.UseHttps(new HttpsConnectionAdapterOptions
{
    ServerCertificate = cert,
    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
    HandshakeTimeout = TimeSpan.FromSeconds(5),
    ClientCertificateValidation = (c, _, errors) =>
    {
        var ok = allowed is null || Spki(c) == allowed;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} handshake: client {Spki(c)} (chain errors ignored: {errors}) -> {(ok ? "accepted" : "REFUSED")}");
        return ok;
    },
})));
var app = builder.Build();
app.MapGet("/spike", (HttpContext ctx) =>
{
    var tls = ctx.Features.Get<ITlsHandshakeFeature>();
    var r = new { protocol = tls?.Protocol.ToString(), cipher = tls?.NegotiatedCipherSuite?.ToString(), client = Spki(ctx.Connection.ClientCertificate), remote = ctx.Connection.RemoteIpAddress?.ToString() };
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} request: {r}");
    return r;
});
app.Run();
