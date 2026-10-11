import type { ReactElement } from "react";

/**
 * Las caras de la baraja ESPAÑOLA (la del Truco): espadas, bastos, oros y copas dibujados como tales (no los palos de la baraja francesa), con los numeros
 * acomodados como en el mazo real (1 a 7 con sus figuras repetidas) y la sota (10), el caballo (11) y el rey (12) como personajes. Todo es SVG propio: sin imagenes
 * ni dependencias. Cada carta ocupa una caja de 100 x 150 que escala con el tamaño del contenedor.
 */

export type SpanishSuit = 0 | 1 | 2 | 3; // 0 espadas, 1 bastos, 2 oros, 3 copas

/** Color de cada palo (para el numero y el marco de la carta). */
const SUIT_INK = ["#1f4fa8", "#1f7a46", "#9a6b00", "#b32727"] as const;
const FIGURE_NAMES: Record<number, string> = { 10: "Sota", 11: "Caballo", 12: "Rey" };

/** Los emblemas de cada palo, dibujados en una caja de 40 x 40. */
function Emblem({ suit }: { suit: SpanishSuit }): ReactElement {
  switch (suit) {
    case 0: // espadas: una espada con empuñadura dorada
      return (
        <g>
          <path d="M20 1.5 L26.5 22 L20 25.5 L13.5 22 Z" fill="#8aa6d1" stroke="#26396b" strokeWidth="1.6" strokeLinejoin="round" />
          <path d="M20 5 V22" stroke="#26396b" strokeWidth="1" opacity="0.55" />
          <rect x="8" y="24.5" width="24" height="4.2" rx="2.1" fill="#e2b830" stroke="#8a5a00" strokeWidth="1.3" />
          <rect x="17.6" y="28.5" width="4.8" height="7" rx="1.5" fill="#7a4a1d" stroke="#3f2610" strokeWidth="1.1" />
          <circle cx="20" cy="37.2" r="2.6" fill="#e2b830" stroke="#8a5a00" strokeWidth="1.2" />
        </g>
      );
    case 1: // bastos: un garrote nudoso con hojas
      return (
        <g>
          <path d="M11 36 Q19 22 28 7" stroke="#3f2610" strokeWidth="10" strokeLinecap="round" fill="none" />
          <path d="M11 36 Q19 22 28 7" stroke="#9a6a35" strokeWidth="7" strokeLinecap="round" fill="none" />
          <circle cx="17.5" cy="26" r="2.2" fill="#6b4421" />
          <circle cx="23" cy="16" r="2" fill="#6b4421" />
          <path d="M28 7 q7 -3 9 2 q-5 5 -9 -2 Z" fill="#2e8b4e" stroke="#1d5a32" strokeWidth="1" />
          <path d="M26 10 q-8 -4 -10 1 q5 5 10 -1 Z" fill="#3aa05d" stroke="#1d5a32" strokeWidth="1" />
        </g>
      );
    case 2: // oros: una moneda de oro
      return (
        <g>
          <circle cx="20" cy="20" r="18" fill="#f3c63a" stroke="#8a5a00" strokeWidth="2" />
          <circle cx="20" cy="20" r="12.5" fill="none" stroke="#b7861a" strokeWidth="1.8" />
          <circle cx="20" cy="20" r="4.5" fill="#b7861a" />
          <path d="M20 4.5 V9 M20 31 V35.5 M4.5 20 H9 M31 20 H35.5" stroke="#b7861a" strokeWidth="1.6" strokeLinecap="round" />
        </g>
      );
    default: // copas: un caliz rojo
      return (
        <g>
          <path d="M7 5 H33 C33 19 27 26 22 27.2 V33 H28.5 V37.5 H11.5 V33 H18 V27.2 C13 26 7 19 7 5 Z" fill="#cf3f3f" stroke="#6e1414" strokeWidth="1.8" strokeLinejoin="round" />
          <path d="M7 5 H33" stroke="#6e1414" strokeWidth="2.4" />
          <path d="M12 10 C12 16 15 20 18 22" stroke="#f0a0a0" strokeWidth="1.8" strokeLinecap="round" fill="none" opacity="0.8" />
        </g>
      );
  }
}

