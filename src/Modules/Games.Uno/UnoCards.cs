namespace Casino.Modules.Games.Uno;

public enum UnoKind
{
    Number = 0,
    Skip = 1,
    Reverse = 2,
    DrawTwo = 3,
    Wild = 4,
    WildDrawFour = 5,
}

/// <summary>
/// Las 108 cartas del Uno, codificadas como un entero de 0 a 107. <c>0..99</c> son de color (<c>color = id / 25</c>: 0 rojo, 1 amarillo, 2 verde, 3 azul; dentro de cada
/// color <c>id % 25</c> es 0 = el cero, 1..9 = 1 a 9, 10..18 = 1 a 9 otra vez, 19..20 = salto, 21..22 = reversa, 23..24 = +2); <c>100..103</c> son comodines y
/// <c>104..107</c> comodines +4.
/// </summary>
public static class UnoCards
{
    public const int DeckSize = 108;
    public const int Colors = 4;

    public static bool IsValid(int card) => card is >= 0 and < DeckSize;

    public static UnoKind KindOf(int card)
    {
        if (card >= 104)
        {
            return UnoKind.WildDrawFour;
        }

        if (card >= 100)
        {
            return UnoKind.Wild;
        }

        return (card % 25) switch
        {
            <= 18 => UnoKind.Number,
            <= 20 => UnoKind.Skip,
            <= 22 => UnoKind.Reverse,
            _ => UnoKind.DrawTwo,
        };
    }

    public static bool IsWild(int card) => card >= 100;

    /// <summary>El color de la carta (0 a 3), o -1 si es un comodin.</summary>
    public static int ColorOf(int card) => card >= 100 ? -1 : card / 25;

    /// <summary>El numero (0 a 9) de una carta numerica, o -1 si es de accion o comodin.</summary>
    public static int NumberOf(int card)
    {
        if (KindOf(card) != UnoKind.Number)
        {
            return -1;
        }

        var k = card % 25;
        return k == 0 ? 0 : ((k - 1) % 9) + 1;
    }

    /// <summary>Los puntos de una carta a mano (no cuentan para el pago: gana quien se queda sin cartas; sirven para que un bot elija).</summary>
    public static int WeightOf(int card) => KindOf(card) switch
    {
        UnoKind.Number => NumberOf(card),
        UnoKind.Wild or UnoKind.WildDrawFour => 50,
        _ => 20,
    };

    /// <summary>El mazo completo, sin barajar.</summary>
    public static List<int> NewDeck() => [.. Enumerable.Range(0, DeckSize)];
}
