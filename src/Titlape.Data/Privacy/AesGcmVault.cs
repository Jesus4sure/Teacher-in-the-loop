using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Titlape.Data.Privacy;

/// <summary>
/// AES-256-GCM encryption at rest (Layer 1 security pillar). Envelope format:
/// [version:1][nonce:12][tag:16][ciphertext:n]. Authenticated, so tampering is detected on read.
/// </summary>
public sealed class AesGcmVault
{
    private const byte Version = 1;
    private readonly byte[] _key;

    public AesGcmVault(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("AES-256 requires a 32-byte key.");
        _key = key;
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Derives a 256-bit key from a passphrase (PBKDF2-SHA256, 210k iterations).</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 210_000, HashAlgorithmName.SHA256, 32);

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        var output = new byte[1 + 12 + 16 + plaintext.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, 12);
        var tag = output.AsSpan(13, 16);
        var cipher = output.AsSpan(29);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag, associatedData);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> associatedData = default)
    {
        if (envelope.Length < 29 || envelope[0] != Version) throw new CryptographicException("Unknown envelope format.");
        var plaintext = new byte[envelope.Length - 29];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(envelope.Slice(1, 12), envelope[29..], envelope.Slice(13, 16), plaintext, associatedData);
        return plaintext;
    }

    public string EncryptString(string s) => Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(s)));
    public string DecryptString(string b64) => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(b64)));

    public void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Encrypt(JsonSerializer.SerializeToUtf8Bytes(value), Encoding.UTF8.GetBytes(Path.GetFileName(path))));
        File.Move(tmp, path, overwrite: true);
    }

    public T? LoadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var plain = Decrypt(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(Path.GetFileName(path)));
        return JsonSerializer.Deserialize<T>(plain);
    }
}
