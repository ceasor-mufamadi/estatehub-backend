using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EstateHub.Tests.Infrastructure;

/// <summary>
/// A minimal WebAuthn authenticator in software (like a phone or Touch ID, without the hardware): it
/// answers navigator.credentials.create/get with "none" attestation and ES256 signatures, so tests
/// can drive the real server-side verification end to end.
/// </summary>
public sealed class SoftwareAuthenticator : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(32);
    private readonly string _origin;
    private byte[] _userHandle = [];
    private uint _signCount;

    public SoftwareAuthenticator(string origin = "http://localhost:3000") => _origin = origin;

    /// <summary>The browser's credential.toJSON() after navigator.credentials.create(options).</summary>
    public string Create(string creationOptionsJson)
    {
        var options = JsonNode.Parse(creationOptionsJson)!;
        var rpId = options["rp"]!["id"]!.GetValue<string>();
        _userHandle = FromBase64Url(options["user"]!["id"]!.GetValue<string>());
        var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>());

        var p = _key.ExportParameters(false);
        var coseKey = Cbor.Map((Cbor.Int(1), Cbor.Int(2)), (Cbor.Int(3), Cbor.Int(-7)), (Cbor.Int(-1), Cbor.Int(1)),
            (Cbor.Int(-2), Cbor.Bytes(p.Q.X!)), (Cbor.Int(-3), Cbor.Bytes(p.Q.Y!)));
        var attested = Concat(new byte[16], [(byte)(_credentialId.Length >> 8), (byte)_credentialId.Length], _credentialId, coseKey);
        var authData = Concat(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), [0x45], Counter(), attested); // UP + UV + AT
        var attestation = Cbor.Map((Cbor.Text("fmt"), Cbor.Text("none")), (Cbor.Text("attStmt"), Cbor.Map()), (Cbor.Text("authData"), Cbor.Bytes(authData)));

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = ToBase64Url(clientData),
            ["attestationObject"] = ToBase64Url(attestation),
            ["transports"] = new JsonArray("internal"),
        });
    }

    /// <summary>The browser's credential.toJSON() after navigator.credentials.get(options).</summary>
    public string Get(string requestOptionsJson, string? origin = null)
    {
        var options = JsonNode.Parse(requestOptionsJson)!;
        var rpId = options["rpId"]!.GetValue<string>();
        var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>(), origin);
        _signCount++;
        var authData = Concat(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), [0x05], Counter()); // UP + UV
        var signature = _key.SignData(Concat(authData, SHA256.HashData(clientData)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = ToBase64Url(clientData),
            ["authenticatorData"] = ToBase64Url(authData),
            ["signature"] = ToBase64Url(signature),
            ["userHandle"] = ToBase64Url(_userHandle),
        });
    }

    public void Dispose() => _key.Dispose();

    private byte[] ClientData(string type, string challenge, string? origin = null) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin = origin ?? _origin, crossOrigin = false }));

    private string Credential(JsonObject response) => new JsonObject
    {
        ["id"] = ToBase64Url(_credentialId),
        ["rawId"] = ToBase64Url(_credentialId),
        ["type"] = "public-key",
        ["response"] = response,
        ["clientExtensionResults"] = new JsonObject(),
        ["authenticatorAttachment"] = "platform",
    }.ToJsonString();

    private byte[] Counter() => [(byte)(_signCount >> 24), (byte)(_signCount >> 16), (byte)(_signCount >> 8), (byte)_signCount];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>Just enough CBOR (RFC 8949) for WebAuthn: ints, byte/text strings, maps.</summary>
    private static class Cbor
    {
        public static byte[] Int(long value) => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] data) => Concat(Head(2, (ulong)data.Length), data);

        public static byte[] Text(string text) => Concat(Head(3, (ulong)Encoding.UTF8.GetByteCount(text)), Encoding.UTF8.GetBytes(text));

        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries) =>
            Concat([Head(5, (ulong)entries.Length), .. entries.SelectMany(e => new[] { e.Key, e.Value })]);

        private static byte[] Head(int major, ulong n) => n switch
        {
            < 24 => [(byte)((major << 5) | (int)n)],
            <= byte.MaxValue => [(byte)((major << 5) | 24), (byte)n],
            <= ushort.MaxValue => [(byte)((major << 5) | 25), (byte)(n >> 8), (byte)n],
            _ => [(byte)((major << 5) | 26), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n],
        };
    }
}
