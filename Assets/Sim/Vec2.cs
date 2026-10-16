using System;

namespace Battalion.Sim
{
    /// <summary>2次元ベクトル（メートル）。x 正が東、y 正が北。</summary>
    public readonly struct Vec2
    {
        public readonly double X;
        public readonly double Y;

        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);

        public double Length => Math.Sqrt(X * X + Y * Y);

        public double Dot(Vec2 o) => X * o.X + Y * o.Y;

        public static double Distance(Vec2 a, Vec2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>角度（ラジアン）から単位ベクトルを作る。0 が東。</summary>
        public static Vec2 FromAngle(double rad) => new Vec2(Math.Cos(rad), Math.Sin(rad));

        public override string ToString() => $"({X}, {Y})";
    }

    public static class Angles
    {
        public const double DegToRad = Math.PI / 180.0;
        public const double RadToDeg = 180.0 / Math.PI;

        /// <summary>角度を (-π, π] に正規化する。</summary>
        public static double Wrap(double rad)
        {
            double r = (rad + Math.PI) % (2 * Math.PI);
            if (r < 0) r += 2 * Math.PI;
            return r - Math.PI;
        }
    }
}
