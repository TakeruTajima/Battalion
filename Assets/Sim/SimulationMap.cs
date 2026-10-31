using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>見える範囲の計算（露出の地図と、退路の見積もりの視線に使う）。</summary>
    public static class Visibility
    {
        /// <summary>
        /// 観測者から、中心が <paramref name="radius"/> 以内にある各マスの中心が見えるか（添字 <c>j * Width + i</c>）。
        /// 範囲外のマスは false。
        /// </summary>
        public static bool[] VisibleCells(TerrainMap m, SimParams.SightParams sp, Vec2 observer, double radius)
        {
            var v = new bool[m.Width * m.Height];
            ForCellsWithin(m, observer, radius, (i, j, center) =>
            {
                if (LineOfSight.Visible(m, sp, observer, center)) v[j * m.Width + i] = true;
            });
            return v;
        }

        /// <summary>
        /// 露出の地図：マスの中心が、<paramref name="range"/> 以内のいずれかの観測者（敵）から見えていれば true。
        /// map_reference.py の exposure_map と同じ（視線は敵の中心からマスの中心へ引く）。
        /// </summary>
        public static bool[] Exposure(TerrainMap m, SimParams.SightParams sp, IEnumerable<Vec2> observers, double range)
        {
            var exp = new bool[m.Width * m.Height];
            foreach (var o in observers)
            {
                ForCellsWithin(m, o, range, (i, j, center) =>
                {
                    int k = j * m.Width + i;
                    if (!exp[k] && LineOfSight.Visible(m, sp, o, center)) exp[k] = true;
                });
            }
            return exp;
        }

        /// <summary>中心が <paramref name="p"/> から <paramref name="radius"/> 以内にあるマスを順に渡す。</summary>
        public static void ForCellsWithin(TerrainMap m, Vec2 p, double radius, Action<int, int, Vec2> f)
        {
            int i0 = Math.Max(0, (int)Math.Floor((p.X - radius - m.OriginX) / m.CellM));
            int i1 = Math.Min(m.Width - 1, (int)Math.Floor((p.X + radius - m.OriginX) / m.CellM));
            int j0 = Math.Max(0, (int)Math.Floor((p.Y - radius - m.OriginY) / m.CellM));
            int j1 = Math.Min(m.Height - 1, (int)Math.Floor((p.Y + radius - m.OriginY) / m.CellM));
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    var c = m.CellCenter(i, j);
                    if (Vec2.Distance(p, c) <= radius) f(i, j, c);
                }
            }
        }
    }

    // 地図まわり：命令の挙動、経路、重なりの禁止、陣地構築、地形の効果、見える範囲
    public sealed partial class Simulation
    {
        sealed class SightGrid
        {
            public Vec2 From;
            public bool[] Cells;
        }

        // 中隊ごとの見える範囲と、陣営ごとの露出の地図（10秒ごとに作り直す）
        readonly Dictionary<int, SightGrid> _sight = new Dictionary<int, SightGrid>();
        readonly bool[][] _exposure = new bool[2][];

        double SightRadius => _p.Fire.RangeMaxM + _map.CellM * Math.Sqrt(2) / 2;

        // ---------------------------------------------------------------- 見える範囲と露出の地図

        void RebuildVisibility()
        {
            _sight.Clear();
            _exposure[0] = _exposure[1] = null;
            _enemyCells[0] = _enemyCells[1] = null;
            foreach (var c in _companies)
                if (IsShooter(c)) _sight[c.Id] = BuildSight(c);
        }

        static bool IsShooter(Company c) => c.OnBoard && c.UnderCommand && c.Strength > 0;

        SightGrid BuildSight(Company c) =>
            new SightGrid { From = c.Position, Cells = Visibility.VisibleCells(_map, _p.Sight, c.Position, SightRadius) };

        SightGrid SightOf(Company c)
        {
            // 作り直しの後に指揮下へ戻った中隊などは、その場で作る（次の作り直しまで使う）。下見では使い回しを作らない
            if (_sight.TryGetValue(c.Id, out var g)) return g;
            g = BuildSight(c);
            if (!_previewing) _sight[c.Id] = g;
            return g;
        }

        /// <summary>
        /// 中隊 <paramref name="e"/> から、中心が射程（＋マスの半対角）以内の各マスの中心が見えるか。
        /// 10秒ごとに、その時点の位置で作り直す。退路の見積もりの視線に使う。
        /// </summary>
        public bool[] VisibleCellsFrom(Company e) => SightOf(e).Cells;

        /// <summary>
        /// <paramref name="side"/> の中隊から見た露出の地図：マスの中心が、400m 以内にいる指揮下の敵のいずれかから見えていれば true。
        /// </summary>
        public bool[] ExposureFor(Side side)
        {
            int s = (int)side;
            if (_exposure[s] != null) return _exposure[s];
            var exp = BuildExposure(side, SightOf);
            if (!_previewing) _exposure[s] = exp;
            return exp;
        }

        bool[] BuildExposure(Side side, Func<Company, SightGrid> sightOf)
        {
            var exp = new bool[_map.Width * _map.Height];
            foreach (var e in _companies)
            {
                if (e.Side == side || !IsShooter(e)) continue;
                var g = sightOf(e);
                // 敵の矩形から接触の距離（50m 以内）のマスは、視線を問わず射界に数える（P2c.1.2）
                var rect = RectAt(g.From, e.Facing);
                double contact = B.SightContactGapM;
                Visibility.ForCellsWithin(_map, g.From, _p.Fire.RangeMaxM, (i, j, center) =>
                {
                    int k = j * _map.Width + i;
                    if (g.Cells[k] || rect.DistanceTo(center) <= contact) exp[k] = true;
                });
            }
            return exp;
        }

        // ---------------------------------------------------------------- 表示用の読み取り専用の口

        /// <summary>1回の射撃の記録（表示用）。</summary>
        public readonly struct FireRecord
        {
            public readonly int Shooter;
            public readonly int Target;
            /// <summary>撃たれた側から見た射手の向き。</summary>
            public readonly Aspect Aspect;
            /// <summary>損害の期待値（1分あたり、その刻みの条件で）。</summary>
            public readonly double ExpectedPerMin;
            /// <summary>この刻みの実際の損害。</summary>
            public readonly int Casualties;
            /// <summary>この射撃の損害による士気の減り（練度の減りやすさを掛ける前）。</summary>
            public readonly double MoraleHit;
            public readonly double Distance;

            public FireRecord(int shooter, int target, Aspect aspect, double expectedPerMin, int casualties, double moraleHit, double distance)
            {
                Shooter = shooter;
                Target = target;
                Aspect = aspect;
                ExpectedPerMin = expectedPerMin;
                Casualties = casualties;
                MoraleHit = moraleHit;
                Distance = distance;
            }
        }

        readonly List<FireRecord> _fireLog = new List<FireRecord>();

        /// <summary>直前の刻みの射撃の記録（表示用。読むだけ）。次の刻みで書き換わる。</summary>
        public IReadOnlyList<FireRecord> LastTickFire => _fireLog;
        // Sim の中の「見える範囲」の使い回し（10秒ごとに作り直す）に触らない。表示から何度呼んでも会戦の結果は変わらない。

        /// <summary>使い回し中の見える範囲があればそれを、なければその場で作ったもの（保存しない）を返す。</summary>
        SightGrid PeekSight(Company c) => _sight.TryGetValue(c.Id, out var g) ? g : BuildSight(c);

        /// <summary>
        /// 露出の地図を、Sim の使い回しに触らずに返す（表示用）。<paramref name="side"/> の中隊から見て、敵の射界にあるマスが true。
        /// 返す配列は書き換えないこと。
        /// </summary>
        public bool[] PeekExposure(Side side)
        {
            int s = (int)side;
            return _exposure[s] ?? BuildExposure(side, PeekSight);
        }

        /// <summary>退路の見積もりを、Sim の使い回しに触らずに行う（表示用）。</summary>
        public EscapeEstimate PeekEscape(Company c) => EstimateEscape(c, e => PeekSight(e).Cells);

        // ---------------------------------------------------------------- 命令の開始

        void StartOrder(Company c, Order order, double t, double tickStart)
        {
            c.CurrentOrder = order;
            c.Arrived = !order.Moves;
            c.Path = null;
            c.PathIndex = 0;
            c.AttackChosenAspect = AttackAspect.None;
            c.AttackFixed = false;
            c.Pursuing = false;
            c.PursuitTargetId = -1;
            // 向き直った向きは新しい命令が出るまで（P1d の5）
            c.FacingOverride = null;
            c.TurnWaitStart = null;
            c.SeekingSight = false;
            c.PinStandoffM = null;

            switch (order.Type)
            {
                case OrderType.Advance:
                case OrderType.Retreat:
                    c.MoveGoal = AdjustDestination(c, order.Destination.Value);
                    Plan(c, tickStart);
                    break;
                case OrderType.Entrench:
                    if (order.Destination.HasValue)
                    {
                        c.MoveGoal = AdjustDestination(c, order.Destination.Value);
                        Plan(c, tickStart);
                    }
                    break;
                case OrderType.Attack:
                {
                    var tgt = EnemyOf(c, order.TargetId);
                    if (tgt == null || !tgt.UnderCommand)
                    {
                        // 目標がもう指揮下でない：攻撃は始めた時点で完了（P2b の「完了したら」）。独断の攻撃は段の完了に数えない（P2c）
                        HoldInPlace(c);
                        if (c.Independent == IndependentAct.None) c.StageCompleted = true;
                        break;
                    }
                    ChooseAttackBearing(c, tgt, order, true);
                    c.MoveGoal = AdjustDestination(c, JumpOff(tgt, c.AttackBearing));
                    Plan(c, tickStart);
                    if (_log.Enabled && !_previewing)
                        _log.Write(Ev(t, "attack_plan", c)
                            .Add("target", tgt.Name)
                            .Add("aspect", AspectName(c.AttackChosenAspect))
                            .Add("bearing", Math.Round(Angles.Wrap(c.AttackBearing) * Angles.RadToDeg, 1))
                            .Add("x", c.MoveGoal.X)
                            .Add("y", c.MoveGoal.Y)
                            .ToString());
                    break;
                }
                case OrderType.Pin:
                {
                    var tgt = EnemyOf(c, order.TargetId);
                    if (tgt == null) HoldInPlace(c);
                    else StartPin(c, tgt, tickStart);
                    break;
                }
            }
        }

        /// <summary>台本のイベントをログに残す（予備隊の逆襲など）。</summary>
        public void LogScriptEvent(double t, string type, Company unit, Company target)
        {
            if (_log.Enabled) _log.Write(Ev(t, type, unit).Add("target", target.Name).ToString());
        }

        static void HoldInPlace(Company c)
        {
            c.CurrentOrder = Order.Hold(c.Id);
            c.Arrived = true;
            c.Path = null;
        }

        Company EnemyOf(Company c, int id)
        {
            if (id < 0 || id >= _companies.Count) return null;
            var e = _companies[id];
            return e.OnBoard && e.Side != c.Side ? e : null;
        }

        static string AspectName(AttackAspect a)
        {
            switch (a)
            {
                case AttackAspect.Front: return "front";
                case AttackAspect.Flank: return "flank";
                case AttackAspect.Rear: return "rear";
                default: return "none";
            }
        }

        double ThreatWeightOf(Company c) => c.ThreatWeightOverride ?? _p.MapRules.ThreatWeight(c.Personality);

        /// <summary>今の位置から <see cref="Company.MoveGoal"/> への経路を引く。次の引き直しは30秒後。</summary>
        void Plan(Company c, double tickStart)
        {
            c.NextReplanTime = tickStart + _p.MapRules.ReplanSec;
            var cells = FindPath(c, c.MoveGoal, out _);
            if (cells == null)
            {
                // 届かない（川の向こうで橋も浅瀬も使えない等）。その場で止まる
                c.Path = null;
                c.Arrived = true;
                return;
            }
            c.Path = PathFinder.ToWaypoints(_map, cells, c.MoveGoal);
            c.PathIndex = 0;
        }

        /// <summary>
        /// 経路を引く。<paramref name="allowPartial"/> なら、届かないときは届くところまでの経路を返す（移動に使う）。
        /// 経路コストを比べるとき（攻撃の側を選ぶ等）は、届かない経路を安く見せないように false にする。
        /// </summary>
        List<PathFinder.Cell> FindPath(Company c, Vec2 goal, out double cost, bool allowPartial = true)
        {
            _map.CellOf(c.Position, out int si, out int sj);
            _map.CellOf(goal, out int gi, out int gj);
            var start = new PathFinder.Cell(si, sj);
            var end = new PathFinder.Cell(gi, gj);
            var exposure = ExposureFor(c.Side);
            double w = ThreatWeightOf(c);
            // 敵の矩形の周りのマスは通らない（P1d の2）。止まっている味方の周り（中心から 200m 未満のマス）は、通れるが
            // 距離のコストを ×3 にする（隊列の超越。P1e の1）。届かなければ、届くところまでの経路を引く
            double ec = _p.MapRules.ExposureCostM;
            var enemy = EnemyCells(c.Side);
            var friend = StationaryFriendZone(c);
            return PathFinder.Find(_map, start, end, exposure, w, ec, out cost, enemy, distanceMult: friend)
                   ?? (allowPartial ? PathFinder.Find(_map, start, end, exposure, w, ec, out cost, enemy, true, friend) : null);
        }

        /// <summary>
        /// 止まっている指揮下の味方か（移動の命令がないか、着いているか、制圧下で伏せて止まっている。R2.1）。
        /// 動揺で停止中（P2b.2）は、命令が保持に替わって着いた扱いなので、もとから止まっている味方に入る。
        /// </summary>
        bool IsStationaryFriend(Company f, Company c) =>
            f != c && f.Side == c.Side && f.OnBoard && f.UnderCommand && (!f.CurrentOrder.Moves || f.Arrived || PinnedDown(f));

        /// <summary>
        /// 止まっている指揮下の味方の中心から 200m 未満のマスの、距離のコストの倍率（×3。それ以外は 1）。
        /// 該当がなければ null。
        /// </summary>
        double[] StationaryFriendZone(Company c)
        {
            double[] zone = null;
            double min = _p.MapRules.AvoidStationaryFriendM;
            double mult = _p.MapRules.StationaryFriendPathCostMult;
            foreach (var f in _companies)
            {
                if (!IsStationaryFriend(f, c)) continue;
                if (zone == null)
                {
                    zone = new double[_map.Width * _map.Height];
                    for (int k = 0; k < zone.Length; k++) zone[k] = 1.0;
                }
                Visibility.ForCellsWithin(_map, f.Position, min, (i, j, center) =>
                {
                    if (Vec2.Distance(center, f.Position) < min) zone[j * _map.Width + i] = mult;
                });
            }
            return zone;
        }

        double PathCost(Company c, Vec2 rawGoal)
        {
            var cells = FindPath(c, AdjustDestination(c, rawGoal), out double cost, allowPartial: false);
            return cells == null ? double.PositiveInfinity : cost;
        }

        // ---------------------------------------------------------------- 攻撃

        /// <summary>攻撃の発起点：目標の中心から、地図の方角 <paramref name="bearingRad"/> へ 250m の点（P1e の4）。</summary>
        public Vec2 JumpOff(Company tgt, double bearingRad) => tgt.Position + Vec2.FromAngle(bearingRad) * _p.MapRules.AttackStandoffM;

        /// <summary>目標の中心から見た、<paramref name="p"/> の方角（同じ点なら目標の向き）。</summary>
        public static double BearingFrom(Company tgt, Vec2 p)
        {
            var d = p - tgt.Position;
            return d.Length < 1e-9 ? tgt.Facing : Math.Atan2(d.Y, d.X);
        }

        /// <summary>
        /// 方角 <paramref name="bearingRad"/> から攻めたときの向き（目標の向きとの差が ±60° 以内は正面、120° 以内は側面、それ以外は背面）。
        /// </summary>
        public AttackAspect AspectOfBearing(Company tgt, double bearingRad)
        {
            double a = Math.Abs(Angles.Wrap(bearingRad - tgt.Facing)) * Angles.RadToDeg;
            if (a <= _p.Fire.FrontArcDeg + 1e-9) return AttackAspect.Front;
            if (a <= _p.Fire.FlankArcDeg + 1e-9) return AttackAspect.Flank;
            return AttackAspect.Rear;
        }

        /// <summary>
        /// 範囲での指定の候補の方角。今の方角 <paramref name="current"/> が範囲に入っていればそれだけ。
        /// 入っていなければ、範囲の端から 10° 内側の方角のうち、今の方角に最も近いもの（同じ近さなら複数）。
        /// </summary>
        List<double> RangeCandidates(Company tgt, AttackAspect range, double current)
        {
            var list = new List<double>();
            if (AspectOfBearing(tgt, current) == range)
            {
                list.Add(current);
                return list;
            }
            double front = _p.Fire.FrontArcDeg, flank = _p.Fire.FlankArcDeg, inset = B.AttackRangeInsetDeg;
            double[] rel;
            switch (range)
            {
                case AttackAspect.Front: rel = new[] { front - inset, -(front - inset) }; break;
                case AttackAspect.Rear: rel = new[] { flank + inset, -(flank + inset) }; break;
                default: rel = new[] { front + inset, flank - inset, -(front + inset), -(flank - inset) }; break;
            }
            double best = double.MaxValue;
            foreach (double r in rel)
            {
                double b = Angles.Wrap(tgt.Facing + r * Angles.DegToRad);
                double d = Math.Abs(Angles.Wrap(b - current));
                if (d < best - 1e-9)
                {
                    best = d;
                    list.Clear();
                    list.Add(b);
                }
                else if (Math.Abs(d - best) <= 1e-9) list.Add(b);
            }
            return list;
        }

        /// <summary>
        /// 攻撃の方角を選ぶ（P1e の4）。地図の方角の指定はそのまま。範囲の指定は <see cref="RangeCandidates"/> から。
        /// 指定なしは今の方角。機敏は、今より良い向き（正面＜側面＜背面）の範囲の候補のうち、経路コストが今の方角の 1.5 倍以下で
        /// 最も良い向きを選ぶ。候補を経路コストで選ぶときは、前に選んだ方角を、他の候補が 0.8 倍未満のときだけ替える。
        /// 選び直し（<paramref name="first"/> が false）では、前に選んだ方角が今も同じ範囲（指定なしなら同じ向き）に入っていれば保つ。
        /// 近づく途中で今の方角が範囲に入るたびに発起点が範囲の端へ寄っていかないように（端から 10° 内側の意味がなくなる）。
        /// </summary>
        void ChooseAttackBearing(Company c, Company tgt, Order order, bool first)
        {
            double bearing;
            if (order.BearingRad.HasValue)
            {
                bearing = order.BearingRad.Value;
            }
            else
            {
                double current = BearingFrom(tgt, c.Position);
                double? prev = first ? (double?)null : c.AttackBearing;
                if (order.Aspect != AttackAspect.None)
                {
                    bearing = prev.HasValue && AspectOfBearing(tgt, prev.Value) == order.Aspect
                        ? prev.Value
                        : Cheapest(c, tgt, RangeCandidates(tgt, order.Aspect, current), prev, out _);
                }
                else if (prev.HasValue && c.Personality != Personality.Agile)
                {
                    bearing = prev.Value;
                }
                else
                {
                    bearing = current;
                    if (c.Personality == Personality.Agile)
                    {
                        var now = AspectOfBearing(tgt, current);
                        double baseCost = PathCost(c, JumpOff(tgt, current));
                        for (var r = AttackAspect.Rear; r > now; r--)
                        {
                            double b = Cheapest(c, tgt, RangeCandidates(tgt, r, current), prev, out double cost);
                            if (!double.IsInfinity(cost) && cost <= _p.MapRules.AgileFlankCostRatio * baseCost)
                            {
                                bearing = b;
                                break;
                            }
                        }
                        if (prev.HasValue && AspectOfBearing(tgt, prev.Value) == AspectOfBearing(tgt, bearing)) bearing = prev.Value;
                    }
                }
            }
            // 着く前の選び直しで方角が大きく（20° 以上）変わったらログに残す
            if (!first && _log.Enabled && !_previewing && Math.Abs(Angles.Wrap(bearing - c.AttackBearing)) * Angles.RadToDeg >= 20)
                _log.Write(Ev(Time, "attack_bearing_change", c)
                    .Add("target", tgt.Name)
                    .Add("aspect", AspectName(AspectOfBearing(tgt, bearing)))
                    .Add("bearing", Math.Round(Angles.Wrap(bearing) * Angles.RadToDeg, 1))
                    .ToString());
            c.AttackBearing = bearing;
            c.AttackChosenAspect = AspectOfBearing(tgt, bearing);
        }

        static bool SameBearing(double a, double b) => Math.Abs(Angles.Wrap(a - b)) < 0.5 * Angles.DegToRad;

        /// <summary>
        /// 候補の方角のうち、経路コストの最も安いもの（届かない候補は選ばない。候補が1つならそれ）。
        /// 前に選んだ方角 <paramref name="prev"/> が候補にあれば、他の候補がその 0.8 倍未満のときだけ替える（P1d の揺れ止め）。
        /// </summary>
        double Cheapest(Company c, Company tgt, List<double> candidates, double? prev, out double cost)
        {
            if (candidates.Count == 1)
            {
                cost = PathCost(c, JumpOff(tgt, candidates[0]));
                return candidates[0];
            }
            double best = candidates[0];
            cost = double.PositiveInfinity;
            double? prevCost = null;
            double prevB = 0;
            foreach (double b in candidates)
            {
                double k = PathCost(c, JumpOff(tgt, b));
                if (prev.HasValue && SameBearing(b, prev.Value))
                {
                    prevCost = k;
                    prevB = b;
                }
                if (k < cost)
                {
                    cost = k;
                    best = b;
                }
            }
            if (prevCost.HasValue && !double.IsInfinity(prevCost.Value) && !(cost < B.AttackSideSwitchRatio * prevCost.Value))
            {
                cost = prevCost.Value;
                return prevB;
            }
            return best;
        }

        void FaceToward(Company c, Vec2 p, double maxTurn)
        {
            var d = p - c.Position;
            if (d.Length < 1e-9) return;
            c.Facing = TurnToward(c.Facing, Math.Atan2(d.Y, d.X), maxTurn);
        }

        /// <summary>
        /// 優先して撃つ命令の目標：拘束の命令中、攻撃で発起点に着いた後（向き直った後は除く）、追撃中は追っている中隊。
        /// </summary>
        Company OrderTarget(Company c)
        {
            var o = c.CurrentOrder;
            if (c.Pursuing) return EnemyOf(c, c.PursuitTargetId);
            if (o.Type == OrderType.Pin) return EnemyOf(c, o.TargetId);
            if (o.Type == OrderType.Attack && c.Arrived && !c.FacingOverride.HasValue) return EnemyOf(c, o.TargetId);
            return null;
        }

        bool CanFireAt(Company shooter, Company e)
        {
            if (!e.OnBoard || e.Side == shooter.Side || e.Strength <= 0) return false;
            if (Vec2.Distance(shooter.Position, e.Position) > _p.Fire.RangeMaxM) return false;
            // 接触中（矩形の隙間 50m 以内）は視線を問わない（P1g）
            return SightForFire(shooter, e);
        }

        // ---------------------------------------------------------------- 移動

        void MoveUnderCommand(Company c, double t, double tickStart, double dt, double maxTurn)
        {
            // 自律行動の小さな動き（物陰へ寄る・陣地に入る。P2c）：命令は変えずに経路に沿って動く
            if (c.AutoMove != AutoMove.None)
            {
                FollowPath(c, dt, maxTurn);
                return;
            }
            switch (c.CurrentOrder.Type)
            {
                case OrderType.Attack:
                    UpdateAttack(c, t, tickStart, maxTurn);
                    break;
                case OrderType.Pin:
                {
                    var tgt = EnemyOf(c, c.CurrentOrder.TargetId);
                    if (tgt == null)
                    {
                        HoldInPlace(c);
                        break;
                    }
                    UpdatePin(c, tgt, tickStart, maxTurn);
                    if (c.Arrived || c.State == CompanyState.Shaken) return;
                    break;
                }
                case OrderType.Advance:
                case OrderType.Retreat:
                case OrderType.Entrench:
                    if (c.CurrentOrder.Moves && !c.Arrived && tickStart >= c.NextReplanTime)
                    {
                        // 引き直しのたびに、味方の今の状態で目的地をずらし直す（止まった味方の近くを目指し続けないように）
                        c.MoveGoal = AdjustDestination(c, c.CurrentOrder.Destination.Value);
                        Plan(c, tickStart);
                    }
                    break;
            }

            var o = c.CurrentOrder;
            if (!o.Moves || c.Arrived)
            {
                // 向き直った向き（P1d の5）があればそれを、なければ命令の向きを保つ
                // 撃てる目標がないときは最も近い敵の方（P2c）
                if (c.FacingOverride.HasValue) c.Facing = TurnToward(c.Facing, c.FacingOverride.Value, maxTurn);
                else if (c.AutoFacing.HasValue) c.Facing = TurnToward(c.Facing, c.AutoFacing.Value, maxTurn);
                else if (o.FacingRad.HasValue) c.Facing = TurnToward(c.Facing, o.FacingRad.Value, maxTurn);
                return;
            }
            FollowPath(c, dt, maxTurn);
        }

        /// <summary>
        /// 経路に沿って進む（P1e の2）。速度は中心のマスの移動倍率×拘束の×0.5×超越の×0.5（味方の矩形と重なる間）に、動き方の倍率を掛ける。
        /// 接敵中は向きを変えずに動く：進む方角が正面の弧なら前進、横（60〜120°）なら横歩き ×0.5、背後の弧なら後ろ向き ×0.6。
        /// 接敵していなければ、進む方角へ旋回しながら進む（向きとの差が 20° を超える間は ×0.7）。到着したら指定の向きへ旋回する。
        /// </summary>
        void FollowPath(Company c, double dt, double maxTurn)
        {
            var o = c.CurrentOrder;
            while (c.Path != null && c.PathIndex < c.Path.Count && (c.Path[c.PathIndex] - c.Position).Length < 1e-9)
                c.PathIndex++;
            if (c.Path == null || c.PathIndex >= c.Path.Count)
            {
                ArriveAt(c);
                if (o.FacingRad.HasValue) c.Facing = TurnToward(c.Facing, o.FacingRad.Value, maxTurn);
                return;
            }

            var target = c.Path[c.PathIndex];
            var to = target - c.Position;
            double dist = to.Length;

            double mv = _map.MoveMult(_map.TerrainAt(c.Position));
            if (mv <= 0) mv = _map.MoveMult(TerrainMap.Ford); // 通れないマスに押し出された場合の保険
            double speed = _p.Movement.SpeedMps * mv * (c.Pinned ? _p.Pinning.MoveMult : 1.0);
            if (c.PassOverlap) speed *= _p.Movement.PassThroughSpeedMult;
            double heading = Math.Atan2(to.Y, to.X);
            // 阻まれている間に向き直った中隊（P1e.2）は、向き直った向きへ回り、その向きのまま動く（接敵中と同じ扱い）
            if (c.FacingOverride.HasValue) c.Facing = TurnToward(c.Facing, c.FacingOverride.Value, maxTurn);
            // 自律行動の小さな動き（P2c）も向きを変えずに動く（撃ってくる敵に横腹や背を向けない）
            if (c.InContact || c.FacingOverride.HasValue || c.AutoMove != AutoMove.None)
            {
                // 接敵中は向きを変えない（敵に横腹や背を向けない）。背を向けて逃げるのは壊走だけ
                double diff = Math.Abs(Angles.Wrap(heading - c.Facing)) * Angles.RadToDeg;
                if (diff <= _p.Fire.FrontArcDeg + 1e-9)
                {
                    c.MoveStyle = MoveStyle.Forward;
                }
                else if (diff <= _p.Fire.FlankArcDeg + 1e-9)
                {
                    c.MoveStyle = MoveStyle.Sideways;
                    speed *= _p.Movement.SidewaysSpeedMult;
                }
                else
                {
                    c.MoveStyle = MoveStyle.Backward;
                    speed *= _p.Movement.RetreatSpeedMult;
                }
            }
            else
            {
                // 曲がり角で止まらず、旋回しながら進む
                c.Facing = TurnToward(c.Facing, heading, maxTurn);
                if (Math.Abs(Angles.Wrap(heading - c.Facing)) * Angles.RadToDeg > _p.Movement.TurningSlowDeg)
                {
                    c.MoveStyle = MoveStyle.Turning;
                    speed *= _p.Movement.TurningSpeedMult;
                }
                else
                {
                    c.MoveStyle = MoveStyle.Forward;
                }
            }

            double step = speed * dt;
            // 浮動小数点の誤差で残ったごく僅かな距離のために、到着が1刻み遅れないようにする
            if (step >= dist - 1e-9)
            {
                c.Position = target;
                c.PathIndex++;
                if (c.PathIndex >= c.Path.Count) ArriveAt(c);
            }
            else
            {
                c.Position = c.Position + to * (step / dist);
            }
        }

        // ---------------------------------------------------------------- 重なりの禁止

        /// <summary>
        /// 目的地が他の味方（止まっている中隊はその位置、動いている中隊はその目的地）から 200m 未満、または通れない場所なら、
        /// 目的地を中心に 50m 刻みで外へ広げた輪の上（16方位）を調べ、条件を満たす点のうち元の目的地に最も近い点へずらす。
        /// 同じ輪の上に候補が複数あれば、中隊の今の位置に最も近い点（手前の側）を選ぶ。
        /// </summary>
        public Vec2 AdjustDestination(Company c, Vec2 dest)
        {
            if (DestinationOk(c, dest)) return dest;
            for (double r = _p.MapRules.DestinationSearchStepM; r <= _p.MapRules.DestinationSearchMaxM + 1e-9; r += _p.MapRules.DestinationSearchStepM)
            {
                Vec2? best = null;
                double bestD = double.MaxValue;
                for (int k = 0; k < _p.MapRules.DestinationSearchDirs; k++)
                {
                    var p = dest + Vec2.FromAngle(2 * Math.PI * k / _p.MapRules.DestinationSearchDirs) * r;
                    if (!DestinationOk(c, p)) continue;
                    double d = Vec2.Distance(p, c.Position);
                    if (d < bestD) { bestD = d; best = p; }
                }
                if (best.HasValue) return best.Value;
            }
            return dest;
        }

        bool DestinationOk(Company c, Vec2 p)
        {
            if (!_map.Contains(p) || !_map.Passable(p)) return false;
            double min = _p.Movement.FriendlyMinSpacingM;
            foreach (var f in _companies)
            {
                if (f == c || f.Side != c.Side || !f.OnBoard || !f.UnderCommand) continue;
                var at = f.CurrentOrder.Moves && !f.Arrived ? f.MoveGoal : f.Position;
                if (Vec2.Distance(p, at) < min) return false;
            }
            return true;
        }

        /// <summary>
        /// 移動の後、中心どうしが 200m 未満の味方の組を ID の小さい順に1回ずつ調べて押し離す。
        /// 両方が動いていれば半分ずつ、片方だけが動いていれば動いている方だけを押す。止まっている中隊は押さない。
        /// 押した先が通れない場所（地図の外を含む）なら、その中隊は押さない。壊走中・再集結中の中隊は対象外。
        /// </summary>
        void KeepFriendlySpacing()
        {
            double min = _p.Movement.FriendlyMinSpacingM;
            for (int a = 0; a < _companies.Count; a++)
            {
                var ca = _companies[a];
                if (!ca.OnBoard || !ca.UnderCommand) continue;
                for (int b = a + 1; b < _companies.Count; b++)
                {
                    var cb = _companies[b];
                    if (cb.Side != ca.Side || !cb.OnBoard || !cb.UnderCommand) continue;
                    var d = cb.Position - ca.Position;
                    double dist = d.Length;
                    if (dist >= min || (!ca.MovedThisTick && !cb.MovedThisTick)) continue;
                    // 隊列の超越の間は押し離さない（P1e の1）
                    if (ca.Passing.Contains(cb.Id) || cb.Passing.Contains(ca.Id)) continue;
                    var u = dist < 1e-6 ? new Vec2(0, 1) : d * (1.0 / dist);
                    double overlap = min - dist;
                    if (ca.MovedThisTick && cb.MovedThisTick)
                    {
                        TryPush(ca, u * (-overlap / 2));
                        TryPush(cb, u * (overlap / 2));
                    }
                    else if (ca.MovedThisTick)
                    {
                        TryPush(ca, u * -overlap);
                    }
                    else
                    {
                        TryPush(cb, u * overlap);
                    }
                }
            }
        }

        void TryPush(Company c, Vec2 delta)
        {
            var p = c.Position + delta;
            if (!_map.Contains(p) || !_map.Passable(p)) return;
            var before = c.Position;
            c.Position = p;
            // 押し離しでも敵の矩形に 10m より近づかない（P1d の2）
            ClampAgainstEnemies(c, before);
            if (c.Position.X != before.X || c.Position.Y != before.Y) c.MovedThisTick = true;
        }

        // ---------------------------------------------------------------- 陣地構築と地形の効果

        /// <summary>
        /// 陣地構築：陣地構築の命令中に動かなければ進捗が進み、10分で完成する。動くと進捗は 0 に戻る。
        /// 完成すると、中心のマスと、向きを8方位に丸めて直交する左右2マスずつが、平地・砲撃痕なら塹壕になる。
        /// </summary>
        void UpdateEntrenchment(double t)
        {
            foreach (var c in _companies)
            {
                if (!c.OnBoard || !c.UnderCommand) continue;
                if (c.MovedThisTick)
                {
                    c.EntrenchProgress = 0;
                    continue;
                }
                if (c.CurrentOrder.Type != OrderType.Entrench || c.EntrenchProgress >= _p.MapRules.EntrenchSec) continue;
                if (!ReadyToDig(c)) continue;
                // 占領の乱れの間は、陣地構築を始めない（C12）
                if (c.EntrenchProgress <= 0 && InOccupationDisarray(c)) continue;
                c.EntrenchProgress = Math.Min(_p.MapRules.EntrenchSec, c.EntrenchProgress + _p.TickSec);
                if (c.EntrenchProgress >= _p.MapRules.EntrenchSec) CompleteEntrenchment(c, t);
            }
        }

        /// <summary>
        /// 掘り始められるか：目的地のある陣地構築なら、着いて、指定の向きへ向き終えていること。目的地がなければ常に。
        /// </summary>
        static bool ReadyToDig(Company c)
        {
            var o = c.CurrentOrder;
            if (!o.Destination.HasValue) return true;
            if (!c.Arrived) return false;
            double? want = c.FacingOverride ?? o.FacingRad;
            return !want.HasValue || Math.Abs(Angles.Wrap(want.Value - c.Facing)) <= 1e-9;
        }

        void CompleteEntrenchment(Company c, double t)
        {
            foreach (var (i, j) in EntrenchCells(c))
            {
                char ch = _map.TerrainAt(i, j);
                // 陣地の向きは、完成したときの中隊の向き（R2）。もとからある塹壕の向きは変えない
                if ((ch == TerrainMap.Plain || ch == TerrainMap.Crater) && _map.SetOverlay(i, j, TerrainMap.Trench))
                    _map.SetFortificationFacing(i, j, c.Facing);
            }
            if (_log.Enabled)
                _log.Write(Ev(t, "entrench_complete", c).Add("x", c.Position.X).Add("y", c.Position.Y).ToString());
            NoteDispatch(c, DispatchKind.EntrenchDone, t);
        }

        /// <summary>陣地構築で塹壕にするマスの候補：中心と、向きに直交する方向（8方位に丸める）の左右2マスずつ。</summary>
        public IEnumerable<(int i, int j)> EntrenchCells(Company c)
        {
            _map.CellOf(c.Position, out int ci, out int cj);
            double a8 = Math.Round((c.Facing + Math.PI / 2) / (Math.PI / 4)) * (Math.PI / 4);
            int di = (int)Math.Round(Math.Cos(a8)), dj = (int)Math.Round(Math.Sin(a8));
            for (int k = -_p.MapRules.EntrenchHalfWidthCells; k <= _p.MapRules.EntrenchHalfWidthCells; k++)
            {
                int i = ci + k * di, j = cj + k * dj;
                if (_map.InBounds(i, j)) yield return (i, j);
            }
        }

        /// <summary>
        /// 撃たれる側の被弾倍率（中心のマス）。塹壕の 0.3 はその刻みに動いていない中隊にだけ効き、動いていれば 1.0。
        /// 陣地構築の途中は、動いていなければ進捗に応じて地形の値から 0.3 へ近づく。
        /// 開けた場所（平地・砲撃痕・橋・浅瀬・塹壕）を移動中なら、さらに terrain.moving_open_damage_mult を掛ける。
        /// 向きのある守り（塹壕・陣地構築・浅い掩体。R2）は、撃ってきた敵の位置 <paramref name="from"/> が陣地の向きの正面の弧の
        /// 外なら、守りの倍率を terrain.fortification_off_arc_mult に置き換える。<paramref name="from"/> がなければ（自律行動の
        /// 判断など）向きを見ない。村・森などの守りは向きによらない。
        /// </summary>
        public double TerrainDamageMult(Company c, Vec2? from = null, bool closeCombat = false)
        {
            char ch = _map.TerrainAt(c.Position);
            double off = _p.Terrain.FortificationOffArcMult;
            double f;
            if (ch == TerrainMap.Trench)
                f = c.MovedThisTick ? 1.0
                    : CloseFortification(InFortificationArc(_map.FortificationFacingAt(c.Position), c, from) ? _map.DamageMult(TerrainMap.Trench) : off,
                        closeCombat);
            else f = _map.DamageMult(ch);
            if (!c.MovedThisTick && c.EntrenchProgress > 0 && ch != TerrainMap.Trench)
            {
                // 陣地構築の途中の守りは、今の中隊の向きで見る（陣地の向きは完成したときに決まる）。塹壕のマスでは塹壕の向きで見る
                double target = CloseFortification(InFortificationArc(c.Facing, c, from) ? _p.MapRules.EntrenchDamageMult : off, closeCombat);
                if (f > target) f += (target - f) * Math.Min(1.0, c.EntrenchProgress / _p.MapRules.EntrenchSec);
            }
            // 移動中、または接敵中にその場で向きを変えている間（R3）
            if ((c.MovedThisTick || c.TurningAsMoving) && IsOpenGround(ch)) f *= _p.Terrain.MovingOpenDamageMult;
            // 浅い掩体（P2c）。向きは掘り始めたときの中隊の向き（R2）
            f *= ScrapeMult(c, ch, InFortificationArc(c.ScrapeFacing, c, from), closeCombat);
            return f;
        }

        /// <summary>
        /// 向きのある陣地の被弾倍率を、接近戦の相手から撃たれるとき（<paramref name="closeCombat"/>）は close_combat.fortification_mult
        /// より強く効かせない（R4。胸墻はもう守りにならない）。
        /// </summary>
        double CloseFortification(double mult, bool closeCombat) =>
            closeCombat ? Math.Max(mult, _p.CloseCombat.FortificationMult) : mult;

        /// <summary>
        /// 撃ってきた敵の位置 <paramref name="from"/> が、向き <paramref name="facing"/> の陣地の正面の弧（±fire.front_arc_deg）にいるか
        /// （R2）。向きがない陣地、撃ってきた位置が分からないときは true（守りが効く）。
        /// </summary>
        public bool InFortificationArc(double? facing, Company c, Vec2? from)
        {
            if (!facing.HasValue || !from.HasValue || _p.Terrain.IgnoreFortificationFacing) return true;
            var d = from.Value - c.Position;
            if (d.Length < 1e-9) return true;
            double deg = Math.Abs(Angles.Wrap(Math.Atan2(d.Y, d.X) - facing.Value)) * Angles.RadToDeg;
            return deg <= _p.Fire.FrontArcDeg + 1e-9;
        }

        /// <summary>
        /// 中隊が今いる陣地の向き（R2。表示用）：止まって塹壕にいればそのマスの向き、陣地構築の途中なら今の向き、浅い掩体があれば
        /// 掘り始めたときの向き。陣地がなければ null。
        /// </summary>
        public double? FortificationFacingOf(Company c)
        {
            if (c.MovedThisTick) return null;
            char ch = _map.TerrainAt(c.Position);
            if (ch == TerrainMap.Trench) return _map.FortificationFacingAt(c.Position);
            if (c.EntrenchProgress > 0) return c.Facing;
            if (ScrapeMult(c, ch, true) < 1.0) return c.ScrapeFacing;
            return null;
        }

        /// <summary>移動中の被弾倍率を掛ける、開けた場所のマス（平地・砲撃痕・橋・浅瀬・塹壕）。</summary>
        public static bool IsOpenGround(char ch) =>
            ch == TerrainMap.Plain || ch == TerrainMap.Crater || ch == TerrainMap.Bridge || ch == TerrainMap.Ford || ch == TerrainMap.Trench;

        /// <summary>籠城：中心のマスが村か塹壕（基本の地形・構築した塹壕）。動いているかどうかは問わない。</summary>
        void UpdateFortified()
        {
            foreach (var c in _companies)
            {
                if (!c.OnBoard) continue;
                char ch = _map.TerrainAt(c.Position);
                c.Fortified = ch == TerrainMap.Village || ch == TerrainMap.Trench;
            }
        }
    }
}
