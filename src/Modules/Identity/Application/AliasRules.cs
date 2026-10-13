using System.Text.RegularExpressions;

namespace Casino.Modules.Identity.Application;

public static partial class AliasRules
{
    public const int MinLength = 3;
    public const int MaxLength = 20;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex AllowedChars();

    /// <summary>El alias es publico (lo ve el cajero y los otros jugadores): sin espacios ni caracteres raros, y nunca con forma de email.</summary>
    public static bool IsValid(string? alias) =>
        alias is { Length: >= MinLength and <= MaxLength } && AllowedChars().IsMatch(alias);
}
