using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Casino.Modules.Games.Tables;

/// <summary>
/// Barajado provably fair para los juegos de mesas: Fisher-Yates con numeros uniformes de HMAC-SHA256(semilla, "{etiqueta}:{contador}"), 8 palabras de 32 bits por
/// bloque (big-endian) y rechazo para no sesgar. Es el mismo esquema que el zapato de Blackjack, generalizado: cada juego usa su propia etiqueta
/// (por ejemplo <c>"uno:{tableId}"</c>), asi nadie puede reutilizar una baraja de otro juego. Cualquiera puede recalcularlo con la semilla revelada.
/// </summary>
public static class FairShuffle
{
    /// <summary>Baraja <paramref name="items"/> en el lugar.</summary>
    public static void Shuffle<T>(IList<T> items, string serverSeed, string label)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverSeed);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var stream = new Stream(serverSeed, label);
        for (var i = items.Count - 1; i >= 1; i--)
        {
            var j = (int)stream.Below((uint)(i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>Un numero uniforme en [0, bound) sacado del flujo de la partida (para sorteos que no son barajas, como quien es mano).</summary>
    public static int Pick(string serverSeed, string label, int bound)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverSeed);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfLessThan(bound, 1);

        return (int)new Stream(serverSeed, label).Below((uint)bound);
    }

    private sealed class Stream(string serverSeed, string label)
    {
        private readonly byte[] _key = Encoding.UTF8.GetBytes(serverSeed);
        private byte[] _block = [];
        private int _word = 8;
        private long _counter;

        public uint Below(uint bound)
        {
            const ulong range = 1UL << 32;
            var limit = range - (range % bound); // el mayor multiplo de bound que entra en 32 bits: lo que lo pase se descarta
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
                _block = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{label}:{_counter++}"));
                _word = 0;
            }

            return BinaryPrimitives.ReadUInt32BigEndian(_block.AsSpan(_word++ * 4, 4));
        }
    }
}
