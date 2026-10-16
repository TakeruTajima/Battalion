using System;
using Battalion.Sim;

namespace Battalion.View
{
    /// <summary>
    /// 駒（中隊の矩形）の形。正面幅（200m）は向きに直交する長辺、奥行（50m）は向きに沿う短辺。UnityEngine に依存しない。
    /// </summary>
    public static class UnitGeometry
    {
        /// <summary>向きの単位ベクトル（前）と、左（前から反時計回りに90°）。</summary>
        public static void Axes(double facingRad, out Vec2 front, out Vec2 left)
        {
            front = Vec2.FromAngle(facingRad);
            left = new Vec2(-front.Y, front.X);
        }

        /// <summary>点を駒の座標に直す：along は前方向、across は左方向の距離（m）。</summary>
        public static void ToLocal(Vec2 center, double facingRad, Vec2 p, out double along, out double across)
        {
            Axes(facingRad, out var front, out var left);
            var d = p - center;
            along = d.Dot(front);
            across = d.Dot(left);
        }

        /// <summary>点が駒の矩形の中か（<paramref name="marginM"/> だけ広げて判定する）。</summary>
        public static bool Contains(Vec2 center, double facingRad, double frontageM, double depthM, Vec2 p, double marginM = 0)
        {
            ToLocal(center, facingRad, p, out double along, out double across);
            return Math.Abs(along) <= depthM / 2 + marginM && Math.Abs(across) <= frontageM / 2 + marginM;
        }

        /// <summary>
        /// 攻撃で「側面」を選んで敵の駒をクリックしたときの側。長辺を3等分し、左の端なら +1（敵の左の側面）、
        /// 右の端なら −1（右の側面）、中央なら 0（Sim が経路コストの小さい側を選ぶ）。
        /// 側の符号は <see cref="Simulation.JumpOff"/> と同じ（+1 は敵の向きから見て左）。
        /// </summary>
        public static int FlankSideAt(Vec2 center, double facingRad, double frontageM, Vec2 p)
        {
            ToLocal(center, facingRad, p, out _, out double across);
            double third = frontageM / 6; // 中心から長辺の 1/6 までが中央の 1/3
            if (across > third) return +1;
            if (across < -third) return -1;
            return 0;
        }
    }
}
