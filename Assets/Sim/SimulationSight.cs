using System;

namespace Battalion.Sim
{
    // P1g：目標が見えないときの中隊長の判断（仕様「P1g：目標が見えないときの中隊長の判断（確定）」）。
    // 攻撃と拘束の中隊は、命令の目標が見えなければ（視線が通らない、または射程外）、30秒ごとに見える位置を探して動く。
    // 互いの矩形の隙間が 50m 以内（接触中）なら、視線を問わずに撃ち合える。
    public sealed partial class Simulation
    {
        /// <summary>接触中か：互いの矩形の隙間が <c>behavior.sight_contact_gap_m</c>（50m）以内。視線を問わずに撃ち合える。</summary>
        public bool InFireContact(Company a, Company b)
        {
            double limit = B.SightContactGapM;
            return UnitRect.Gap(RectOf(a), RectOf(b), limit + 1) <= limit;
        }

        /// <summary>
        /// 中隊 <paramref name="c"/> が敵 <paramref name="e"/> に気付いているか（P2c.1.2）：視線が通るか、接触の距離（矩形の隙間 50m 以内）にいる。
        /// 撃ち合い（撃つための視線）、接敵中、向き直りの正面の弧、自律の向きで同じ判定を使う。
        /// </summary>
        public bool Aware(Company c, Company e) =>
            InFireContact(c, e) || LineOfSight.Visible(_map, _p.Sight, c.Position, e.Position);

        /// <summary>
        /// 点 <paramref name="p"/> に向き <paramref name="facing"/> で置いた中隊の矩形が、矩形 <paramref name="enemy"/> と接触の距離（隙間 50m 以内）か。
        /// 物陰の候補の点、退路の見積もりの各刻みの点で、気付いている（撃てる）かを見るのに使う（P2c.1.2）。
        /// </summary>
        bool InContactAt(Vec2 p, double facing, in UnitRect enemy)
        {
            double limit = B.SightContactGapM;
            return UnitRect.Gap(RectAt(p, facing), enemy, limit + 1) <= limit;
        }

        /// <summary>撃つための視線：気付いているか（接触中なら視線を問わない）。</summary>
        bool SightForFire(Company shooter, Company e) => Aware(shooter, e);

        /// <summary>命令の目標と交戦できるか（射程内で、視線が通るか接触中）。</summary>
        bool CanEngage(Company c, Company tgt) => CanFireAt(c, tgt);

        /// <summary>点 <paramref name="p"/> から目標が見えて射程内か（探索の候補の判定）。</summary>
        bool EngageableFrom(Vec2 p, Company tgt) =>
            Vec2.Distance(p, tgt.Position) <= _p.Fire.RangeMaxM && LineOfSight.Visible(_map, _p.Sight, p, tgt.Position);

        void LogSeek(double t, Company c, Company tgt, string how)
        {
            if (_log.Enabled)
                _log.Write(Ev(t, "seek_sight", c)
                    .Add("target", tgt.Name)
                    .Add("how", how)
                    .Add("x", c.MoveGoal.X)
                    .Add("y", c.MoveGoal.Y)
                    .ToString());
        }

        /// <summary>
        /// 攻撃：発起点に着いた後、目標と交戦できなければ、固定した方角に沿って目標へ詰め、見えて射程内になる最初の点へ動く
        /// （10m 刻みで今の距離から近づけて調べる）。敵の矩形と 10m の隙間が取れなくなるまでに見つからなければ、その手前の点まで詰め、
        /// 接触の規則（隙間 50m 以内は視線を問わない）に任せる。
        /// </summary>
        void SeekSightForAttack(Company c, Company tgt, double t, double tickStart)
        {
            var dir = Vec2.FromAngle(c.AttackFixedBearing);
            double facing = c.AttackFixedBearing + Math.PI; // 目標の方を向いて詰める
            double d0 = Vec2.Distance(c.Position, tgt.Position);
            Vec2? best = null, last = null;
            for (double d = d0 - B.SightSeekStepM; d > 0; d -= B.SightSeekStepM)
            {
                var p = tgt.Position + dir * d;
                if (!_map.Contains(p) || !_map.Passable(p)) continue;
                if (EnemyGapAt(c, p, facing) < B.EnemyMinGapM) break;
                last = p;
                if (EngageableFrom(p, tgt))
                {
                    best = p;
                    break;
                }
            }
            var goal = best ?? last;
            if (!goal.HasValue) return;
            c.MoveGoal = goal.Value;
            c.Arrived = false;
            c.SeekingSight = true;
            Plan(c, tickStart);
            LogSeek(t, c, tgt, "advance");
        }

        /// <summary>
        /// 拘束：拘束点に着いた後、目標と交戦できなければ、目標からの今の距離を保ったまま今の方角の左右 ±45°（5° 刻み）で見える位置を探し、
        /// 今の位置に最も近い（方角の差の小さい）位置へ移る。なければ今の方角に沿って詰め、見えて射程内になる最初の点へ（近い拘束の 125m より
        /// 近くへは詰めない。それでも見えなければ 125m の点へ）。
        /// </summary>
        void SeekSightForPin(Company c, Company tgt, double t, double tickStart)
        {
            var off = c.Position - tgt.Position;
            double d = off.Length;
            double bearing = Math.Atan2(off.Y, off.X);
            int steps = (int)Math.Round(B.SightSeekSideDeg / B.SightSeekSideStepDeg);
            for (int k = 1; k <= steps; k++)
            {
                foreach (int sign in new[] { +1, -1 })
                {
                    double b = bearing + sign * k * B.SightSeekSideStepDeg * Angles.DegToRad;
                    var p = tgt.Position + Vec2.FromAngle(b) * d;
                    if (!_map.Contains(p) || !_map.Passable(p) || !EngageableFrom(p, tgt)) continue;
                    c.PinBearing = b;
                    c.PinStandoffM = d;
                    c.MoveGoal = AdjustDestination(c, p);
                    c.Arrived = false;
                    c.SeekingSight = true;
                    Plan(c, tickStart);
                    LogSeek(t, c, tgt, "side");
                    return;
                }
            }
            // 左右になければ、今の方角に沿って詰める（125m まで）
            var dir = Vec2.FromAngle(bearing);
            double stop = B.PinDistanceM;
            if (d <= stop + 1e-6) return;
            double found = stop;
            for (double r = d - B.SightSeekStepM; r > stop; r -= B.SightSeekStepM)
            {
                var p = tgt.Position + dir * r;
                if (!_map.Contains(p) || !_map.Passable(p)) continue;
                if (EngageableFrom(p, tgt))
                {
                    found = r;
                    break;
                }
            }
            c.PinBearing = bearing;
            c.PinStandoffM = found;
            c.MoveGoal = AdjustDestination(c, tgt.Position + dir * found);
            c.Arrived = false;
            c.SeekingSight = true;
            Plan(c, tickStart);
            LogSeek(t, c, tgt, "advance");
        }
    }
}
