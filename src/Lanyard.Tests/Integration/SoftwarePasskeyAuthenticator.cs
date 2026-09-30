using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lanyard.Tests.Integration;

// Stands in for a phone's Face ID / fingerprint authenticator, so the passkey endpoints can be
// driven end to end without a browser. It does exactly what a real platform authenticator does
// with the options the server sends: makes a P-256 key pair ("none" attestation, like iCloud
// Keychain and Google Password Manager) and signs sign-in challenges with it. The output is the
// same JSON lanyardPasskeys.js posts, so the server can't tell the difference.
internal sealed class SoftwarePasskeyAuthenticator : IDisposable
{
    private const byte FlagUserPresent = 0x01;
    private const byte FlagUserVerified = 0x04;
    private const byte FlagAttestedCredentialData = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _origin;
    private uint _signCount;

    public SoftwarePasskeyAuthenticator(string origin)
    {
        _origin = origin;
    }

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    // The WebAuthn user handle - the account id the phone hands back when signing in.
    public string? UserHandle { get; private set; }

    public bool UserVerified { get; set; } = true;

    // navigator.credentials.create(): answers the server's creation options with a new credential.
    public string CreateCredentialJson(string creationOptionsJson)
    {
        JsonNode options = JsonNode.Parse(creationOptionsJson)!;
        string rpId = options["rp"]!["id"]!.GetValue<string>();
        UserHandle = options["user"]!["id"]!.GetValue<string>();

        byte[] clientDataJson = ClientData("webauthn.create", options["challenge"]!.GetValue<string>());

        ECParameters publicKey = _key.ExportParameters(false);
        byte[] cosePublicKey = Cbor.Map(
            (Cbor.Int(1), Cbor.Int(2)),          // kty: EC2
            (Cbor.Int(3), Cbor.Int(-7)),         // alg: ES256
            (Cbor.Int(-1), Cbor.Int(1)),         // crv: P-256
            (Cbor.Int(-2), Cbor.Bytes(publicKey.Q.X!)),
            (Cbor.Int(-3), Cbor.Bytes(publicKey.Q.Y!)));

        using MemoryStream authData = new();
        authData.Write(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.WriteByte((byte)(FlagUserPresent | (UserVerified ? FlagUserVerified : 0) | FlagAttestedCredentialData));
        authData.Write(BigEndian(_signCount));
        authData.Write(new byte[16]); // AAGUID - all zeros, as "none" attestation allows
        authData.Write([(byte)(CredentialId.Length >> 8), (byte)CredentialId.Length]);
        authData.Write(CredentialId);
        authData.Write(cosePublicKey);

        byte[] attestationObject = Cbor.Map(
            (Cbor.Text("fmt"), Cbor.Text("none")),
            (Cbor.Text("attStmt"), Cbor.Map()),
            (Cbor.Text("authData"), Cbor.Bytes(authData.ToArray())));

        return JsonSerializer.Serialize(new
        {
            id = Base64Url.EncodeToString(CredentialId),
            rawId = Base64Url.EncodeToString(CredentialId),
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientDataJson),
                attestationObject = Base64Url.EncodeToString(attestationObject),
                transports = new[] { "internal", "hybrid" },
            },
        });
    }

    // navigator.credentials.get(): signs the server's sign-in challenge.
    public string GetAssertionJson(string requestOptionsJson)
    {
        JsonNode options = JsonNode.Parse(requestOptionsJson)!;
        string rpId = options["rpId"]!.GetValue<string>();

        byte[] clientDataJson = ClientData("webauthn.get", options["challenge"]!.GetValue<string>());

        _signCount++;

        using MemoryStream authData = new();
        authData.Write(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.WriteByte((byte)(FlagUserPresent | (UserVerified ? FlagUserVerified : 0)));
        authData.Write(BigEndian(_signCount));
        byte[] authenticatorData = authData.ToArray();

        byte[] signedData = [.. authenticatorData, .. SHA256.HashData(clientDataJson)];
        byte[] signature = _key.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return JsonSerializer.Serialize(new
        {
            id = Base64Url.EncodeToString(CredentialId),
            rawId = Base64Url.EncodeToString(CredentialId),
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new { },
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientDataJson),
                authenticatorData = Base64Url.EncodeToString(authenticatorData),
                signature = Base64Url.EncodeToString(signature),
                userHandle = UserHandle,
            },
        });
    }

    public void Dispose() => _key.Dispose();

    private byte[] ClientData(string type, string challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin = _origin, crossOrigin = false });

    private static byte[] BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    // Just enough CBOR (RFC 8949) for an attestation object and a COSE key.
    private static class Cbor
    {
        public static byte[] Int(int value) =>
            value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Text(string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            return [.. Head(3, (ulong)utf8.Length), .. utf8];
        }

        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries)
        {
            List<byte> result = [.. Head(5, (ulong)entries.Length)];

            foreach ((byte[] key, byte[] value) in entries)
            {
                result.AddRange(key);
                result.AddRange(value);
            }

            return [.. result];
        }

        private static byte[] Head(int majorType, ulong length)
        {
            byte major = (byte)(majorType << 5);

            return length switch
            {
                < 24 => [(byte)(major | (byte)length)],
                <= byte.MaxValue => [(byte)(major | 24), (byte)length],
                <= ushort.MaxValue => [(byte)(major | 25), (byte)(length >> 8), (byte)length],
                _ => [(byte)(major | 26), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length],
            };
        }
    }
}
