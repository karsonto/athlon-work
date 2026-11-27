using System.Security.Cryptography;
using System.Text;

namespace Athlon.Agent.Infrastructure.CloudSandbox;

/// <summary>
/// AES-256-GCM frame: version(1)=0x01 | nonce(12) | ciphertext | tag(16).
/// AAD = UTF-8 "<rpc_method>|&lt;session_id&gt;".
/// </summary>
public static class AesGcmFrame
{
    public const byte Version = 0x01;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int KeyLength = 32;

    public static byte[] ParseSharedKeyBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "CloudSandbox:SharedKey is required (Base64-encoded 32-byte AES key).");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("CloudSandbox:SharedKey is not valid Base64.", ex);
        }

        if (key.Length != KeyLength)
        {
            throw new InvalidOperationException(
                $"CloudSandbox:SharedKey must decode to {KeyLength} bytes, got {key.Length}.");
        }

        return key;
    }

    public static byte[] BuildAad(string rpcMethod, string? sessionId = null)
    {
        var sid = string.IsNullOrWhiteSpace(sessionId) ? "" : sessionId.Trim();
        return Encoding.UTF8.GetBytes(rpcMethod + "|" + sid);
    }

    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext, string rpcMethod, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("AES key must be 32 bytes.", nameof(key));
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        var aad = BuildAad(rpcMethod, sessionId);
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        var frame = new byte[1 + NonceLength + ciphertext.Length + TagLength];
        frame[0] = Version;
        Buffer.BlockCopy(nonce, 0, frame, 1, NonceLength);
        Buffer.BlockCopy(ciphertext, 0, frame, 1 + NonceLength, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, frame, 1 + NonceLength + ciphertext.Length, TagLength);
        return frame;
    }

    public static byte[] Open(byte[] key, ReadOnlySpan<byte> frame, string rpcMethod, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("AES key must be 32 bytes.", nameof(key));
        }

        if (frame.Length < 1 + NonceLength + TagLength || frame[0] != Version)
        {
            throw new CryptographicException("Unsupported or empty secure frame.");
        }

        var nonce = frame.Slice(1, NonceLength);
        var tag = frame.Slice(frame.Length - TagLength, TagLength);
        var ciphertext = frame.Slice(1 + NonceLength, frame.Length - 1 - NonceLength - TagLength);
        var plain = new byte[ciphertext.Length];
        var aad = BuildAad(rpcMethod, sessionId);
        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plain, aad);
        return plain;
    }
}
