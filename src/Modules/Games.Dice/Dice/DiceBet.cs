using Casino.Modules.Games.Fairness;

namespace Casino.Modules.Games.Dice;

public enum DiceDirection
{
    /// <summary>Gana si el dado sale por debajo del limite.</summary>
    Under,

    /// <summary>Gana si el dado sale por encima del limite.</summary>
    Over,
}

/// <summary>
/// Dados (over/under): el servidor sortea un numero de 0,00 a 99,99 (en centesimas: 0 a 9999) y el jugador elige su probabilidad de ganar
/// (<see cref="Chance"/>, en centesimas de punto porcentual: 4950 = 49,50 %) y si apuesta a que sale por debajo o por encima.
/// Paga apuesta x 9800 / probabilidad (ventaja de la casa: 2 %), redondeando hacia abajo: siempre es un entero.
/// </summary>
public sealed class DiceBet
{
    public const int Rolls = 10_000;
    public const int MinChance = 200;
    public const int MaxChance = 9_500;
    public const long MinStake = 1;
    public const long MaxStake = 10_000;
    private const long PayoutNumerator = 9_800;

    private DiceBet(DiceDirection direction, int chance, long stake)
    {
        Direction = direction;
        Chance = chance;
        Stake = stake;
    }

    public DiceDirection Direction { get; }

    public int Chance { get; }

    public long Stake { get; }

    public static bool TryCreate(DiceDirection direction, int chance, long stake, out DiceBet? bet)
    {
        bet = null;
        if (!Enum.IsDefined(direction) || chance is < MinChance or > MaxChance || stake is < MinStake or > MaxStake)
        {
            return false;
        }

        bet = new DiceBet(direction, chance, stake);
        return true;
    }

    public static DiceBet Create(DiceDirection direction, int chance, long stake) =>
        TryCreate(direction, chance, stake, out var bet) ? bet! : throw new ArgumentException("Apuesta de dados invalida.");

    public bool Wins(int roll) => Direction == DiceDirection.Under ? roll < Chance : roll >= Rolls - Chance;

    /// <summary>Lo que se cobra (apuesta incluida) si gana; 0 si pierde.</summary>
    public long PayoutFor(int roll) => Wins(roll) ? Stake * PayoutNumerator / Chance : 0;
}

public sealed record DiceOutcome(int Roll, long Payout);

public static class DiceGame
{
    /// <summary>Resuelve la tirada de forma determinista a partir de las seeds y el nonce. No guarda ni publica nada.</summary>
    public static DiceOutcome Play(DiceBet bet, string serverSeed, string clientSeed, long nonce)
    {
        ArgumentNullException.ThrowIfNull(bet);

        var roll = new FairRng(serverSeed, clientSeed, nonce).NextInt(DiceBet.Rolls);
        return new DiceOutcome(roll, bet.PayoutFor(roll));
    }
}
