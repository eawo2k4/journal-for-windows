using System.Security.Cryptography;
using System.Text;

namespace WindowsJournal.Services;

/// <summary>
/// The encryption behind automatic backups. Unlike the journal itself (DPAPI, tied to one Windows account on one PC),
/// a backup is encrypted with a random secret, shown to the writer once as a recovery key, so it can be opened anywhere.
/// </summary>
public static class BackupCrypto
{
    // 160 random bits: far beyond guessing, and short enough to type in once.
    private const int SecretLength = 20;
    private const int ChecksumLength = 2;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private static readonly byte[] Magic = "WJB1"u8.ToArray();
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("WindowsJournal backup v1");

    // Crockford base32: no I, L, O or U, so a handwritten key can't be misread.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    public static bool IsBackup(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    /// <summary>The secret as groups of four characters, with a checksum so typing mistakes can be caught.</summary>
    public static string ToRecoveryKey(byte[] secret)
    {
        byte[] data = [.. secret, .. SHA256.HashData(secret).AsSpan(0, ChecksumLength)];
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return string.Join('-', text.ToString().Chunk(4).Select(group => new string(group)));
    }

    /// <summary>Reads a typed or pasted recovery key. Fails on anything mistyped, ignoring case, spaces and dashes.</summary>
    public static bool TryParseRecoveryKey(string? text, out byte[] secret)
    {
        secret = [];
        if (text is null) return false;

        var data = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is '-' || char.IsWhiteSpace(raw)) continue;

            var c = char.ToUpperInvariant(raw) switch { 'O' => '0', 'I' or 'L' => '1', var other => other };
            var value = Alphabet.IndexOf(c);
            if (value < 0) return false;

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                data.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        if (data.Count != SecretLength + ChecksumLength) return false;

        var candidate = data.Take(SecretLength).ToArray();
        if (!SHA256.HashData(candidate).AsSpan(0, ChecksumLength).SequenceEqual(data.Skip(SecretLength).ToArray())) return false;

        secret = candidate;
        return true;
    }

    public static byte[] Encrypt(byte[] plain, byte[] secret)
    {
        var result = new byte[Magic.Length + NonceLength + TagLength + plain.Length];
        var span = result.AsSpan();
        Magic.CopyTo(span);
        var nonce = span.Slice(Magic.Length, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        var tag = span.Slice(Magic.Length + NonceLength, TagLength);
        var cipher = span[(Magic.Length + NonceLength + TagLength)..];

        using var aes = new AesGcm(DeriveKey(secret), TagLength);
        aes.Encrypt(nonce, plain, cipher, tag, Magic);
        return result;
    }

    /// <exception cref="CryptographicException">Wrong key, or the file was damaged or changed.</exception>
    public static byte[] Decrypt(byte[] data, byte[] secret)
    {
        if (!IsBackup(data) || data.Length < Magic.Length + NonceLength + TagLength)
        {
            throw new InvalidDataException("This file isn't a Journal backup.");
        }

        var span = data.AsSpan();
        var nonce = span.Slice(Magic.Length, NonceLength);
        var tag = span.Slice(Magic.Length + NonceLength, TagLength);
        var cipher = span[(Magic.Length + NonceLength + TagLength)..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(DeriveKey(secret), TagLength);
        aes.Decrypt(nonce, cipher, tag, plain, Magic);
        return plain;
    }

    private static byte[] DeriveKey(byte[] secret) => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, info: KeyInfo);
}
