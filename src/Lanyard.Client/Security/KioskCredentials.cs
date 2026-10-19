using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.Kiosk;

namespace Lanyard.Client.Security;

// This kiosk's own credential, encrypted with DPAPI for the Windows user the kiosk runs as, so
// copying the file to another PC (or reading it as another user) gets nothing. Kept next to
// client-id.txt, outside the install folder, so updates don't lose it. If it can't be decrypted
// (the Windows profile was reset, say) it's deleted and the kiosk pairs again.
public sealed class DpapiKioskCredentialStore(string path) : IKioskCredentialStore
{
    // Ties the encrypted blob to this purpose, so it can't be swapped for another DPAPI blob of
    // the same user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lanyard.KioskCredential.v1");

    public string? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            Console.WriteLine($"The saved kiosk credential couldn't be read ({ex.Message}); this kiosk will pair again.");
            Delete();

            return null;
        }
    }

    public void Save(string credential)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(credential), Entropy, DataProtectionScope.CurrentUser);

        // Written beside and moved over, so a power cut mid-write can't leave half a file.
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Couldn't delete the saved kiosk credential: {ex.Message}");
        }
    }
}

// The credential the rest of the kiosk uses for every request to the server.
public static class KioskCredentials
{
    private const string LegacySecretVariable = "LANYARD_CLIENT_SHARED_SECRET";

    // The default every kiosk shipped with before 1.0.43 - never a real secret.
    private const string OldDefaultSecret = "changeme";

    private static string? _current;

    public static IKioskCredentialStore Store { get; } = new DpapiKioskCredentialStore(Path.Combine(ClientIdentity.DataDirectory, "credential.bin"));

    public static string? Current => _current;

    // The old shared secret, while this kiosk still has one configured (it's removed once swapped).
    public static string? LegacySecret
    {
        get
        {
            string? secret = Environment.GetEnvironmentVariable(LegacySecretVariable);

            return string.IsNullOrWhiteSpace(secret) || secret == OldDefaultSecret ? null : secret;
        }
    }

    public static void Use(string credential) => _current = credential;

    public static string? LoadSaved() => _current = Store.Load();

    // Saves a new credential (a rotation) and starts using it.
    public static void Replace(string credential)
    {
        Store.Save(credential);
        _current = credential;
    }

    // The server says this credential is dead. The kiosk restarts and pairs again.
    public static void Forget()
    {
        Store.Delete();
        _current = null;
    }

    // Its own credential when it has one, otherwise the old shared secret.
    public static void ApplyTo(HttpRequestMessage request)
    {
        if (_current is string credential)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
        else if (LegacySecret is string secret)
        {
            request.Headers.Add(ClientConnectionQuery.SecretHeader, secret);
        }
    }
}
