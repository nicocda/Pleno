using System.Text.RegularExpressions;

namespace Casino.Modules.Games.Platform;

/// <summary>
/// Elige que juegos carga un host: los que estan habilitados por configuracion (<c>Games:Enabled</c>), o todos si no se configura.
/// Falla al arrancar ante un id desconocido o repetido, para que un error de configuracion no deje un juego "a medias".
/// </summary>
public static partial class GameSelection
{
    public static IReadOnlyList<IGameModule> Select(IEnumerable<IGameModule> available, IEnumerable<string>? enabled)
    {
        ArgumentNullException.ThrowIfNull(available);

        var all = available.ToList();
        foreach (var game in all)
        {
            if (!ValidId().IsMatch(game.Info.Id))
            {
                throw new InvalidOperationException($"El id de juego '{game.Info.Id}' no es valido: minusculas, numeros y guiones.");
            }
        }

        var duplicated = all.GroupBy(g => g.Info.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new InvalidOperationException($"El id de juego '{duplicated.Key}' esta repetido.");
        }

        var wanted = enabled?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList() ?? [];
        if (wanted.Count == 0)
        {
            return all;
        }

        var unknown = wanted.Where(id => all.All(g => !string.Equals(g.Info.Id, id, StringComparison.Ordinal))).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"Games:Enabled nombra juegos que no existen: {string.Join(", ", unknown)}. Disponibles: {string.Join(", ", all.Select(g => g.Info.Id))}.");
        }

        return [.. all.Where(g => wanted.Contains(g.Info.Id))];
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidId();
}
