using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Casino.Modules.Identity.Application;

/// <summary>
/// Claves para proteger el email (PII). Son secretos: user-secrets en local, Key Vault o <c>.env</c> con permisos 600 en el servidor. Nunca se versionan.
/// <c>Identity:Email:EncryptionKeys:&lt;version&gt;</c> (32 bytes en base64, una por version) y <c>Identity:Email:HmacKey</c> (32 bytes en base64, distinta a las anteriores).
/// </summary>
public sealed class EmailProtectionOptions
{
    public const string SectionName = "Identity:Email";

    public int CurrentKeyVersion { get; set; } = 1;

    public Dictionary<int, string> EncryptionKeys { get; set; } = [];

    public string HmacKey { get; set; } = string.Empty;
}

/// <summary>Cifra el email (AES-256-GCM) y calcula su hash con clave (HMAC-SHA256) para buscarlo sin descifrar.</summary>
public sealed class EmailProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly byte[] AssociatedData = "casino:identity:email"u8.ToArray();

    private readonly Dictionary<int, byte[]> _encryptionKeys;
    private readonly byte[] _hmacKey;

    public EmailProtector(IOptions<EmailProtectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        _encryptionKeys = value.EncryptionKeys.ToDictionary(pair => pair.Key, pair => ParseKey(pair.Value, $"{EmailProtectionOptions.SectionName}:EncryptionKeys:{pair.Key}"));
        _hmacKey = ParseKey(value.HmacKey, $"{EmailProtectionOptions.SectionName}:HmacKey");
        CurrentKeyVersion = value.CurrentKeyVersion;
        if (!_encryptionKeys.ContainsKey(CurrentKeyVersion))
        {
            throw new InvalidOperationException($"Falta la clave de cifrado del email para la version {CurrentKeyVersion} ({EmailProtectionOptions.SectionName}:EncryptionKeys:{CurrentKeyVersion}).");
        }
    }

    public int CurrentKeyVersion { get; }

    /// <summary>Forma canonica para comparar: sin espacios y en minusculas. Es lo que se hashea; el cifrado guarda lo que escribio el usuario ya normalizado.</summary>
    public static string Normalize(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    public string Hash(string email)
    {
        var data = Encoding.UTF8.GetBytes(Normalize(email));
        return Convert.ToBase64String(HMACSHA256.HashData(_hmacKey, data));
    }

    public string Encrypt(string email, out int keyVersion)
    {
        var plain = Encoding.UTF8.GetBytes(Normalize(email));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_encryptionKeys[CurrentKeyVersion], TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);
        }

        keyVersion = CurrentKeyVersion;
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Decrypt(string protectedEmail, int keyVersion)
    {
        ArgumentNullException.ThrowIfNull(protectedEmail);
        if (!_encryptionKeys.TryGetValue(keyVersion, out var key))
        {
            throw new CryptographicException($"No hay clave de cifrado del email para la version {keyVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        var raw = Convert.FromBase64String(protectedEmail);
        if (raw.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Email cifrado invalido.");
        }

        var nonce = raw.AsSpan(0, NonceSize);
        var tag = raw.AsSpan(NonceSize, TagSize);
        var cipher = raw.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] ParseKey(string? base64, string name)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidOperationException($"Falta el secreto '{name}' (32 bytes en base64). Ver docs/autenticacion.md.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"El secreto '{name}' no es base64 valido.");
        }

        return key.Length == KeySize ? key : throw new InvalidOperationException($"El secreto '{name}' debe tener {KeySize} bytes.");
    }
}
