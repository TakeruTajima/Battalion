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
        public static bool[] VisibleCells(TerrainMap m, Vec2 observer, double radius)
        {
            var v = new bool[m.Width * m.Height];
            ForCellsWithin(m, observer, radius, (i, j, center) =>
            {
                if (LineOfSight.Visible(m, observer, center)) v[j * m.Width + i] = true;
            });
            return v;
        }

        /// <summary>
        /// 露出の地図：マスの中心が、<paramref name="range"/> 以内のいずれかの観測者（敵）から見えていれば true。
        /// map_reference.py の exposure_map と同じ（視線は敵の中心からマスの中心へ引く）。
        /// </summary>
        public static bool[] Exposure(TerrainMap m, IEnumerable<Vec2> observers, double range)
        {
            var exp = new bool[m.Width * m.Height];
            foreach (var o in observers)
            {
                ForCellsWithin(m, o, range, (i, j, center) =>
                {
                    int k = j * m.Width + i;
                    if (!exp[k] && LineOfSight.Visible(m, o, center)) exp[k] = true;
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
            foreach (var c in _companies)
                if (IsShooter(c)) _sight[c.Id] = BuildSight(c);
        }

        static bool IsShooter(Company c) => c.OnBoard && c.UnderCommand && c.Strength > 0;

        SightGrid BuildSight(Company c) =>
            new SightGrid { From = c.Position, Cells = Visibility.VisibleCells(_map, c.Position, SightRadius) };

        SightGrid SightOf(Company c)
        {
            // 作り直しの後に指揮下へ戻った中隊などは、その場で作る（次の作り直しまで使う）
            if (!_sight.TryGetValue(c.Id, out var g)) _sight[c.Id] = g = BuildSight(c);
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
            var exp = new bool[_map.Width * _map.Height];
            foreach (var e in _companies)
            {
                if (e.Side == side || !IsShooter(e)) continue;
                var g = SightOf(e);
                Visibility.ForCellsWithin(_map, g.From, _p.Fire.RangeMaxM, (i, j, center) =>
                {
                    int k = j * _map.Width + i;
                    if (g.Cells[k]) exp[k] = true;
                });
            }
            return _exposure[s] = exp;
        }

        // ---------------------------------------------------------------- 命令の開始

        void StartOrder(Company c, Order order, double t, double tickStart)
        {
            c.CurrentOrder = order;
            c.Arrived = !order.Moves;
            c.Path = null;
            c.PathIndex = 0;
            c.Backward = false;
            c.AttackChosenAspect = AttackAspect.None;
            c.AttackSide = 0;

            switch (order.Type)
            {
                case OrderType.Advance:
                case OrderType.Retreat:
                    c.MoveGoal = AdjustDestination(c, order.Destination.Value);
                    Plan(c, tickStart);
                    break;
                case OrderType.Attack:
                {
                    var tgt = EnemyOf(c, order.TargetId);
                    if (tgt == null || !tgt.UnderCommand)
                    {
                        HoldInPlace(c);
                        break;
                    }
                    ChooseAttackAspect(c, tgt, order);
                    c.MoveGoal = AdjustDestination(c, JumpOff(tgt, c.AttackChosenAspect, c.AttackSide));
                    Plan(c, tickStart);
                    if (_log.Enabled)
                        _log.Write(Ev(t, "attack_plan", c)
                            .Add("target", tgt.Name)
                            .Add("aspect", AspectName(c.AttackChosenAspect))
                            .Add("side", c.AttackSide)
                            .Add("x", c.MoveGoal.X)
                            .Add("y", c.MoveGoal.Y)
                            .ToString());
                    break;
                }
                case OrderType.Pin:
                    if (EnemyOf(c, order.TargetId) == null) HoldInPlace(c);
                    break;
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

        double ThreatWeightOf(Company c) => c.ThreatWeightOverride ?? MapRules.ThreatWeight(c.Personality);

        /// <summary>今の位置から <see cref="Company.MoveGoal"/> への経路を引く。次の引き直しは30秒後。</summary>
        void Plan(Company c, double tickStart)
        {
            c.NextReplanTime = tickStart + MapRules.ReplanSec;
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
            if (c.CurrentOrder.Type == OrderType.Retreat) c.Backward = InRearArc(c, c.MoveGoal);
        }

        List<PathFinder.Cell> FindPath(Company c, Vec2 goal, out double cost)
        {
            _map.CellOf(c.Position, out int si, out int sj);
            _map.CellOf(goal, out int gi, out int gj);
            var start = new PathFinder.Cell(si, sj);
            var end = new PathFinder.Cell(gi, gj);
            var exposure = ExposureFor(c.Side);
            double w = ThreatWeightOf(c);
            // 止まっている味方は押されないので、その周り（中心から 200m 未満のマス）を避けて引く。
            // 避けると届かない場合（味方に囲まれている等）は、避けずに引き直す
            var path = PathFinder.Find(_map, start, end, exposure, w, out cost, StationaryFriendZone(c));
            return path ?? PathFinder.Find(_map, start, end, exposure, w, out cost);
        }

        /// <summary>
        /// 止まっている指揮下の味方（移動の命令がないか、着いている中隊）の中心から 200m 未満のマス。
        /// 経路はここを通らない。自分が今いるマスは除く。
        /// </summary>
        bool[] StationaryFriendZone(Company c)
        {
            bool[] zone = null;
            double min = _p.Movement.FriendlyMinSpacingM;
            foreach (var f in _companies)
            {
                if (f == c || f.Side != c.Side || !f.OnBoard || !f.UnderCommand) continue;
                if (f.CurrentOrder.Moves && !f.Arrived) continue;
                if (zone == null) zone = new bool[_map.Width * _map.Height];
                Visibility.ForCellsWithin(_map, f.Position, min, (i, j, center) =>
                {
                    if (Vec2.Distance(center, f.Position) < min) zone[j * _map.Width + i] = true;
                });
            }
            if (zone != null) zone[_map.IndexOf(c.Position)] = false;
            return zone;
        }

        double PathCost(Company c, Vec2 rawGoal)
        {
            var cells = FindPath(c, AdjustDestination(c, rawGoal), out double cost);
            return cells == null ? double.PositiveInfinity : cost;
        }

        /// <summary>目的地が向きから 120° より後ろ（背後の弧）にあるか。</summary>
        bool InRearArc(Company c, Vec2 p)
        {
            var d = p - c.Position;
            if (d.Length < 1e-9) return false;
            double deg = Math.Abs(Angles.Wrap(Math.Atan2(d.Y, d.X) - c.Facing)) * Angles.RadToDeg;
            return deg > _p.Fire.FlankArcDeg;
        }

        // ---------------------------------------------------------------- 攻撃

        /// <summary>攻撃の発起点：敵の中心から、敵の向きを基準にした方向へ 250m（側面は +1 が敵の左、−1 が右）。</summary>
        public static Vec2 JumpOff(Company tgt, AttackAspect aspect, int side)
        {
            var front = Vec2.FromAngle(tgt.Facing);
            var left = new Vec2(-front.Y, front.X);
            switch (aspect)
            {
                case AttackAspect.Rear: return tgt.Position - front * MapRules.AttackStandoffM;
                case AttackAspect.Flank: return tgt.Position + left * (side * MapRules.AttackStandoffM);
                default: return tgt.Position + front * MapRules.AttackStandoffM;
            }
        }

        void ChooseAttackAspect(Company c, Company tgt, Order order)
        {
            var aspect = order.Aspect;
            int side = 0;
            if (aspect == AttackAspect.Flank)
            {
                side = order.FlankSide != 0 ? Math.Sign(order.FlankSide) : CheaperFlankSide(c, tgt, out _);
            }
            else if (aspect == AttackAspect.None)
            {
                // 指定がなければ正面。機敏は、側面の経路コストが正面の1.5倍以下なら側面を選ぶ
                aspect = AttackAspect.Front;
                if (c.Personality == Personality.Agile)
                {
                    double front = PathCost(c, JumpOff(tgt, AttackAspect.Front, 0));
                    int s = CheaperFlankSide(c, tgt, out double flank);
                    if (!double.IsInfinity(flank) && flank <= MapRules.AgileFlankCostRatio * front)
                    {
                        aspect = AttackAspect.Flank;
                        side = s;
                    }
                }
            }
            c.AttackChosenAspect = aspect;
            c.AttackSide = side;
        }

        /// <summary>左右の側面の発起点のうち、経路コストの小さい側（同じなら左）。</summary>
        int CheaperFlankSide(Company c, Company tgt, out double cost)
        {
            double left = PathCost(c, JumpOff(tgt, AttackAspect.Flank, +1));
            double right = PathCost(c, JumpOff(tgt, AttackAspect.Flank, -1));
            cost = Math.Min(left, right);
            return left <= right ? +1 : -1;
        }

        /// <summary>
        /// 攻撃の続き。目標が指揮下でなくなったら（壊走・降伏など）その場で保持する。
        /// 30秒ごとに敵の今の位置と向きで発起点を計算し直す（選んだ方向と側は変えない）。着いた後は、発起点が 50m より離れたらまた動く。
        /// </summary>
        void UpdateAttack(Company c, double tickStart, double maxTurn)
        {
            var tgt = EnemyOf(c, c.CurrentOrder.TargetId);
            if (tgt == null || !tgt.UnderCommand)
            {
                HoldInPlace(c);
                return;
            }
            if (tickStart >= c.NextReplanTime)
            {
                var goal = AdjustDestination(c, JumpOff(tgt, c.AttackChosenAspect, c.AttackSide));
                if (!c.Arrived || Vec2.Distance(goal, c.Position) > MapRules.AttackRepositionM)
                {
                    c.MoveGoal = goal;
                    c.Arrived = false;
                    Plan(c, tickStart);
                }
                else
                {
                    c.NextReplanTime = tickStart + MapRules.ReplanSec;
                }
            }
            if (c.Arrived) FaceToward(c, tgt.Position, maxTurn);
        }

        void FaceToward(Company c, Vec2 p, double maxTurn)
        {
            var d = p - c.Position;
            if (d.Length < 1e-9) return;
            c.Facing = TurnToward(c.Facing, Math.Atan2(d.Y, d.X), maxTurn);
        }

        /// <summary>拘束の命令中と、攻撃で発起点に着いた後に、優先して撃つ命令の目標。</summary>
        Company OrderTarget(Company c)
        {
            var o = c.CurrentOrder;
            if (o.Type == OrderType.Pin || (o.Type == OrderType.Attack && c.Arrived)) return EnemyOf(c, o.TargetId);
            return null;
        }

        bool CanFireAt(Company shooter, Company e)
        {
            if (!e.OnBoard || e.Side == shooter.Side || e.Strength <= 0) return false;
            if (Vec2.Distance(shooter.Position, e.Position) > _p.Fire.RangeMaxM) return false;
            return LineOfSight.Visible(_map, shooter.Position, e.Position);
        }

        // ---------------------------------------------------------------- 移動

        void MoveUnderCommand(Company c, double t, double tickStart, double dt, double maxTurn)
        {
            switch (c.CurrentOrder.Type)
            {
                case OrderType.Attack:
                    UpdateAttack(c, tickStart, maxTurn);
                    break;
                case OrderType.Pin:
                {
                    var tgt = EnemyOf(c, c.CurrentOrder.TargetId);
                    if (tgt == null) HoldInPlace(c);
                    else FaceToward(c, tgt.Position, maxTurn);
                    return;
                }
                case OrderType.Advance:
                case OrderType.Retreat:
                    if (!c.Arrived && tickStart >= c.NextReplanTime)
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
                if (o.FacingRad.HasValue) c.Facing = TurnToward(c.Facing, o.FacingRad.Value, maxTurn);
                return;
            }
            FollowPath(c, dt, maxTurn);
        }

        /// <summary>
        /// 経路に沿って進む。速度は中心のマスの移動倍率×拘束の×0.5。前進は各区間の向きへ旋回し終えてから進む。
        /// 後ろ向きの後退は向きを変えずに×0.6で下がる。到着したら指定の向きへ旋回する。
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

            double mv = TerrainMap.MoveMultOf(_map.TerrainAt(c.Position));
            if (mv <= 0) mv = TerrainMap.MoveMultOf(TerrainMap.Ford); // 通れないマスに押し出された場合の保険
            double speed = _p.Movement.SpeedMps * mv * (c.Pinned ? _p.Pinning.MoveMult : 1.0);
            if (c.Backward)
            {
                // 後退は向きを変えずに後ろ向きに下がる（速度×0.6）。背を向けて逃げるのは壊走だけ
                speed *= _p.Movement.RetreatSpeedMult;
            }
            else
            {
                // 進行方向を向いてから進む（旋回が終わるまでは動かない）
                double heading = Math.Atan2(to.Y, to.X);
                c.Facing = TurnToward(c.Facing, heading, maxTurn);
                if (Math.Abs(Angles.Wrap(heading - c.Facing)) > 1e-9) return;
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
            for (double r = MapRules.DestinationSearchStepM; r <= MapRules.DestinationSearchMaxM + 1e-9; r += MapRules.DestinationSearchStepM)
            {
                Vec2? best = null;
                double bestD = double.MaxValue;
                for (int k = 0; k < MapRules.DestinationSearchDirs; k++)
                {
                    var p = dest + Vec2.FromAngle(2 * Math.PI * k / MapRules.DestinationSearchDirs) * r;
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
            c.Position = p;
            c.MovedThisTick = true;
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
                if (c.CurrentOrder.Type != OrderType.Entrench || c.EntrenchProgress >= MapRules.EntrenchSec) continue;
                c.EntrenchProgress = Math.Min(MapRules.EntrenchSec, c.EntrenchProgress + _p.TickSec);
                if (c.EntrenchProgress >= MapRules.EntrenchSec) CompleteEntrenchment(c, t);
            }
        }

        void CompleteEntrenchment(Company c, double t)
        {
            foreach (var (i, j) in EntrenchCells(c))
            {
                char ch = _map.TerrainAt(i, j);
                if (ch == TerrainMap.Plain || ch == TerrainMap.Crater) _map.SetOverlay(i, j, TerrainMap.Trench);
            }
            if (_log.Enabled)
                _log.Write(Ev(t, "entrench_complete", c).Add("x", c.Position.X).Add("y", c.Position.Y).ToString());
        }

        /// <summary>陣地構築で塹壕にするマスの候補：中心と、向きに直交する方向（8方位に丸める）の左右2マスずつ。</summary>
        public IEnumerable<(int i, int j)> EntrenchCells(Company c)
        {
            _map.CellOf(c.Position, out int ci, out int cj);
            double a8 = Math.Round((c.Facing + Math.PI / 2) / (Math.PI / 4)) * (Math.PI / 4);
            int di = (int)Math.Round(Math.Cos(a8)), dj = (int)Math.Round(Math.Sin(a8));
            for (int k = -MapRules.EntrenchHalfWidthCells; k <= MapRules.EntrenchHalfWidthCells; k++)
            {
                int i = ci + k * di, j = cj + k * dj;
                if (_map.InBounds(i, j)) yield return (i, j);
            }
        }

        /// <summary>
        /// 撃たれる側の被弾倍率（中心のマス）。塹壕の 0.3 はその刻みに動いていない中隊にだけ効き、動いていれば 1.0。
        /// 陣地構築の途中は、動いていなければ進捗に応じて地形の値から 0.3 へ近づく。
        /// </summary>
        public double TerrainDamageMult(Company c)
        {
            char ch = _map.TerrainAt(c.Position);
            double f = ch == TerrainMap.Trench
                ? (c.MovedThisTick ? 1.0 : TerrainMap.DamageMultOf(TerrainMap.Trench))
                : TerrainMap.DamageMultOf(ch);
            if (!c.MovedThisTick && c.EntrenchProgress > 0 && f > MapRules.EntrenchDamageMult)
                f += (MapRules.EntrenchDamageMult - f) * Math.Min(1.0, c.EntrenchProgress / MapRules.EntrenchSec);
            return f;
        }

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
