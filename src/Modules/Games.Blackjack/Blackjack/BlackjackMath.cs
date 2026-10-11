using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Casino.Modules.Games.Blackjack;

/// <summary>El resultado de una mano de un jugador contra el crupier.</summary>
public enum HandResult
{
    /// <summary>Blackjack natural (21 con dos cartas) y el crupier no lo tiene: paga 3 a 2.</summary>
    Blackjack = 1,

    Win = 2,

    /// <summary>Empate: se devuelve lo apostado.</summary>
    Push = 3,

    Lose = 4,

    /// <summary>El jugador se paso de 21: pierde sin importar lo que haga el crupier.</summary>
    Bust = 5,
}

/// <summary>El valor de una mano: el total y si es "blanda" (cuenta un as como 11).</summary>
public readonly record struct HandValue(int Total, bool IsSoft);

/// <summary>
/// Las reglas y el mazo de Blackjack, sin dependencias: todo con enteros y funciones puras. El mazo es un zapato de 6 barajas barajado con
/// Fisher-Yates a partir de la semilla de la mano (HMAC-SHA256), asi que cualquiera puede recalcularlo (docs/juego-blackjack.md).
/// Una carta es un entero de 0 a 51: <c>rango = carta % 13</c> (0 = as, 1 a 8 = 2 a 9, 9 a 12 = 10, J, Q, K) y <c>palo = carta / 13</c>.
/// </summary>
public static class BlackjackMath
{
    public const int Decks = 6;
    public const int ShoeSize = Decks * 52;
    public const int Twenty1 = 21;
    public const int DealerStandsOn = 17;

    public static int Rank(int card) => card % 13;

    public static int Suit(int card) => card / 13;

    /// <summary>Puntos de una carta con el as contado como 11 (el ajuste a 1 lo hace <see cref="Evaluate"/>).</summary>
    public static int PointsOf(int card)
    {
        var rank = Rank(card);
        return rank == 0 ? 11 : rank >= 9 ? 10 : rank + 1;
    }

    public static HandValue Evaluate(IEnumerable<int> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var total = 0;
        var aces = 0;
        foreach (var card in cards)
        {
            total += PointsOf(card);
            if (Rank(card) == 0)
            {
                aces++;
            }
        }

        while (total > Twenty1 && aces > 0)
        {
            total -= 10;
            aces--;
        }

        return new HandValue(total, aces > 0);
    }

    public static bool IsBlackjack(IReadOnlyCollection<int> cards) => cards.Count == 2 && Evaluate(cards).Total == Twenty1;

    public static bool IsBust(IEnumerable<int> cards) => Evaluate(cards).Total > Twenty1;

    /// <summary>El crupier pide carta mientras tenga menos de 17 (planta en todos los 17, tambien en un 17 blando).</summary>
    public static bool DealerMustDraw(IEnumerable<int> dealerCards) => Evaluate(dealerCards).Total < DealerStandsOn;

    public static HandResult Resolve(IReadOnlyCollection<int> player, IReadOnlyCollection<int> dealer)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(dealer);

        if (IsBust(player))
        {
            return HandResult.Bust;
        }

        var playerBlackjack = IsBlackjack(player);
        var dealerBlackjack = IsBlackjack(dealer);
        if (playerBlackjack && dealerBlackjack)
        {
            return HandResult.Push;
        }

        if (playerBlackjack)
        {
            return HandResult.Blackjack;
        }

        if (dealerBlackjack)
        {
            return HandResult.Lose;
        }

        var playerTotal = Evaluate(player).Total;
        var dealerTotal = Evaluate(dealer).Total;
        if (dealerTotal > Twenty1 || playerTotal > dealerTotal)
        {
            return HandResult.Win;
        }

        return playerTotal == dealerTotal ? HandResult.Push : HandResult.Lose;
    }

    /// <summary>Lo que se cobra en total (incluye lo apostado): blackjack 3 a 2, ganar 1 a 1, empate devuelve la apuesta. Siempre un entero.</summary>
    public static long PayoutFor(long stake, HandResult result) => result switch
    {
        HandResult.Blackjack => stake + (stake * 3 / 2),
        HandResult.Win => stake * 2,
        HandResult.Push => stake,
        _ => 0,
    };

    /// <summary>El zapato barajado de una mano: 312 cartas (0 a 51 repetidas 6 veces), en el orden en que se reparten.</summary>
    public static int[] Shoe(string serverSeed, Guid roundId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverSeed);

        var order = new int[ShoeSize];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        var stream = new HmacStream(serverSeed, roundId);
        for (var i = order.Length - 1; i >= 1; i--)
        {
            var j = (int)stream.Below((uint)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }

        for (var i = 0; i < order.Length; i++)
        {
            order[i] %= 52;
        }

        return order;
    }

    /// <summary>
    /// Posicion de cada carta del reparto inicial en el zapato, para <paramref name="seats"/> jugadores: primero una carta a cada uno, luego la del crupier
    /// (visible), luego la segunda de cada uno y por ultimo la del crupier boca abajo. Lo que sigue (<see cref="FirstFreeCard"/>) es para pedir.
    /// </summary>
    public static int PlayerFirstCard(int seat) => seat;

    public static int DealerUpCard(int seats) => seats;

    public static int PlayerSecondCard(int seats, int seat) => seats + 1 + seat;

    public static int DealerHoleCard(int seats) => (2 * seats) + 1;

    public static int FirstFreeCard(int seats) => (2 * seats) + 2;

    /// <summary>Numeros enteros uniformes sacados de HMAC-SHA256(semilla, "blackjack:{roundId}:{contador}"), 8 palabras de 32 bits por bloque, con rechazo para no sesgar.</summary>
    private sealed class HmacStream(string serverSeed, Guid roundId)
    {
        private readonly byte[] _key = Encoding.UTF8.GetBytes(serverSeed);
        private byte[] _block = [];
        private int _word = 8;
        private long _counter;

        public uint Below(uint bound)
        {
            const ulong range = 1UL << 32;
            var limit = range - (range % bound); // el mayor multiplo de bound que entra en 32 bits: lo que lo pase se descarta (rechazo)
            while (true)
            {
                var x = Next();
                if (x < limit)
                {
                    return x % bound;
                }
            }
        }

        private uint Next()
        {
            if (_word == 8)
            {
                _block = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"blackjack:{roundId:N}:{_counter++}"));
                _word = 0;
            }

            return BinaryPrimitives.ReadUInt32BigEndian(_block.AsSpan(_word++ * 4, 4));
        }
    }
}
