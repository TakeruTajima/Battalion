using System;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2c.1.1：接触の距離の敵と「接敵中」（仕様「P2c.1.1：接触の距離の敵と『接敵中』（確定）」）。</summary>
    public class P2c11Tests
    {
        /// <summary>
        /// 40×40 マス（50m）の平地で、北の行（中心 y=1025m）だけを高さ <paramref name="ridgeM"/> の稜線にした地図。
        /// </summary>
        static TerrainMap RidgeMap(double ridgeM)
        {
            const int w = 40, h = 40;
            var sb = new StringBuilder();
            sb.Append("{\"cell_m\":50,\"width\":40,\"height\":40,\"rows_order\":\"north_first\",\"terrain\":[");
            sb.Append(string.Join(",", Enumerable.Repeat("\"" + new string('.', w) + "\"", h)));
            sb.Append("],\"elevation\":[");
            for (int r = 0; r < h; r++)
            {
                int j = h - 1 - r;
                double z = j == 20 ? ridgeM : 0;
                sb.Append(r > 0 ? "," : "").Append("[").Append(string.Join(",", Enumerable.Repeat(z.ToString(System.Globalization.CultureInfo.InvariantCulture), w))).Append("]");
            }
            sb.Append("]}");
            return TerrainMap.FromJson(JsonValue.Parse(sb.ToString()), TestUtil.Params.Terrain);
        }

        /// <summary>
        /// 稜線を挟んで、矩形の隙間 40m（接触の距離 50m 以内）に敵がいる。視線は通らない。北の敵を向いたまま東へ前進すると、
        /// 接敵中として向きを保ち、横歩き（×0.5）・後ろ歩き（×0.6）で動く。稜線がなければ（視線が通れば）同じく接敵中で、P2c.1 から変わらない。
        /// </summary>
        [TestCase(30.0)]
        [TestCase(0.0)]
        public void KeepsFacingWhenMovingNextToAContactEnemyBehindARidge(double ridge)
        {
            var sim = new Simulation(TestUtil.Params, 1, RidgeMap(ridge));
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(1025, 975), 90);
            var e = sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(1025, 1065), -90);
            Assert.That(sim.InFireContact(a, e), Is.True, "隙間 40m：接触の距離（50m 以内）");
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, a.Position, e.Position), Is.EqualTo(ridge == 0), "稜線で視線が通らない");
            sim.Issue(Order.Advance(a.Id, new Vec2(1425, 975)));
            for (int k = 0; k < 10; k++)
            {
                a.Morale = 100;
                e.Morale = 100;
                sim.Step();
            }
            Assert.That(a.InContact, Is.True, "接敵中");
            // 敵の周りのマスを避ける経路なので、まず南東へ下がる（後ろ歩き）。どちらでも向きは変えない
            Assert.That(a.MoveStyle == MoveStyle.Sideways || a.MoveStyle == MoveStyle.Backward, Is.True, $"向きを保って横歩き・後ろ歩き（{a.MoveStyle}）");
            Assert.That(a.Facing * Angles.RadToDeg, Is.EqualTo(90).Within(1e-6), "北の敵を向いたまま");
            double moved = Vec2.Distance(a.Position, new Vec2(1025, 975));
            double fast = 10 * sim.Params.TickSec * sim.Params.Movement.SpeedMps;
            Assert.That(moved, Is.GreaterThan(0).And.LessThanOrEqualTo(fast * Math.Max(sim.Params.Movement.SidewaysSpeedMult, sim.Params.Movement.RetreatSpeedMult) + 1e-6),
                "横歩き・後ろ歩きの速さ");
        }
    }
}
