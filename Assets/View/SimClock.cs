using System;

namespace Battalion.View
{
    /// <summary>
    /// 時間の進め方（仕様「Sim と表示の境界」）。ゲーム内秒＝現実の秒×4×倍速。たまった分だけ刻みを進め、1フレーム最大16刻み。
    /// それ以上たまった分は捨てる（重いフレームで遅れを取り戻そうとして、さらに重くならないように）。一時停止中はためない。
    /// UnityEngine に依存しない（テストしやすいように）。
    /// </summary>
    public sealed class SimClock
    {
        public const double GameSecondsPerRealSecond = 4.0;
        public const int MaxTicksPerFrame = 16;
        public static readonly int[] Speeds = { 1, 2, 4, 8, 16 };

        readonly double _tickSec;
        double _accum;

        public bool Paused { get; set; }
        public int Speed { get; private set; } = 1;

        public SimClock(double tickSec)
        {
            _tickSec = tickSec;
        }

        public void SetSpeed(int speed)
        {
            if (Array.IndexOf(Speeds, speed) < 0) throw new ArgumentOutOfRangeException(nameof(speed));
            Speed = speed;
        }

        /// <summary>一段速く（×16 が上限）。</summary>
        public void SpeedUp()
        {
            int k = Array.IndexOf(Speeds, Speed);
            if (k < Speeds.Length - 1) Speed = Speeds[k + 1];
        }

        /// <summary>一段遅く（×1 が下限）。</summary>
        public void SpeedDown()
        {
            int k = Array.IndexOf(Speeds, Speed);
            if (k > 0) Speed = Speeds[k - 1];
        }

        /// <summary>現実の経過秒を受け取り、このフレームに進める刻みの数を返す。</summary>
        public int Advance(double realDeltaSec)
        {
            if (Paused || realDeltaSec <= 0) return 0;
            _accum += realDeltaSec * GameSecondsPerRealSecond * Speed;
            int ticks = (int)Math.Floor(_accum / _tickSec);
            if (ticks > MaxTicksPerFrame)
            {
                ticks = MaxTicksPerFrame;
                _accum = 0; // 上限を超えた分は捨てる
            }
            else
            {
                _accum -= ticks * _tickSec;
            }
            return ticks;
        }

        /// <summary>
        /// 直前の刻みから次の刻みまでの割合（0〜1）。補間に使う。一時停止中はたまった分が増えないので、描く位置もそこで止まる。
        /// </summary>
        public float Alpha => (float)Math.Min(1.0, Math.Max(0.0, _accum / _tickSec));

        public void Reset()
        {
            _accum = 0;
        }
    }
}