/** Donde va cada figura de los numeros 2 a 7 (centros, en la caja de 100 x 150). */
const PIPS: Record<number, ReadonlyArray<readonly [number, number]>> = {
  2: [[50, 46], [50, 112]],
  3: [[50, 40], [50, 80], [50, 120]],
  4: [[32, 52], [68, 52], [32, 112], [68, 112]],
  5: [[32, 46], [68, 46], [50, 82], [32, 118], [68, 118]],
  6: [[32, 42], [68, 42], [32, 80], [68, 80], [32, 118], [68, 118]],
  7: [[32, 42], [68, 42], [50, 61], [32, 80], [68, 80], [32, 118], [68, 118]],
};
/** Tamaño de cada figura segun cuantas hay en la carta (con menos, mas grandes). */
const PIP_SIZE: Record<number, number> = { 2: 40, 3: 36, 4: 36, 5: 31, 6: 31, 7: 28 };

function place(suit: SpanishSuit, cx: number, cy: number, size: number, key: string | number): ReactElement {
  const scale = size / 40;
  return (
    <g key={key} data-pip="" transform={`translate(${cx - size / 2} ${cy - size / 2}) scale(${scale})`}>
      <Emblem suit={suit} />
    </g>
  );
}

/** La sota, el caballo y el rey: un personaje sencillo dentro de un marco, con el emblema del palo arriba y abajo. */
function Figure({ suit, number }: { suit: SpanishSuit; number: number }): ReactElement {
  const ink = SUIT_INK[suit];
  return (
    <g data-figure={number}>
      <rect x="17" y="32" width="66" height="86" rx="6" fill={ink} fillOpacity="0.1" stroke={ink} strokeWidth="1.6" />
      {number === 10 && (
        <g>
          <path d="M40 110 L44 76 H56 L60 110 Z" fill={ink} stroke="#222" strokeWidth="1" strokeLinejoin="round" />
          <circle cx="50" cy="66" r="9" fill="#f2cfa4" stroke="#222" strokeWidth="1" />
          <path d="M40 62 Q50 46 60 62 Z" fill="#c9a227" stroke="#222" strokeWidth="1" strokeLinejoin="round" />
          <path d="M58 56 Q66 44 70 50" stroke="#c0392b" strokeWidth="2" fill="none" strokeLinecap="round" />
        </g>
      )}
      {number === 11 && (
        <g>
          <path d="M36 112 L39 80 Q40 60 54 56 L60 46 L64 56 L70 62 Q68 71 60 72 L60 82 L66 112 Z" fill={ink} stroke="#222" strokeWidth="1" strokeLinejoin="round" />
          <circle cx="58" cy="62" r="1.6" fill="#fff" />
          <path d="M44 62 Q40 72 40 84" stroke="#f2cfa4" strokeWidth="3" fill="none" strokeLinecap="round" opacity="0.7" />
        </g>
      )}
      {number === 12 && (
        <g>
          <path d="M34 112 L40 78 H60 L66 112 Z" fill={ink} stroke="#222" strokeWidth="1" strokeLinejoin="round" />
          <circle cx="50" cy="68" r="10" fill="#f2cfa4" stroke="#222" strokeWidth="1" />
          <path d="M42 76 Q50 94 58 76" fill="#d9d2c0" stroke="#222" strokeWidth="1" />
          <path d="M37 58 L40 42 L46 52 L50 40 L54 52 L60 42 L63 58 Z" fill="#e2b830" stroke="#8a5a00" strokeWidth="1.2" strokeLinejoin="round" />
        </g>
      )}
      {place(suit, 31, 44, 15, "a")}
      {place(suit, 69, 106, 15, "b")}
      <text x="50" y="128" textAnchor="middle" fontSize="8.5" fontWeight="800" fill={ink} fontFamily="Georgia, 'Times New Roman', serif" letterSpacing="0.06em">
        {FIGURE_NAMES[number]!.toUpperCase()}
      </text>
    </g>
  );
}

/** La cara de una carta de la baraja española (ver arriba). Solo decorativa: el nombre accesible lo pone quien la usa (`aria-label`). */
export function SpanishCardFace({ suit, number }: { suit: SpanishSuit; number: number }): ReactElement {
  const ink = SUIT_INK[suit];
  const corner = { fontFamily: "Georgia, 'Times New Roman', serif", fontWeight: 800, fill: ink } as const;
  return (
    <svg className="tru-card__svg" viewBox="0 0 100 150" aria-hidden="true" focusable="false" data-suit={suit} data-number={number}>
      <rect x="4" y="4" width="92" height="142" rx="7" fill="none" stroke={ink} strokeOpacity="0.4" strokeWidth="1.5" />

      <text x="12" y="25" fontSize="21" {...corner}>{number}</text>

      {number >= 10 ? (
        <Figure suit={suit} number={number} />
      ) : number === 1 ? (
        place(suit, 50, 78, 64, "ace")
      ) : (
        PIPS[number]!.map(([x, y], i) => place(suit, x, y, PIP_SIZE[number]!, i))
      )}
    </svg>
  );
}
