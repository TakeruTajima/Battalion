using System;

namespace Battalion.Sim
{
    /// <summary>
    /// 中隊の矩形（向きのある長方形。正面幅は向きに直交する長辺、奥行は向きに沿う短辺）と、矩形どうしの隙間。
    /// P1d の「敵の中隊とは重ならない」「壊走中の味方のすり抜け」に使う。
    /// </summary>
    public readonly struct UnitRect
    {
        public readonly Vec2 A, B, C, D;
        public readonly Vec2 Center;
        /// <summary>中心から角までの距離（遠い矩形をすぐに除くのに使う）。</summary>
        public readonly double Radius;

        public UnitRect(Vec2 center, double facingRad, double frontageM, double depthM)
        {
            var f = Vec2.FromAngle(facingRad);
            var l = new Vec2(-f.Y, f.X);
            double hd = depthM / 2, hf = frontageM / 2;
            Center = center;
            A = center + f * hd + l * hf;
            B = center + f * hd - l * hf;
            C = center - f * hd - l * hf;
            D = center - f * hd + l * hf;
            Radius = Math.Sqrt(hd * hd + hf * hf);
        }

        Vec2 this[int k] => k == 0 ? A : k == 1 ? B : k == 2 ? C : D;

        /// <summary>2つの矩形が重なっているか（分離軸：それぞれの2辺の向き）。</summary>
        public static bool Overlap(in UnitRect p, in UnitRect q)
        {
            return !Separated(p, q, p.B - p.A) && !Separated(p, q, p.D - p.A)
                && !Separated(p, q, q.B - q.A) && !Separated(p, q, q.D - q.A);
        }

        static bool Separated(in UnitRect p, in UnitRect q, Vec2 axis)
        {
            Project(p, axis, out double p0, out double p1);
            Project(q, axis, out double q0, out double q1);
            return p1 < q0 || q1 < p0;
        }

        static void Project(in UnitRect r, Vec2 axis, out double lo, out double hi)
        {
            lo = double.MaxValue;
            hi = double.MinValue;
            for (int k = 0; k < 4; k++)
            {
                double v = r[k].Dot(axis);
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        }

        /// <summary>
        /// 2つの矩形の隙間（m）。重なっていれば 0。重なっていなければ、互いの頂点から相手の辺への最短距離の最小。
        /// <paramref name="farBeyond"/> より明らかに遠ければ、計算せずに中心どうしの距離から見積もった値を返す。
        /// </summary>
        public static double Gap(in UnitRect p, in UnitRect q, double farBeyond = double.MaxValue)
        {
            double centers = Vec2.Distance(p.Center, q.Center);
            double lower = centers - p.Radius - q.Radius;
            if (lower > farBeyond) return lower;
            if (Overlap(p, q)) return 0;
            double best = double.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                for (int k = 0; k < 4; k++)
                {
                    best = Math.Min(best, PointSegment(p[i], q[k], q[(k + 1) % 4]));
                    best = Math.Min(best, PointSegment(q[i], p[k], p[(k + 1) % 4]));
                }
            }
            return best;
        }

        static double PointSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            var ab = b - a;
            double len2 = ab.Dot(ab);
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - a).Dot(ab) / len2));
            return Vec2.Distance(p, a + ab * t);
        }

        /// <summary>点から矩形までの距離（中なら 0）。</summary>
        public double DistanceTo(Vec2 p)
        {
            var ab = B - A;
            var ad = D - A;
            double u = (p - A).Dot(ab) / ab.Dot(ab), v = (p - A).Dot(ad) / ad.Dot(ad);
            if (u >= 0 && u <= 1 && v >= 0 && v <= 1) return 0;
            double best = double.MaxValue;
            for (int k = 0; k < 4; k++) best = Math.Min(best, PointSegment(p, this[k], this[(k + 1) % 4]));
            return best;
        }

        /// <summary>軸に平行なマス（左下 x0,y0、右上 x1,y1）と重なっているか（分離軸）。</summary>
        public bool OverlapsBox(double x0, double y0, double x1, double y1)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int k = 0; k < 4; k++) { lo = Math.Min(lo, this[k].X); hi = Math.Max(hi, this[k].X); }
            if (hi < x0 || lo > x1) return false;
            lo = double.MaxValue; hi = double.MinValue;
            for (int k = 0; k < 4; k++) { lo = Math.Min(lo, this[k].Y); hi = Math.Max(hi, this[k].Y); }
            if (hi < y0 || lo > y1) return false;
            var box = new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };
            foreach (var axis in new[] { B - A, D - A })
            {
                Project(this, axis, out double r0, out double r1);
                double b0 = double.MaxValue, b1 = double.MinValue;
                foreach (var v in box)
                {
                    double d = v.Dot(axis);
                    b0 = Math.Min(b0, d);
                    b1 = Math.Max(b1, d);
                }
                if (r1 < b0 || b1 < r0) return false;
            }
            return true;
        }
    }
}
