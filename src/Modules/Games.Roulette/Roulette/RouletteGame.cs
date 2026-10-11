using Casino.Modules.Games.Fairness;

namespace Casino.Modules.Games.Roulette;

public sealed record RouletteOutcome(int WinningNumber, long Payout);

public static class RouletteGame
{
    /// <summary>Resuelve una apuesta de forma determinista a partir de las seeds y el nonce. No guarda ni publica nada.</summary>
    public static RouletteOutcome Play(RouletteBet bet, string serverSeed, string clientSeed, long nonce)
    {
        ArgumentNullException.ThrowIfNull(bet);

        var winningNumber = new FairRng(serverSeed, clientSeed, nonce).NextInt(RouletteBet.PocketCount);
        return new RouletteOutcome(winningNumber, bet.PayoutFor(winningNumber));
    }

    /// <summary>
    /// Resuelve una tirada con varias apuestas: se sortea UN numero (un solo nonce) y el pago es la suma de lo que paga cada apuesta.
    /// Apostar a la vez a rojo y negro, o a un pleno y a su columna, es legal: cada apuesta se resuelve por separado.
    /// </summary>
    public static RouletteOutcome PlayMany(IReadOnlyList<RouletteBet> bets, string serverSeed, string clientSeed, long nonce)
    {
        ArgumentNullException.ThrowIfNull(bets);
        if (bets.Count == 0)
        {
            throw new ArgumentException("Una tirada necesita al menos una apuesta.", nameof(bets));
        }

        var winningNumber = new FairRng(serverSeed, clientSeed, nonce).NextInt(RouletteBet.PocketCount);
        long payout = 0;
        foreach (var bet in bets)
        {
            payout = checked(payout + bet.PayoutFor(winningNumber));
        }

        return new RouletteOutcome(winningNumber, payout);
    }
}
