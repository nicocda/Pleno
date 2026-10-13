using System.Security.Cryptography;
using Casino.Modules.Identity.Application;
using Microsoft.Extensions.Options;

namespace Casino.Identity.Tests;

public sealed class EmailProtectorTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static EmailProtector Create() =>
        new(Options.Create(new EmailProtectionOptions { CurrentKeyVersion = 1, EncryptionKeys = { [1] = NewKey() }, HmacKey = NewKey() }));

    [Fact]
    public void Cifra_y_descifra_el_mismo_email()
    {
        var protector = Create();

        var cipher = protector.Encrypt("Jugador@Example.com ", out var version);

        Assert.Equal("jugador@example.com", protector.Decrypt(cipher, version));
    }

    [Fact]
    public void El_texto_cifrado_no_contiene_el_email_ni_se_repite()
    {
        var protector = Create();

        var first = protector.Encrypt("jugador@example.com", out _);
        var second = protector.Encrypt("jugador@example.com", out _);

        Assert.DoesNotContain("jugador", first, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first, second); // nonce aleatorio: dos cifrados del mismo valor son distintos
    }

    [Fact]
    public void El_hash_es_estable_y_no_distingue_mayusculas_ni_espacios()
    {
        var protector = Create();

        Assert.Equal(protector.Hash("jugador@example.com"), protector.Hash("  JUGADOR@Example.COM "));
        Assert.NotEqual(protector.Hash("a@example.com"), protector.Hash("b@example.com"));
    }

    [Fact]
    public void El_hash_depende_de_la_clave()
    {
        Assert.NotEqual(Create().Hash("jugador@example.com"), Create().Hash("jugador@example.com"));
    }

    [Fact]
    public void Un_texto_alterado_no_se_descifra()
    {
        var protector = Create();
        var cipher = Convert.FromBase64String(protector.Encrypt("jugador@example.com", out var version));
        cipher[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Decrypt(Convert.ToBase64String(cipher), version));
    }

    [Fact]
    public void Se_puede_rotar_la_clave_y_leer_lo_cifrado_con_la_anterior()
    {
        var oldKey = NewKey();
        var hmac = NewKey();
        var before = new EmailProtector(Options.Create(new EmailProtectionOptions { CurrentKeyVersion = 1, EncryptionKeys = { [1] = oldKey }, HmacKey = hmac }));
        var cipher = before.Encrypt("jugador@example.com", out var version);

        var after = new EmailProtector(Options.Create(new EmailProtectionOptions { CurrentKeyVersion = 2, EncryptionKeys = { [1] = oldKey, [2] = NewKey() }, HmacKey = hmac }));

        Assert.Equal(1, version);
        Assert.Equal("jugador@example.com", after.Decrypt(cipher, version));
        after.Encrypt("jugador@example.com", out var newVersion);
        Assert.Equal(2, newVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-base64!")]
    [InlineData("AAAA")]
    public void Una_clave_ausente_o_invalida_impide_arrancar(string key)
    {
        var options = new EmailProtectionOptions { CurrentKeyVersion = 1, EncryptionKeys = { [1] = key }, HmacKey = NewKey() };

        Assert.Throws<InvalidOperationException>(() => new EmailProtector(Options.Create(options)));
    }

    [Fact]
    public void Si_falta_la_clave_de_la_version_vigente_no_arranca()
    {
        var options = new EmailProtectionOptions { CurrentKeyVersion = 2, EncryptionKeys = { [1] = NewKey() }, HmacKey = NewKey() };

        Assert.Throws<InvalidOperationException>(() => new EmailProtector(Options.Create(options)));
    }
}
