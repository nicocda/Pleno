using Casino.Modules.Identity.Application;

namespace Casino.Identity.Tests;

public sealed class AliasRulesTests
{
    [Theory]
    [InlineData("jugador1")]
    [InlineData("a.b-c_d")]
    [InlineData("Nico")]
    public void Acepta_alias_validos(string alias) => Assert.True(AliasRules.IsValid(alias));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("un alias con espacios")]
    [InlineData("persona@example.com")]
    [InlineData("_empieza_con_guion")]
    [InlineData("demasiado_largo_para_un_alias")]
    public void Rechaza_alias_invalidos(string? alias) => Assert.False(AliasRules.IsValid(alias));
}
