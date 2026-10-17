using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    // P1d：行動の規則（仕様「P1d：行動の規則（確定）」の1〜7）。
    // 攻撃の側の選び直しと方角の固定、敵の中隊と重ならない、拘束、追撃の許可、向き直り、壊走中の味方のすり抜け、逃げる方位の揺れ止め。
    public sealed partial class Simulation
    {
        SimParams.BehaviorParams B => _p.Behavior;

        // ---------------------------------------------------------------- 矩形

        /// <summary>中隊の今の矩形。</summary>
        public UnitRect RectOf(Company c) => RectAt(c.Position, c.Facing);

        UnitRect RectAt(Vec2 pos, double facing) => new UnitRect(pos, facing, _p.Movement.FrontageM, _p.Movement.DepthM);

        /// <summary>敵の矩形として数える中隊：盤上にいる（降伏・離脱を除く）。壊走中・再集結中の敵も含む。</summary>
        static bool BlocksAsEnemy(Company e, Company c) => e.OnBoard && e.Side != c.Side;

        /// <summary>中隊を <paramref name="pos"/> に置いたときの、敵の矩形との隙間の最小（向きは今のまま）。</summary>
        double EnemyGapAt(Company c, Vec2 pos, double facing)
        {
            var me = RectAt(pos, facing);
            double far = B.EnemyMinGapM + 1;
            double best = double.MaxValue;
            foreach (var e in _companies)
            {
                if (!BlocksAsEnemy(e, c)) continue;
                best = Math.Min(best, UnitRect.Gap(me, RectOf(e), far));
            }
            return best;
        }

        double NearestEnemyCenterDistanceAt(Company c, Vec2 pos)
        {
            double best = double.MaxValue;
            foreach (var e in _companies)
                if (BlocksAsEnemy(e, c)) best = Math.Min(best, Vec2.Distance(pos, e.Position));
            return best;
        }

        /// <summary>
        /// 動いた中隊を、敵の矩形との隙間が 10m 未満にならない所までで止める（P1d の2）。
        /// 隙間がもう 10m 未満のときは、隙間が広がる動き（重なっているなら中心が離れる動き）だけを許す。止めたら true。
        /// </summary>
        bool ClampAgainstEnemies(Company c, Vec2 before)
        {
            var after = c.Position;
            if (after.X == before.X && after.Y == before.Y) return false;
            double min = B.EnemyMinGapM;
            double gapAfter = EnemyGapAt(c, after, c.Facing);
            if (gapAfter >= min) return false;
            double gapBefore = EnemyGapAt(c, before, c.Facing);
            if (gapAfter > gapBefore) return false; // 離れる動き
            if (gapAfter <= 0 && gapBefore <= 0 && NearestEnemyCenterDistanceAt(c, after) > NearestEnemyCenterDistanceAt(c, before))
                return false; // 重なりから抜け出す動き
            if (gapBefore < min)
            {
                c.Position = before;
                return true;
            }
            // 隙間がちょうど 10m になる所まで（二分法）
            double lo = 0, hi = 1;
            var d = after - before;
            for (int k = 0; k < 20; k++)
            {
                double mid = (lo + hi) / 2;
                if (EnemyGapAt(c, before + d * mid, c.Facing) >= min) lo = mid;
                else hi = mid;
            }
            c.Position = before + d * lo;
            return true;
        }

        // ---------------------------------------------------------------- 経路で敵を避ける

        readonly bool[][] _enemyCells = new bool[2][];

        /// <summary>
        /// <paramref name="side"/> の中隊から見て、経路で避けるマス：敵の矩形（降伏を除く）が掛かるマスと、
        /// 中心が敵の矩形から「動く中隊の矩形の半対角（約 103m）＋隙間 10m」以内のマス。動く中隊は幅 200m の矩形なので、
        /// 中心だけが敵のマスを避けても、横幅で敵の角をかすめて止まってしまうため。P1d は半幅（100m）だったが、
        /// 接敵中は向きを変えずに斜めに動くので、どの向きでも引っかからない半対角にした（P1e.1）。
        /// 露出の地図と同じく10秒ごとに作り直す（その後に初めて要るときに、その時点の位置で作る）。
        /// </summary>
        bool[] EnemyCells(Side side)
        {
            int s = (int)side;
            if (_enemyCells[s] != null) return _enemyCells[s];
            var cells = new bool[_map.Width * _map.Height];
            double clear = Math.Sqrt(_p.Movement.FrontageM * _p.Movement.FrontageM + _p.Movement.DepthM * _p.Movement.DepthM) / 2
                           + B.EnemyMinGapM;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == side) continue;
                var r = RectOf(e);
                Visibility.ForCellsWithin(_map, e.Position, r.Radius + clear + _map.CellM, (i, j, center) =>
                {
                    double x0 = _map.OriginX + i * _map.CellM, y0 = _map.OriginY + j * _map.CellM;
                    if (r.DistanceTo(center) < clear || r.OverlapsBox(x0, y0, x0 + _map.CellM, y0 + _map.CellM))
                        cells[j * _map.Width + i] = true;
                });
            }
            if (!_previewing) _enemyCells[s] = cells;
            return cells;
        }

        // ---------------------------------------------------------------- 1. 攻撃の側と方角の固定、4. 追撃

        /// <summary>
        /// 攻撃の続き（P1d の1と4）。
        /// 着く前：30秒ごとに、攻める方角を選び直して（P1e の4。地図の方角で指定した攻撃はそのまま）発起点を計算し直す。
        /// 着いた後：目標の中心から見た自分の方角を固定し、目標の中心が 50m 以上動いたときだけ、その方角の 250m の点へ動き直す。
        /// 目標が崩れたら、確保か追撃（性格による）に移る。
        /// </summary>
        void UpdateAttack(Company c, double t, double tickStart, double maxTurn)
        {
            if (c.Pursuing)
            {
                UpdatePursuit(c, t, tickStart, maxTurn);
                return;
            }
            var tgt = EnemyOf(c, c.CurrentOrder.TargetId);
            if (tgt == null || !tgt.UnderCommand)
            {
                OnAttackTargetBroken(c, tgt, t, tickStart);
                return;
            }

            if (!c.AttackFixed)
            {
                // 着いた：発起点に届いた。届かない発起点の手前で止まった、敵の手前で止められた（P1e.1）だけなら
                // 着いたことにせず、30秒ごとに引き直し続ける
                bool reached = c.Arrived && Vec2.Distance(c.Position, c.MoveGoal) <= 1.0;
                if (reached)
                {
                    // 着いた：目標の中心から見た自分の方角を固定する
                    var d = c.Position - tgt.Position;
                    c.AttackFixed = true;
                    c.AttackFixedBearing = Math.Atan2(d.Y, d.X);
                    c.AttackFixedTargetPos = tgt.Position;
                }
                else if (tickStart >= c.NextReplanTime)
                {
                    ChooseAttackBearing(c, tgt, c.CurrentOrder, false);
                    c.MoveGoal = AdjustDestination(c, JumpOff(tgt, c.AttackBearing));
                    c.Arrived = false;
                    Plan(c, tickStart);
                }
            }
            else if (Vec2.Distance(tgt.Position, c.AttackFixedTargetPos) >= _p.MapRules.AttackRepositionM)
            {
                // 目標が場所ごと動いた：固定した方角の 250m の点へ動き直す
                c.AttackFixedTargetPos = tgt.Position;
                c.MoveGoal = AdjustDestination(c, tgt.Position + Vec2.FromAngle(c.AttackFixedBearing) * _p.MapRules.AttackStandoffM);
                c.Arrived = false;
                c.FacingOverride = null;
                Plan(c, tickStart);
            }

            if (c.AttackFixed && c.Arrived && !c.FacingOverride.HasValue) FaceToward(c, tgt.Position, maxTurn);
        }

        /// <summary>
        /// 攻撃の目標が指揮下でなくなった（壊走・降伏・離脱・再集結）。確保なら固める。追撃可なら性格で決める：
        /// 慎重は確保、冷静は 400m 以内に指揮下の敵がいなければ追う、機敏と猪突は追う（猪突は限界 1.5 倍）。
        /// 追えるのは目標が壊走中のときだけ。
        /// </summary>
        void OnAttackTargetBroken(Company c, Company tgt, double t, double tickStart)
        {
            var o = c.CurrentOrder;
            double mult = B.PursuitLimitMult[c.Personality];
            bool pursue = o.OnBreak == AttackOnBreak.Pursue && tgt != null && tgt.State == CompanyState.Routed && mult > 0;
            if (pursue && c.Personality == Personality.Calm && EnemyUnderCommandWithin(c, B.PursuitCalmClearRadiusM)) pursue = false;
            if (_log.Enabled)
                _log.Write(Ev(t, "target_broken", c)
                    .Add("target", tgt != null ? tgt.Name : _companies[o.TargetId].Name)
                    .Add("decision", pursue ? "pursue" : "secure")
                    .ToString());
            if (!pursue)
            {
                Secure(c, tgt ?? _companies[o.TargetId]);
                return;
            }
            c.Pursuing = true;
            c.PursuitTargetId = tgt.Id;
            c.PursuitStart = c.Position;
            c.PursuitLimitM = B.PursuitLimitM * mult;
            c.NextPursuitPlan = double.NegativeInfinity;
            c.FacingOverride = null;
        }

        /// <summary>
        /// 追う動き：5秒ごとに、壊走中の目標の中心から自分の方へ 100m の点へ経路を引く。撃つ目標は追っている中隊。
        /// 限界（追撃を始めた地点から）を越えた、目標が降伏・離脱・再集結した、冷静の 400m 以内に指揮下の敵が現れた、で確保に移る。
        /// </summary>
        void UpdatePursuit(Company c, double t, double tickStart, double maxTurn)
        {
            var tgt = _companies[c.PursuitTargetId];
            string stop = null;
            if (tgt.State != CompanyState.Routed) stop = "target_" + Names.Of(tgt.State);
            else if (Vec2.Distance(c.Position, c.PursuitStart) > c.PursuitLimitM) stop = "limit";
            else if (c.Personality == Personality.Calm && EnemyUnderCommandWithin(c, B.PursuitCalmClearRadiusM)) stop = "enemy_near";
            if (stop != null)
            {
                if (_log.Enabled) _log.Write(Ev(t, "pursuit_end", c).Add("target", tgt.Name).Add("reason", stop).ToString());
                Secure(c, tgt);
                return;
            }
            if (tickStart >= c.NextPursuitPlan)
            {
                c.NextPursuitPlan = tickStart + B.PursuitReplanSec;
                var d = c.Position - tgt.Position;
                double len = d.Length;
                var goal = len > 1e-9 ? tgt.Position + d * (B.PursuitKeepDistanceM / len) : c.Position;
                if (Vec2.Distance(goal, c.Position) > 1)
                {
                    c.MoveGoal = AdjustDestination(c, goal);
                    c.Arrived = false;
                    Plan(c, tickStart);
                    c.NextReplanTime = double.PositiveInfinity; // 追撃中は通常の30秒の引き直しをしない
                }
            }
            if (c.Arrived) FaceToward(c, tgt.Position, maxTurn);
        }

        bool EnemyUnderCommandWithin(Company c, double radius)
        {
            foreach (var e in _companies)
                if (e.OnBoard && e.Side != c.Side && e.UnderCommand && Vec2.Distance(c.Position, e.Position) <= radius) return true;
            return false;
        }

        /// <summary>
        /// 確保：その場で保持して固める。向きは、400m 以内に指揮下の敵がいれば最も近いその敵の方、
        /// いなければ目標が逃げていった方角（壊走中ならその逃げる方位、それ以外は目標の最後の位置の方）。
        /// </summary>
        void Secure(Company c, Company tgt)
        {
            double? facing = null;
            Company nearest = null;
            double best = double.MaxValue;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || !e.UnderCommand) continue;
                double d = Vec2.Distance(c.Position, e.Position);
                if (d <= B.SecureFaceEnemyRadiusM && d < best)
                {
                    best = d;
                    nearest = e;
                }
            }
            if (nearest != null)
            {
                var d = nearest.Position - c.Position;
                facing = Math.Atan2(d.Y, d.X);
            }
            else if (tgt != null)
            {
                if (tgt.State == CompanyState.Routed && tgt.FleeDirection.HasValue) facing = tgt.FleeDirection.Value;
                else
                {
                    var d = tgt.Position - c.Position;
                    if (d.Length > 1e-9) facing = Math.Atan2(d.Y, d.X);
                }
            }
            c.Pursuing = false;
            c.PursuitTargetId = -1;
            c.FacingOverride = null;
            c.TurnWaitStart = null;
            c.CurrentOrder = Order.Hold(c.Id, facing);
            c.Arrived = true;
            c.Path = null;
        }

        // ---------------------------------------------------------------- 3. 拘束

        /// <summary>
        /// 拘束の命令の始め：今いる方角を覚え、拘束の距離（遠い 350m／近い 125m。P1e の3）の点まで詰める。
        /// 動揺中と、すでにその距離以内なら詰めない。
        /// </summary>
        void StartPin(Company c, Company tgt, double tickStart)
        {
            var d = c.Position - tgt.Position;
            c.PinBearing = Math.Atan2(d.Y, d.X);
            if (c.State == CompanyState.Shaken || d.Length <= PinDistance(c))
            {
                c.Arrived = true;
                c.Path = null;
                return;
            }
            c.MoveGoal = AdjustDestination(c, PinPoint(tgt, c));
            c.Arrived = false;
            Plan(c, tickStart);
        }

        double PinDistance(Company c) => c.CurrentOrder.PinRange == PinRange.Near ? B.PinDistanceM : B.PinDistanceFarM;

        Vec2 PinPoint(Company tgt, Company c) => tgt.Position + Vec2.FromAngle(c.PinBearing) * PinDistance(c);

        /// <summary>
        /// 拘束の続き：30秒ごとに、目標の今の位置から同じ方角の拘束の距離の点を計算し直す。着いた後は 50m 以上ずれたら動き直す
        /// （目標に拘束の距離より近ければ動かない）。動揺中は詰めない。着いたら目標に正面を向ける。
        /// </summary>
        void UpdatePin(Company c, Company tgt, double tickStart, double maxTurn)
        {
            if (c.State != CompanyState.Shaken && tickStart >= c.NextReplanTime)
            {
                var goal = PinPoint(tgt, c);
                bool far = Vec2.Distance(c.Position, tgt.Position) > PinDistance(c);
                if (!c.Arrived || (far && Vec2.Distance(goal, c.Position) > _p.MapRules.AttackRepositionM))
                {
                    c.MoveGoal = AdjustDestination(c, goal);
                    c.Arrived = false;
                    c.FacingOverride = null;
                    Plan(c, tickStart);
                }
                else
                {
                    c.NextReplanTime = tickStart + _p.MapRules.ReplanSec;
                }
            }
            if (c.Arrived || c.State == CompanyState.Shaken) FaceToward(c, tgt.Position, maxTurn);
        }

        // ---------------------------------------------------------------- 5. 向き直り

        /// <summary>向き直りの対象か：止まっている保持・陣地構築・攻撃（着いた後、追撃中を除く）・前進や後退（着いた後）。拘束は対象外。</summary>
        static bool CanReactByTurning(Company c)
        {
            if (!c.UnderCommand) return false;
            var o = c.CurrentOrder;
            switch (o.Type)
            {
                case OrderType.Hold: return true;
                case OrderType.Entrench: return !o.Moves || c.Arrived;
                case OrderType.Attack: return c.Arrived && c.AttackFixed && !c.Pursuing;
                case OrderType.Advance:
                case OrderType.Retreat: return c.Arrived;
                default: return false;
            }
        }

        /// <summary>
        /// 向き直り（P1d の5）。損害と士気の後に呼ぶ（その刻みの射撃の記録を使う）。
        /// 側面か背面から指揮下の敵に撃たれ、正面の弧が空いていれば（性格で距離と視線の要否が違う）、待ちの時計を始める。
        /// 練度の遅れが経ったら、最後に側面・背面から撃ってきた敵の方へ向き直る（旋回の速さで）。待ちの間に正面に敵が現れるか、
        /// 10秒間側面・背面から撃たれなければ取り消す。向き直った向きは新しい命令が出るまで保つ。
        /// </summary>
        void UpdateFacingReaction(double t)
        {
            foreach (var f in _fireLog)
            {
                if (f.Aspect == Aspect.Front) continue;
                var tg = _companies[f.Target];
                tg.LastFlankHitTime = t;
                tg.LastFlankShooter = f.Shooter;
            }
            foreach (var c in _companies)
            {
                double clear = B.TurnFrontClearM[c.Personality];
                if (clear < 0 || !CanReactByTurning(c))
                {
                    c.TurnWaitStart = null;
                    continue;
                }
                if (FrontArcOccupied(c, clear, B.TurnFrontNeedsSight[c.Personality]) || t - c.LastFlankHitTime >= B.TurnCancelQuietSec)
                {
                    c.TurnWaitStart = null;
                    continue;
                }
                if (c.LastFlankHitTime == t && !c.TurnWaitStart.HasValue)
                {
                    c.TurnWaitStart = t;
                    if (_log.Enabled) _log.Write(Ev(t, "turn_wait", c).Add("by", _companies[c.LastFlankShooter].Name).ToString());
                }
                if (c.TurnWaitStart.HasValue && t - c.TurnWaitStart.Value >= _p.Of(c.Training).OrderStartSec)
                {
                    var shooter = _companies[c.LastFlankShooter];
                    var d = shooter.Position - c.Position;
                    c.TurnWaitStart = null;
                    if (d.Length < 1e-9) continue;
                    c.FacingOverride = Math.Atan2(d.Y, d.X);
                    if (_log.Enabled) _log.Write(Ev(t, "turn_start", c).Add("toward", shooter.Name).ToString());
                }
            }
        }

        /// <summary>正面の弧（±60°）の <paramref name="radius"/> 以内に、指揮下の敵がいるか（必要なら視線が通る敵だけ）。</summary>
        bool FrontArcOccupied(Company c, double radius, bool needsSight)
        {
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || !e.UnderCommand) continue;
                if (Vec2.Distance(c.Position, e.Position) > radius) continue;
                if (AspectOf(c, e.Position) != Aspect.Front) continue;
                if (needsSight && !LineOfSight.Visible(_map, _p.Sight, c.Position, e.Position)) continue;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 6. 壊走中の味方のすり抜け

        /// <summary>壊走中の中隊が味方の矩形と重なっているか。</summary>
        bool OverlapsFriend(Company c, out List<Company> friends)
        {
            friends = null;
            var me = RectOf(c);
            foreach (var f in _companies)
            {
                if (f == c || f.Side != c.Side || !f.OnBoard) continue;
                if (!UnitRect.Overlap(me, RectOf(f))) continue;
                if (friends == null) friends = new List<Company>();
                friends.Add(f);
            }
            return friends != null;
        }

        /// <summary>すり抜けられた指揮下の味方：その壊走中の中隊ごとに一度だけ士気 −5、30秒間の射撃効率 ×0.5（隊形の乱れ）。</summary>
        void ApplyPassThrough(Company routed, double t)
        {
            if (!OverlapsFriend(routed, out var friends)) return;
            foreach (var f in friends)
            {
                if (!f.UnderCommand || f.PassedBy.Contains(routed.Id)) continue;
                f.PassedBy.Add(routed.Id);
                double hit = B.RoutPassMoraleHit * _p.Of(f.Training).MoraleSensitivity;
                f.Morale = Math.Max(0, f.Morale - hit);
                f.Ledger.Disarray -= hit;
                f.DisarrayUntil = t + B.DisarraySec;
                if (_log.Enabled) _log.Write(Ev(t, "disarray", f).Add("by", routed.Name).Add("morale_hit", hit).ToString());
            }
        }
    }
}
