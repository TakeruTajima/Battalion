using System;

namespace Battalion.Sim
{
    /// <summary>
    /// PCG32（PCG-XSH-RR 64/32）。pcg_basic.c の pcg32_srandom_r / pcg32_random_r と同じ手順。
    /// </summary>
    public sealed class Pcg32
    {
        const ulong Multiplier = 6364136223846793005UL;

        /// <summary>シミュレーションで使う既定の系列番号。</summary>
        public const ulong DefaultStream = 54UL;

        ulong _state;
        readonly ulong _inc;

        public Pcg32(ulong seed, ulong stream = DefaultStream)
        {
            _state = 0UL;
            _inc = (stream << 1) | 1UL;
            NextUInt();
            _state += seed;
            NextUInt();
        }

        public uint NextUInt()
        {
            ulong old = _state;
            _state = unchecked(old * Multiplier + _inc);
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>[0, 1) の一様乱数（53ビット精度、32ビット出力を2回使う）。</summary>
        public double NextDouble()
        {
            ulong a = NextUInt() >> 5;  // 27 bits
            ulong b = NextUInt() >> 6;  // 26 bits
            return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
        }

        /// <summary>
        /// ポアソン乱数（Knuth 法）。1刻みの λ は 0.1 未満を想定しているので、ループは平均 1 回強で終わる。
        /// λ ≤ 0 のときは乱数を消費せずに 0 を返す。
        /// </summary>
        public int NextPoisson(double lambda)
        {
            if (lambda <= 0) return 0;
            double l = Math.Exp(-lambda);
            int k = 0;
            double p = 1.0;
            do
            {
                k++;
                p *= NextDouble();
            } while (p > l);
            return k - 1;
        }
    }
}
