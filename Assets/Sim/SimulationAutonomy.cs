using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    // P2c：中隊長の自律行動（仕様「P2c：中隊長の自律行動（確定）」）。
    // 持ち場と裁量、全ての中隊長の自律行動（向き・物陰・浅い掩体・崩した敵の陣地に入る）、性格の独断（猪突・機敏・慎重）。
    // 判断は刻みの最後（段の進行の後）にまとめて行い、動きは次の刻みの移動から効く。追撃と猪突の続けての攻撃は、
    // 目標が崩れた・追撃を終えた刻みに攻撃の続き（SimulationBehavior）の中で決める。
    public sealed partial class Simulation
    {
        SimParams.AutonomyParams A => _p.Autonomy;

        // ---------------------------------------------------------------- 裁量と拒む士気

        /// <summary>命令の裁量：今の命令書の今の段の命令なら、その段の裁量。見つからなければ任せる。</summary>
        Discretion DiscretionOf(Company c, Order o)
        {
            var sh = c.ActiveSheet;
            if (sh != null && c.StageIndex < sh.Stages.Count && ReferenceEquals(sh.Stages[c.StageIndex].Order, o))
                return sh.Stages[c.StageIndex].Discretion;
            return Discretion.Delegate;
        }

        /// <summary>
        /// 前進・攻撃を拒むか。厳命は士気 50 以下（動揺）。任せるは性格の値：猪突 40 以下、慎重 60 未満、ほかは 50 以下。
        /// </summary>
        public bool RefusesAdvance(Company c)
        {
            if (c.Discretion == Discretion.Strict) return c.Morale <= _p.Morale.ShakenThreshold;
            double v = A.AdvanceRefuseMorale[c.Personality];
            return c.Personality == Personality.Cautious ? c.Morale < v : c.Morale <= v;
        }

        /// <summary>
        /// 拒む士気で前進を止める・再開する（P2b.2 の動揺で停止中を、拒む士気で判定する。P2c）。状態遷移の中で毎刻み呼ぶ。
        /// </summary>
        void UpdateRefusal(Company c, double t)
        {
            bool refuse = RefusesAdvance(c);
            if (refuse && !c.Arrived
                && (c.CurrentOrder.Type == OrderType.Advance || (c.CurrentOrder.Type == OrderType.Entrench && c.CurrentOrder.Moves)))
            {
                // 今の段は捨てずに「動揺で停止中」とする（P2b.2）
                c.ShakenHalt = true;
                c.HaltedOrder = c.CurrentOrder;
                c.CurrentOrder = Order.Hold(c.Id, c.CurrentOrder.FacingRad);
                c.Arrived = true;
                c.PostPoint = c.Position;
                c.PostSettled = true;
            }
            else if (!refuse && c.ShakenHalt && c.HaltedOrder != null && c.StartWaitOrder == null)
            {
                // 拒む士気を越えたら、止めた段を練度の遅れの後に同じ目的地で再開する（P2b.2）
                c.StartWaitOrder = c.HaltedOrder;
                c.StartWaitAt = t + _p.Of(c.Training).OrderStartSec;
                c.ShakenHalt = false;
                c.HaltedOrder = null;
                if (_log.Enabled) _log.Write(Ev(t, "halt_resume", c).Add("side", SideName(c.Side)).ToString());
            }
        }

        /// <summary>自律行動・独断をやめる（新しい命令、壊走、再集結の完了）。</summary>
        static void ClearAutonomy(Company c)
        {
            c.AutoMove = AutoMove.None;
            c.AutoFacing = null;
            c.UnderFireSince = null;
            c.CoverWaitStart = null;
            c.Independent = IndependentAct.None;
            c.IndependentWaitOrder = null;
            c.IndependentStopped = false;
            c.BrokenTargetPos = null;
            c.EnterGoal = null;
        }

        /// <summary>
        /// 全ての中隊長の自律行動の対象か：止まっている（命令待ち・保持・確保・着いた後・動揺で停止中・拘束点）。
        /// 攻撃の途中、追撃中、独断中、見える位置を探して動いている間、自律行動の小さな動きの間は除く。
        /// </summary>
        static bool Settled(Company c) =>
            c.AutoMove == AutoMove.None && !c.Pursuing && c.Independent == IndependentAct.None
            && c.CurrentOrder.Type != OrderType.Attack && (!c.CurrentOrder.Moves || c.Arrived) && !c.SeekingSight;

        // ---------------------------------------------------------------- 刻みの最後

        void UpdateAutonomy(double t)
        {
            RecordShotsAt(t);
            foreach (var c in _companies)
            {
                if (!c.OnBoard || !c.UnderCommand) continue;

                TrackFireAndStillness(c, t);
                EndAutoMoveIfDone(c, t);
                // 独断の攻撃が別の命令に変わった（目標がいなかった等）ら、独断は終わり
                if (c.Independent != IndependentAct.None && !c.Pursuing && c.CurrentOrder.Type != OrderType.Attack)
                {
                    c.Independent = IndependentAct.None;
                    c.PostPoint = c.Position;
                }

                bool settled = Settled(c);
                UpdateScrape(c, t, settled);
                if (!settled)
                {
                    c.CoverWaitStart = null;
                    c.AutoFacing = null;
                    continue;
                }
                if (!c.PostSettled)
                {
                    // 動く命令の持ち場は、着いて止まった位置
                    c.PostPoint = c.Position;
                    c.PostSettled = true;
                }
                // 制圧下は、移動を伴う自律行動をしない（R1。向きは変えてよい）
                if (c.Suppressed)
                {
                    c.CoverWaitStart = null;
                    UpdateAutoFacing(c, t);
                    continue;
                }
                // 次の段・新しい命令が始まろうとしている間は、新しく動かない
                if (c.StartWaitOrder != null || PendingOrderOf(c.Id) != null)
                {
                    c.CoverWaitStart = null;
                    c.IndependentWaitOrder = null;
                    continue;
                }
                if (c.IndependentWaitOrder != null)
                {
                    StepIndependentWait(c, t);
                    continue;
                }
                if (c.EnterGoal.HasValue && t >= c.NextEnterTry - 1e-9)
                {
                    StartEnterPosition(c, c.EnterGoal.Value, t);
                    if (c.AutoMove != AutoMove.None) continue;
                }
                if (UpdateCover(c, t)) continue;
                if (UpdateAgile(c, t)) continue;
                UpdateAutoFacing(c, t);
            }
        }

        /// <summary>
        /// 撃たれ続けているか（開けた所で、止まって）と、動いたか（浅い掩体を失う）を数える。
        /// </summary>
        void TrackFireAndStillness(Company c, double t)
        {
            if (c.MovedThisTick)
            {
                c.StillSince = t;
                c.UnderFireSince = null;
                c.ScrapeProgress = 0;
                return;
            }
            bool hit = c.LastTargetedTime == t;
            if (hit)
            {
                if (TerrainDamageMult(c) >= A.CoverOpenMult - 1e-9)
                {
                    if (!c.UnderFireSince.HasValue) c.UnderFireSince = t;
                }
                else c.UnderFireSince = null;
            }
            else if (c.UnderFireSince.HasValue && t - c.LastTargetedTime >= A.CoverFireGapSec - 1e-9)
            {
                c.UnderFireSince = null;
            }
        }

        // ---------------------------------------------------------------- 自律行動の小さな動き（物陰へ寄る・陣地に入る）

        /// <summary>
        /// 自律行動の小さな動きを始める：命令は変えずに、目的地と経路だけ引く。届かなければ動かない（false）。
        /// </summary>
        bool StartAutoMove(Company c, Vec2 goal, AutoMove kind, double t)
        {
            var cells = FindPath(c, goal, out _, allowPartial: false);
            if (cells == null) return false;
            c.Path = PathFinder.ToWaypoints(_map, cells, goal);
            c.PathIndex = 0;
            c.MoveGoal = goal;
            c.AutoMove = kind;
            c.AutoMoveSince = t;
            c.AutoFacing = null;
            return true;
        }

        /// <summary>小さな動きの終わり：着いた、または進めなくなった。陣地に入ったら持ち場はそこ。拘束は今の距離と方角を拘束点にする。</summary>
        void EndAutoMoveIfDone(Company c, double t)
        {
            if (c.AutoMove == AutoMove.None) return;
            bool done = c.Path == null || c.PathIndex >= c.Path.Count;
            if (!done && (c.MovedThisTick || t <= c.AutoMoveSince + 1e-9)) return;
            var kind = c.AutoMove;
            c.AutoMove = AutoMove.None;
            c.Path = null;
            if (kind == AutoMove.EnterPosition) c.PostPoint = c.Position;
            if (c.CurrentOrder.Type == OrderType.Pin)
            {
                var tgt = EnemyOf(c, c.CurrentOrder.TargetId);
                if (tgt != null)
                {
                    var d = c.Position - tgt.Position;
                    c.PinBearing = Math.Atan2(d.Y, d.X);
                    c.PinStandoffM = d.Length;
                }
            }
        }

        // ---------------------------------------------------------------- 開けた所で撃たれている → 物陰へ寄る

        bool UpdateCover(Company c, double t)
        {
            if (c.CoverWaitStart.HasValue)
            {
                if (!c.UnderFireSince.HasValue)
                {
                    c.CoverWaitStart = null;
                    return false;
                }
                if (t - c.CoverWaitStart.Value < _p.Of(c.Training).OrderStartSec - 1e-9) return true;
                c.CoverWaitStart = null;
                if (!StartAutoMove(c, c.CoverGoal, AutoMove.Cover, t)) return false;
                if (c.CoverIndependent) LogIndependent(t, c, "cover", null);
                if (_log.Enabled)
                    _log.Write(Ev(t, "auto_cover", c).Add("side", SideName(c.Side)).Add("independent", c.CoverIndependent)
                        .Add("x", c.CoverGoal.X).Add("y", c.CoverGoal.Y).ToString());
                return true;
            }
            if (!c.UnderFireSince.HasValue || t - c.UnderFireSince.Value < A.CoverUnderFireSec - 1e-9 || t < c.NextCoverSearch - 1e-9)
                return false;
            c.NextCoverSearch = t + A.CoverRetrySec;
            var spot = FindCover(c, out bool independent);
            if (!spot.HasValue) return false;
            c.CoverWaitStart = t;
            c.CoverGoal = spot.Value;
            c.CoverIndependent = independent;
            return true;
        }

        /// <summary>
        /// 物陰を探す：持ち場から 100m（慎重の任せるは 200m）以内のマスの中心で、通れて、被弾倍率が今の 0.7 倍以下。
        /// 慎重の任せる以外は、今の向きから 90° より後ろのマスを除く。今撃ってきている敵の中心から 150m 以内のマス、
        /// 敵の周りの通れないマス、味方から 200m 未満のマスも除く。拘束は目標が見え続けるマスだけ。
        /// 並べ方：今の目標が射程内で見え続ける → 被弾倍率の低い → 今の位置から近い。
        /// </summary>
        public Vec2? FindCover(Company c, out bool independent)
        {
            bool wide = c.Personality == Personality.Cautious && c.Discretion == Discretion.Delegate;
            double radius = wide ? A.CautiousCoverRadiusM : A.PostRadiusM;
            double cur = TerrainDamageMult(c);
            var shooters = new List<Vec2>();
            foreach (var f in _fireLog)
                if (f.Target == c.Id) shooters.Add(_companies[f.Shooter].Position);
            bool pin = c.CurrentOrder.Type == OrderType.Pin;
            var tgt = pin ? EnemyOf(c, c.CurrentOrder.TargetId) : (c.TargetId >= 0 ? EnemyOf(c, c.TargetId) : null);
            var enemy = EnemyCells(c.Side);

            Vec2? best = null;
            bool bestSees = false, bestRear = false;
            double bestMult = double.MaxValue, bestDist = double.MaxValue;
            Visibility.ForCellsWithin(_map, c.PostPoint, radius, (i, j, center) =>
            {
                if (!_map.Passable(center) || enemy[j * _map.Width + i]) return;
                double m = _map.DamageMult(_map.TerrainAt(i, j));
                if (m > A.CoverMultRatio * cur + 1e-9) return;
                var d = center - c.Position;
                double dist = d.Length;
                bool rear = dist > 1e-6 && Math.Abs(Angles.Wrap(Math.Atan2(d.Y, d.X) - c.Facing)) > Math.PI / 2 + 1e-9;
                if (rear && !wide) return;
                foreach (var s in shooters)
                    if (Vec2.Distance(center, s) <= A.CoverMinShooterDistM) return;
                if (!DestinationOk(c, center)) return;
                bool sees = tgt != null && CanShootFrom(c, center, tgt);
                if (pin && !sees) return;
                bool better = !best.HasValue
                              || (sees && !bestSees)
                              || (sees == bestSees && (m < bestMult - 1e-9 || (Math.Abs(m - bestMult) <= 1e-9 && dist < bestDist - 1e-9)));
                if (!better) return;
                best = center;
                bestSees = sees;
                bestRear = rear;
                bestMult = m;
                bestDist = dist;
            });
            independent = best.HasValue && wide && (bestRear || Vec2.Distance(best.Value, c.PostPoint) > A.PostRadiusM + 1e-9);
            return best;
        }

        /// <summary>
        /// 物陰の候補の点 <paramref name="p"/> から目標を撃てるか（P2c.1.2）：射程内で、視線が通るか、今の向きで置いた矩形が接触の距離。
        /// なお、接触の距離の点は経路で敵を避けるマス（敵の矩形から約 113m）に入るので、物陰の候補からは先に外れる。
        /// </summary>
        public bool CanShootFrom(Company c, Vec2 p, Company tgt) =>
            EngageableFrom(p, tgt)
            || (Vec2.Distance(p, tgt.Position) <= _p.Fire.RangeMaxM && InContactAt(p, c.Facing, RectOf(tgt)));

        // ---------------------------------------------------------------- 止まって静か → 浅い掩体

        /// <summary>浅い掩体を掘れる地面（開けた所：平地・砲撃痕）。</summary>
        static bool IsScrapeGround(char ch) => ch == TerrainMap.Plain || ch == TerrainMap.Crater;

        /// <summary>
        /// 浅い掩体：止まっている中隊が 120秒動かず 60秒撃たれていなければ掘り始め、300秒で被弾 ×0.6。掘り始めたら撃たれても続ける。
        /// 陣地構築の命令中・進捗があるときは掘らない（重ねない）。動くと失う（<see cref="TrackFireAndStillness"/>）。
        /// </summary>
        void UpdateScrape(Company c, double t, bool settled)
        {
            if (c.CurrentOrder.Type == OrderType.Entrench || c.EntrenchProgress > 0)
            {
                c.ScrapeProgress = 0;
                return;
            }
            if (!settled || c.MovedThisTick || c.ScrapeProgress >= A.ScrapeDigSec) return;
            if (!IsScrapeGround(_map.TerrainAt(c.Position))) return;
            if (t - c.StillSince < A.ScrapeStillSec - 1e-9) return;
            if (c.ScrapeProgress <= 0 && t - c.LastTargetedTime < A.ScrapeQuietSec - 1e-9) return;
            // 占領の乱れの間は、守りの用意を始めない（C12）
            if (c.ScrapeProgress <= 0 && InOccupationDisarray(c)) return;
            // 浅い掩体の向きは、掘り始めたときの中隊の向き（R2）
            if (c.ScrapeProgress <= 0) c.ScrapeFacing = c.Facing;
            if (c.ScrapeProgress <= 0 && _log.Enabled) _log.Write(Ev(t, "scrape_start", c).Add("side", SideName(c.Side)).ToString());
            c.ScrapeProgress = Math.Min(A.ScrapeDigSec, c.ScrapeProgress + _p.TickSec);
            if (c.ScrapeProgress >= A.ScrapeDigSec && _log.Enabled)
                _log.Write(Ev(t, "scrape_done", c).Add("side", SideName(c.Side)).ToString());
        }

        /// <summary>
        /// 浅い掩体の被弾倍率（1.0 から進捗に応じて 0.6 へ）。掘れる地面で止まっているときだけ。掘り始めたときの向きの正面の弧の
        /// 外から撃たれたとき（<paramref name="inArc"/> が false。R2）は、0.6 の代わりに terrain.fortification_off_arc_mult へ近づく。
        /// </summary>
        double ScrapeMult(Company c, char ch, bool inArc)
        {
            if (c.MovedThisTick || c.ScrapeProgress <= 0 || !IsScrapeGround(ch)) return 1.0;
            double target = inArc ? A.ScrapeDamageMult : _p.Terrain.FortificationOffArcMult;
            return 1.0 + (target - 1.0) * Math.Min(1.0, c.ScrapeProgress / A.ScrapeDigSec);
        }

        // ---------------------------------------------------------------- 撃てる目標がない → 危ない敵の方を向く（P2c.1）

        /// <summary>
        /// 撃てる目標がないときの向き（P2c.1）。向く相手は上から順に、最初に当てはまったもの：
        /// 直近 30秒に自分を撃った指揮下の敵（一番多く撃った敵）、自分から視線の通る 400m 以内の最も近い指揮下の敵、
        /// 同じく 600m 以内の最も近い敵。いなければ今の向きのまま。
        /// 今の正面の弧（±60°）の 400m 以内に自分から見える指揮下の敵がすでにいれば、向き直らない。
        /// 撃てる目標ができたら自律の向きを解き、命令の向きに戻す（その後は射撃の目標選びと P1d の向き直りに任せる）。
        /// 向き直り（P1d の5）があればそちらを先にする。
        /// </summary>
        void UpdateAutoFacing(Company c, double t)
        {
            if (c.FacingOverride.HasValue) return;
            if (c.TargetId >= 0)
            {
                c.AutoFacing = null;
                return;
            }
            if (FrontArcHasVisibleEnemy(c, A.FaceFrontHoldM)) return;
            var e = RecentShooter(c, t) ?? NearestVisibleEnemy(c, A.FaceNearRadiusM) ?? NearestVisibleEnemy(c, A.FaceEnemyRadiusM);
            if (e == null) return;
            var v = e.Position - c.Position;
            if (v.Length > 1e-9) c.AutoFacing = Math.Atan2(v.Y, v.X);
        }

        /// <summary>今の正面の弧（±60°）の <paramref name="radius"/> 以内に、気付いている指揮下の敵がいるか（視線が通るか接触の距離。P2c.1.2）。</summary>
        bool FrontArcHasVisibleEnemy(Company c, double radius)
        {
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || !e.UnderCommand) continue;
                if (Vec2.Distance(c.Position, e.Position) > radius) continue;
                if (AspectOf(c, e.Position) != Aspect.Front) continue;
                if (Aware(c, e)) return true;
            }
            return false;
        }

        /// <summary>気付いている（視線が通るか接触の距離。P2c.1.2）、<paramref name="radius"/> 以内の最も近い指揮下の敵（同じ距離なら ID の小さい方）。</summary>
        Company NearestVisibleEnemy(Company c, double radius)
        {
            Company best = null;
            double bd = radius;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || !e.UnderCommand) continue;
                double d = Vec2.Distance(c.Position, e.Position);
                if (d > bd || (best != null && d >= bd)) continue;
                if (!Aware(c, e)) continue;
                bd = d;
                best = e;
            }
            return best;
        }

        /// <summary>
        /// 直近 30秒に自分を撃った、今も指揮下の敵のうち、撃った刻みの数が一番多いもの（同じなら最後に撃った方、それも同じなら ID の小さい方）。
        /// </summary>
        Company RecentShooter(Company c, double t)
        {
            Company best = null;
            int bestN = 0;
            double bestLast = double.NegativeInfinity;
            foreach (var kv in c.ShotBy)
            {
                var q = kv.Value;
                while (q.Count > 0 && t - q.Peek() > A.FaceShooterMemorySec + 1e-9) q.Dequeue();
                if (q.Count == 0) continue;
                var e = _companies[kv.Key];
                if (!e.OnBoard || !e.UnderCommand) continue;
                double last = 0;
                foreach (double x in q) last = x;
                bool better = best == null || q.Count > bestN
                              || (q.Count == bestN && (last > bestLast || (last == bestLast && e.Id < best.Id)));
                if (!better) continue;
                best = e;
                bestN = q.Count;
                bestLast = last;
            }
            return best;
        }

        /// <summary>この刻みに自分を撃った敵を覚える（向く相手の順位に使う。30秒より古いものは捨てる）。</summary>
        void RecordShotsAt(double t)
        {
            foreach (var f in _fireLog)
            {
                var c = _companies[f.Target];
                if (!c.ShotBy.TryGetValue(f.Shooter, out var q)) c.ShotBy[f.Shooter] = q = new Queue<double>();
                q.Enqueue(t);
                while (q.Count > 0 && t - q.Peek() > A.FaceShooterMemorySec + 1e-9) q.Dequeue();
            }
        }

        // ---------------------------------------------------------------- 機敏：味方に正面から撃たれている敵の側面へ

        /// <summary>
        /// 任せるの機敏が、止まっていて自分の撃てる目標がないとき、5秒ごとに見回る。500m 以内で、別の味方に正面から撃たれている
        /// 指揮下の敵を近い順に調べ、側面の範囲の発起点までの経路が 600m 以内で、発起点が持ち場から 600m 以内なら、
        /// 練度の遅れの後にその敵を側面から攻める（独断）。
        /// </summary>
        bool UpdateAgile(Company c, double t)
        {
            if (c.Personality != Personality.Agile || c.Discretion != Discretion.Delegate || c.State != CompanyState.Normal
                || c.TargetId >= 0 || t < c.NextAgileCheck - 1e-9 || RefusesAdvance(c))
                return false;
            c.NextAgileCheck = t + A.AgileCheckSec;

            var cands = new List<(double d, Company e)>();
            foreach (var f in _fireLog)
            {
                if (f.Aspect != Aspect.Front) continue;
                var sh = _companies[f.Shooter];
                if (sh == c || sh.Side != c.Side) continue;
                var e = _companies[f.Target];
                if (!e.OnBoard || !e.UnderCommand || e.Side == c.Side) continue;
                double d = Vec2.Distance(c.Position, e.Position);
                if (d > A.AgileWatchRadiusM) continue;
                bool dup = false;
                foreach (var x in cands)
                    if (x.e == e) { dup = true; break; }
                if (!dup) cands.Add((d, e));
            }
            cands.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.e.Id.CompareTo(b.e.Id));
            foreach (var (_, e) in cands)
            {
                double bearing = Cheapest(c, e, RangeCandidates(e, AttackAspect.Flank, BearingFrom(e, c.Position)), null, out double cost);
                if (double.IsInfinity(cost)) continue;
                var goal = AdjustDestination(c, JumpOff(e, bearing));
                if (Vec2.Distance(goal, c.PostPoint) > A.AgilePostLimitM + 1e-9) continue;
                var cells = FindPath(c, goal, out _, allowPartial: false);
                if (cells == null) continue;
                if (PathLength(c.Position, PathFinder.ToWaypoints(_map, cells, goal)) > A.AgilePathMaxM + 1e-9) continue;
                c.IndependentWaitOrder = Order.Attack(c.Id, e.Id, AttackAspect.Flank, AttackOnBreak.Secure);
                c.IndependentWaitAt = t + _p.Of(c.Training).OrderStartSec;
                c.IndependentOrigin = c.PostPoint;
                c.AutoFacing = null;
                LogIndependent(t, c, "flank", e);
                return true;
            }
            return false;
        }

        static double PathLength(Vec2 from, List<Vec2> points)
        {
            double len = 0;
            var a = from;
            foreach (var p in points)
            {
                len += Vec2.Distance(a, p);
                a = p;
            }
            return len;
        }

        /// <summary>機敏の回り込みの練度の遅れ。目標が指揮下でなくなった、拒む士気になったら取りやめ。</summary>
        void StepIndependentWait(Company c, double t)
        {
            var o = c.IndependentWaitOrder;
            var tgt = EnemyOf(c, o.TargetId);
            if (tgt == null || !tgt.UnderCommand || c.State != CompanyState.Normal || RefusesAdvance(c))
            {
                c.IndependentWaitOrder = null;
                return;
            }
            if (t < c.IndependentWaitAt - 1e-9) return;
            c.IndependentWaitOrder = null;
            LatchStageComplete(c);
            c.Independent = IndependentAct.Flank;
            StartOrder(c, o, t, t);
        }

        // ---------------------------------------------------------------- 猪突：追撃の後に続けて攻めかかる

        /// <summary>猪突の独断の限度（持ち場から 900m。追撃の限界 600m × 猪突の倍率 1.5）。</summary>
        double RecklessLimitM => B.PursuitLimitM * B.PursuitLimitMult[Personality.Reckless];

        /// <summary>
        /// 追撃を終えた猪突（任せる）：持ち場から 900m に達していれば止まって命令待ち（独断のとき）。そうでなければ、
        /// 400m 以内の動揺・壊走中の敵で最も近いものへ、内部の攻撃の命令（崩れたら：追撃）で続けて攻めかかる。
        /// 扱ったら true（確保はしない）。
        /// </summary>
        bool TryRecklessFollowUp(Company c, Company from, string stop, double t, double tickStart)
        {
            if (c.Personality != Personality.Reckless || c.Discretion != Discretion.Delegate) return false;
            var origin = c.PursuitStart;
            double limit = RecklessLimitM;
            bool wasIndependent = c.Independent != IndependentAct.None;
            if (stop == "limit" || Vec2.Distance(c.Position, origin) >= limit - 1e-9)
            {
                if (!wasIndependent) return false;
                StopIndependent(c, from, "limit", t);
                return true;
            }
            if (RefusesAdvance(c) || c.StartWaitOrder != null || NextStageOnComplete(c)) return false;

            Company best = null;
            double bd = A.RecklessFollowupRadiusM;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || e == from) continue;
                if (e.State != CompanyState.Shaken && e.State != CompanyState.Routed) continue;
                double d = Vec2.Distance(c.Position, e.Position);
                if (d <= bd && (best == null || d < bd))
                {
                    bd = d;
                    best = e;
                }
            }
            if (best == null) return false;

            var order = Order.Attack(c.Id, best.Id, AttackAspect.None, AttackOnBreak.Pursue);
            c.Independent = IndependentAct.Attack;
            c.IndependentOrigin = origin;
            LogIndependent(t, c, "attack", best);
            if (best.State == CompanyState.Routed)
            {
                // 壊走中の敵には、そのまま追撃として追う
                c.CurrentOrder = order;
                c.Arrived = true;
                c.Path = null;
                c.AttackFixed = false;
                c.FacingOverride = null;
                c.TurnWaitStart = null;
                c.SeekingSight = false;
                c.Pursuing = true;
                c.PursuitTargetId = best.Id;
                c.PursuitStart = origin;
                c.PursuitLimitM = limit;
                c.NextPursuitPlan = double.NegativeInfinity;
            }
            else
            {
                StartOrder(c, order, t, tickStart);
            }
            return true;
        }

        /// <summary>独断を限度で止める：その場で保持して命令待ち。</summary>
        void StopIndependent(Company c, Company tgt, string reason, double t)
        {
            c.BrokenTargetPos = null;
            Secure(c, tgt, enter: false);
            c.IndependentStopped = true;
            if (_log.Enabled)
                _log.Write(Ev(t, "independent_end", c).Add("side", SideName(c.Side)).Add("personality", Names.Of(c.Personality))
                    .Add("reason", reason).ToString());
        }

        /// <summary>今の段が「完了したら」で次の段へ移る（独断せずに次の段を待つ）。</summary>
        bool NextStageOnComplete(Company c)
        {
            var sh = c.ActiveSheet;
            return sh != null && c.StageIndex + 1 < sh.Stages.Count && sh.Stages[c.StageIndex + 1].Trigger.Kind == TriggerKind.Complete;
        }

        // ---------------------------------------------------------------- 崩した敵の陣地に入る

        /// <summary>
        /// 確保したとき、崩した敵がいた位置（目標が指揮下でなくなった刻みの中心）が塹壕・村で、200m 以内なら、そこへ入って固める。
        /// </summary>
        void TryEnterPosition(Company c, double t)
        {
            if (!c.BrokenTargetPos.HasValue) return;
            var p = c.BrokenTargetPos.Value;
            c.BrokenTargetPos = null;
            char ch = _map.TerrainAt(p);
            if (ch != TerrainMap.Village && ch != TerrainMap.Trench) return;
            double d = Vec2.Distance(c.Position, p);
            if (d > A.EnterPositionRadiusM + 1e-9 || d < 1.0) return;
            var goal = AdjustDestination(c, p);
            char gch = _map.TerrainAt(goal);
            if ((gch != TerrainMap.Village && gch != TerrainMap.Trench) || Vec2.Distance(goal, c.Position) < 1.0) return;
            StartEnterPosition(c, goal, t);
        }

        /// <summary>
        /// 陣地へ動き出す。経路が引けなければ（崩れた敵がまだ近くにいて、敵の周りのマスが通れない等）、敵の周りのマスを作り直す
        /// 間隔（10秒）ごとに引き直す。新しい命令が来たらやめる。
        /// </summary>
        void StartEnterPosition(Company c, Vec2 goal, double t)
        {
            c.EnterGoal = null;
            if (!StartAutoMove(c, goal, AutoMove.EnterPosition, t))
            {
                c.EnterGoal = goal;
                c.NextEnterTry = t + _p.MapRules.ExposureRebuildSec;
                return;
            }
            if (_log.Enabled)
                _log.Write(Ev(t, "enter_position", c).Add("side", SideName(c.Side)).Add("x", goal.X).Add("y", goal.Y).ToString());
        }

        // ---------------------------------------------------------------- 宣言

        /// <summary>独断の宣言（出来事の欄に出す）。kind：pursue／attack／flank／cover／no_pursuit。</summary>
        void LogIndependent(double t, Company c, string kind, Company target)
        {
            // 宣言は中隊の伝令で本部へ送る（P2c.2）
            NoteDispatch(c, DispatchKind.Declaration, t, target, kind);
            if (!_log.Enabled) return;
            var ev = Ev(t, "independent", c).Add("side", SideName(c.Side)).Add("personality", Names.Of(c.Personality)).Add("kind", kind);
            if (target != null) ev.Add("target", target.Name).Add("target_state", Names.Of(target.State));
            _log.Write(ev.ToString());
        }
    }
}
