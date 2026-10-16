using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>地図の範囲。壊走した中隊がこの外に出ると会戦から離脱する。</summary>
    public readonly struct MapBounds
    {
        public readonly double MinX, MinY, MaxX, MaxY;

        public MapBounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    }

    /// <summary>
    /// 戦闘シミュレーションの核。固定刻み（既定 0.5 秒）。
    /// 1刻みの処理順：命令の適用 → 移動と旋回 → 目標選択 → 損害と士気（同時解決） → 状態遷移 → ログ。
    /// </summary>
    public sealed class Simulation
    {
        readonly SimParams _p;
        readonly Pcg32 _rng;
        readonly IEventSink _log;
        readonly MapBounds _bounds;
        readonly List<Company> _companies = new List<Company>();
        readonly List<Order> _pending = new List<Order>();
        readonly int _snapshotEveryTicks;

        public SimParams Params => _p;
        public IReadOnlyList<Company> Companies => _companies;
        public long Tick { get; private set; }
        /// <summary>経過したゲーム内時間（秒）。直前に終わった刻みの終了時刻。</summary>
        public double Time => Tick * _p.TickSec;
        /// <summary>最初に壊走（降伏を含む）が起きた刻みの終了時刻（秒）。まだなら null。</summary>
        public double? FirstRoutTime { get; private set; }

        public const double SnapshotIntervalSec = 10.0;

        public Simulation(SimParams p, ulong seed, MapBounds bounds, IEventSink log = null)
        {
            _p = p;
            _rng = new Pcg32(seed);
            _bounds = bounds;
            _log = log ?? NullEventSink.Instance;
            _snapshotEveryTicks = (int)Math.Round(SnapshotIntervalSec / p.TickSec);
        }

        public Company AddCompany(string name, Side side, Training training, Personality personality,
            Vec2 position, double facingDeg, int? strength = null, double? morale = null)
        {
            var c = new Company(_companies.Count, name, side, training, personality,
                strength ?? _p.Movement.CompanyStrength, morale ?? _p.Morale.Initial,
                position, facingDeg * Angles.DegToRad);
            _companies.Add(c);
            return c;
        }

        public Company Get(int id) => _companies[id];

        public Company Find(string name)
        {
            foreach (var c in _companies)
                if (c.Name == name) return c;
            return null;
        }

        /// <summary>命令を渡す。次の刻みの最初に即時適用される（P0）。</summary>
        public void Issue(Order order) => _pending.Add(order);

        public void Step()
        {
            Tick++;
            double t = Time;

            ApplyOrders(t);
            Move(t);
            var shots = SelectTargets();
            Resolve(shots, t);
            Transitions(t);

            if (_log.Enabled && Tick % _snapshotEveryTicks == 0)
                foreach (var c in _companies)
                    if (c.OnBoard) LogSnapshot(c, t);
        }

        // ---------------------------------------------------------------- 1. 命令

        void ApplyOrders(double t)
        {
            if (_pending.Count == 0) return;
            foreach (var o in _pending)
            {
                var c = _companies[o.CompanyId];
                // 壊走・再集結中は命令を受け付けない（破棄する）
                if (!c.UnderCommand) continue;

                var order = o;
                // 動揺中は前進を拒み、その場で保持する
                if (order.Type == OrderType.Advance && c.State == CompanyState.Shaken)
                    order = Order.Hold(c.Id, order.FacingRad);

                if (order.Type == OrderType.Retreat)
                {
                    if (c.Pinned && !c.Disengaging)
                    {
                        double hit = _p.Pinning.DisengageMoraleHit * _p.Of(c.Training).MoraleSensitivity;
                        c.Morale = Math.Max(0, c.Morale - hit);
                        c.Ledger.Disengage -= hit;
                        c.Disengaging = true;
                    }
                }
                else
                {
                    c.Disengaging = false;
                }

                c.CurrentOrder = order;
                c.Arrived = order.Type == OrderType.Hold;
            }
            _pending.Clear();
        }

        // ---------------------------------------------------------------- 2. 移動と旋回

        void Move(double t)
        {
            double dt = _p.TickSec;
            double maxTurn = _p.Movement.TurnDegPerSec * Angles.DegToRad * dt;

            foreach (var c in _companies)
            {
                if (!c.OnBoard) continue;

                if (c.State == CompanyState.Routed)
                {
                    MoveRouted(c, t, dt);
                    continue;
                }
                if (c.State == CompanyState.Rallying) continue;

                var o = c.CurrentOrder;
                if (o.Type == OrderType.Hold || c.Arrived)
                {
                    if (o.FacingRad.HasValue) c.Facing = TurnToward(c.Facing, o.FacingRad.Value, maxTurn);
                    continue;
                }

                var to = o.Destination.Value - c.Position;
                double dist = to.Length;
                if (dist < 1e-9)
                {
                    ArriveAt(c);
                    if (o.FacingRad.HasValue) c.Facing = TurnToward(c.Facing, o.FacingRad.Value, maxTurn);
                    continue;
                }

                // 進行方向を向いてから進む（旋回が終わるまでは動かない）
                double heading = Math.Atan2(to.Y, to.X);
                c.Facing = TurnToward(c.Facing, heading, maxTurn);
                if (Math.Abs(Angles.Wrap(heading - c.Facing)) > 1e-9) continue;

                double speed = _p.Movement.SpeedMps * (c.Pinned ? _p.Pinning.MoveMult : 1.0);
                double step = speed * dt;
                if (step >= dist)
                {
                    c.Position = o.Destination.Value;
                    ArriveAt(c);
                }
                else
                {
                    c.Position = c.Position + to * (step / dist);
                }
            }
        }

        static void ArriveAt(Company c)
        {
            c.Arrived = true;
            c.Disengaging = false;
        }

        void MoveRouted(Company c, double t, double dt)
        {
            // 600m 以内の敵の中心の平均位置から遠ざかる。該当する敵がいなければその場に止まる。
            double sx = 0, sy = 0;
            int n = 0;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side) continue;
                if (Vec2.Distance(c.Position, e.Position) > _p.Rout.FleeFromRadiusM) continue;
                sx += e.Position.X;
                sy += e.Position.Y;
                n++;
            }
            if (n == 0) return;

            var away = c.Position - new Vec2(sx / n, sy / n);
            double len = away.Length;
            Vec2 dir = len > 1e-9 ? away * (1.0 / len) : Vec2.FromAngle(c.Facing);
            c.Facing = Math.Atan2(dir.Y, dir.X);
            c.Position = c.Position + dir * (_p.Rout.FleeSpeedMps * dt);

            if (!_bounds.Contains(c.Position))
            {
                var prev = c.State;
                c.State = CompanyState.LeftBattle;
                if (_log.Enabled)
                {
                    LogStateChange(c, prev, t);
                    _log.Write(Ev(t, "left_battle", c)
                        .Add("strength", c.Strength)
                        .Add("morale", c.Morale)
                        .Add("x", c.Position.X)
                        .Add("y", c.Position.Y)
                        .ToString());
                }
            }
        }

        static double TurnToward(double facing, double target, double maxTurn)
        {
            double diff = Angles.Wrap(target - facing);
            if (Math.Abs(diff) <= maxTurn) return Angles.Wrap(target);
            return Angles.Wrap(facing + Math.Sign(diff) * maxTurn);
        }

        // ---------------------------------------------------------------- 3. 目標選択

        struct Shot
        {
            public int Shooter;
            public int Target;
            public Aspect AspectOnTarget;
            public bool TargetInFrontArc;
            public double Distance;
        }

        /// <summary>撃たれる側 <paramref name="target"/> から見た、位置 <paramref name="from"/> の方向。</summary>
        public Aspect AspectOf(Company target, Vec2 from)
        {
            var d = from - target.Position;
            double ang = Math.Atan2(d.Y, d.X);
            double deg = Math.Abs(Angles.Wrap(ang - target.Facing)) * Angles.RadToDeg;
            if (deg <= _p.Fire.FrontArcDeg) return Aspect.Front;
            if (deg <= _p.Fire.FlankArcDeg) return Aspect.Flank;
            return Aspect.Rear;
        }

        /// <summary>
        /// 目標選択：射程内の敵のうち、正面の弧（±60°）にいる最も近い敵。いなければそれ以外で最も近い敵。
        /// 距離が同じなら ID の小さい方。壊走中・再集結中の中隊は撃たない。
        /// </summary>
        public Company SelectTarget(Company shooter)
        {
            if (!shooter.UnderCommand || shooter.Strength <= 0) return null;
            Company bestFront = null, bestAny = null;
            double dFront = double.MaxValue, dAny = double.MaxValue;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == shooter.Side || e.Strength <= 0) continue;
                double d = Vec2.Distance(shooter.Position, e.Position);
                if (d > _p.Fire.RangeMaxM) continue;
                // 走査は ID 昇順なので、厳密に小さいときだけ更新すれば同距離は ID の小さい方が残る
                if (d < dAny) { dAny = d; bestAny = e; }
                if (AspectOf(shooter, e.Position) == Aspect.Front && d < dFront) { dFront = d; bestFront = e; }
            }
            return bestFront ?? bestAny;
        }

        List<Shot> SelectTargets()
        {
            var shots = new List<Shot>(_companies.Count);
            foreach (var c in _companies)
            {
                if (!c.OnBoard) continue;
                var target = SelectTarget(c);
                c.TargetId = target?.Id ?? -1;
                if (target == null) continue;
                shots.Add(new Shot
                {
                    Shooter = c.Id,
                    Target = target.Id,
                    AspectOnTarget = AspectOf(target, c.Position),
                    TargetInFrontArc = AspectOf(c, target.Position) == Aspect.Front,
                    Distance = Vec2.Distance(c.Position, target.Position),
                });
            }
            return shots;
        }

        // ---------------------------------------------------------------- 4. 損害と士気（同時解決）

        void Resolve(List<Shot> shots, double t)
        {
            int n = _companies.Count;
            double dtMin = _p.TickSec / 60.0;
            var cas = new int[n];
            var casMorale = new double[n, 3];
            var hit = new bool[n, 3];
            var firstShooter = new int[n, 3];
            var targeted = new bool[n];
            var pinnedNow = new bool[n];
            for (int i = 0; i < n; i++)
                for (int a = 0; a < 3; a++)
                    firstShooter[i, a] = -1;

            // 全射撃の損害を、刻みの開始時点の状態から計算してバッファに積む
            foreach (var s in shots)
            {
                var u = _companies[s.Shooter];
                var tg = _companies[s.Target];
                int a = (int)s.AspectOnTarget;

                double fQuality = _p.Of(u.Training).FireMult;
                double fRange = s.Distance <= _p.Fire.RangeCloseM ? _p.Fire.RangeCloseMult : 1.0;
                double fTerrain = 1.0; // P0 は平地のみ
                double fAspect = _p.Fire.AspectCasualtyMult[a];
                double fState = 1.0;
                if (u.State == CompanyState.Shaken) fState *= _p.Fire.ShakenMult;
                if (!s.TargetInFrontArc) fState *= _p.Fire.NonFrontTargetMult;
                // 砲撃による制圧（×0.5）は P3

                bool close = s.Distance <= _p.CloseCombat.RadiusM;
                double fExtra = 1.0;
                if (tg.State == CompanyState.Routed && s.Distance <= _p.Rout.PursuitRadiusM) fExtra *= _p.Rout.PursuitCasualtyMult;
                if (tg.Disengaging) fExtra *= _p.Pinning.DisengageCasualtyMult;
                if (close) fExtra *= _p.CloseCombat.CasualtyMult;

                double lambda = _p.Fire.KPerMin * u.Strength * fQuality * fRange * fTerrain * fAspect * fState * fExtra * dtMin;
                int c = _rng.NextPoisson(lambda);

                cas[tg.Id] += c;
                double m = (double)c / tg.InitialStrength * 100.0 * _p.Morale.PerPctLoss * _p.Morale.AspectMoraleMult[a];
                if (close) m *= _p.CloseCombat.MoraleMult;
                casMorale[tg.Id, a] += m;
                hit[tg.Id, a] = true;
                if (firstShooter[tg.Id, a] < 0) firstShooter[tg.Id, a] = u.Id;
                targeted[tg.Id] = true;
                if (s.Distance <= _p.Pinning.RadiusM) pinnedNow[tg.Id] = true;
            }

            // 一括で適用する
            foreach (var c in _companies)
            {
                if (!c.OnBoard) continue;
                int i = c.Id;
                c.Strength = Math.Max(0, c.Strength - cas[i]);
                if (targeted[i]) c.LastTargetedTime = t;

                if (c.State == CompanyState.Routed)
                {
                    // 壊走中は損害だけ受ける。士気・包囲・拘束は扱わない
                    SetEncircled(c, false, null, t);
                    c.Pinned = false;
                    continue;
                }

                double sens = _p.Of(c.Training).MoraleSensitivity;
                double total = 0;

                for (int a = 0; a < 3; a++)
                {
                    double d = casMorale[i, a] * sens;
                    if (d != 0)
                    {
                        c.Ledger.AddAspect((Aspect)a, -d);
                        total += d;
                    }
                }

                if (targeted[i])
                {
                    double d = _p.Morale.UnderFirePerMin * dtMin * sens;
                    c.Ledger.UnderFire -= d;
                    total += d;
                }

                if (hit[i, (int)Aspect.Flank] && !c.FlankShockTaken)
                {
                    c.FlankShockTaken = true;
                    double d = _p.Morale.FirstHitShockFlank * sens;
                    c.Ledger.Flank -= d;
                    total += d;
                    if (_log.Enabled)
                        _log.Write(Ev(t, "first_flank_hit", c).Add("by", _companies[firstShooter[i, (int)Aspect.Flank]].Name).Add("morale_hit", d).ToString());
                }
                if (hit[i, (int)Aspect.Rear] && !c.RearShockTaken)
                {
                    c.RearShockTaken = true;
                    double d = _p.Morale.FirstHitShockRear * sens;
                    c.Ledger.Rear -= d;
                    total += d;
                    if (_log.Enabled)
                        _log.Write(Ev(t, "first_rear_hit", c).Add("by", _companies[firstShooter[i, (int)Aspect.Rear]].Name).Add("morale_hit", d).ToString());
                }

                if (c.UnderCommand)
                {
                    int aspects = (hit[i, 0] ? 1 : 0) + (hit[i, 1] ? 1 : 0) + (hit[i, 2] ? 1 : 0);
                    bool byAspects = aspects >= _p.Encirclement.MinAspects;
                    bool byCorridor = RearCorridorBlocked(c);
                    bool enc = byAspects || byCorridor;
                    SetEncircled(c, enc, enc ? EncirclementReason(byAspects, byCorridor) : null, t);
                    if (enc)
                    {
                        double d = _p.Encirclement.MoralePerMin * dtMin * sens;
                        c.Ledger.Encircled -= d;
                        total += d;
                    }
                    c.Pinned = pinnedNow[i];
                }
                else
                {
                    SetEncircled(c, false, null, t);
                    c.Pinned = false;
                }

                c.Morale = Math.Max(0, c.Morale - total);

                // 回復：壊走しておらず、60秒間狙われていなければ毎分 +1×練度倍率。上限 70
                if (t - c.LastTargetedTime >= _p.Morale.RecoveryQuietSec && c.Morale < _p.Morale.RecoveryCap)
                {
                    double r = _p.Morale.RecoveryPerMin * _p.Of(c.Training).RecoveryMult * dtMin;
                    double after = Math.Min(_p.Morale.RecoveryCap, c.Morale + r);
                    c.Ledger.Recovery += after - c.Morale;
                    c.Morale = after;
                }
            }
        }

        static string EncirclementReason(bool byAspects, bool byCorridor)
        {
            if (byAspects && byCorridor) return "aspects+rear_corridor";
            return byAspects ? "aspects" : "rear_corridor";
        }

        void SetEncircled(Company c, bool value, string reason, double t)
        {
            if (c.Encircled == value) return;
            c.Encircled = value;
            if (!_log.Enabled) return;
            var ev = Ev(t, value ? "encircled_start" : "encircled_end", c);
            if (value) ev.Add("reason", reason);
            _log.Write(ev.ToString());
        }

        /// <summary>背後 500m・左右 ±200m の帯に、壊走していない敵中隊の中心があるか。</summary>
        public bool RearCorridorBlocked(Company c)
        {
            double fx = Math.Cos(c.Facing), fy = Math.Sin(c.Facing);
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || e.State == CompanyState.Routed || e.Strength <= 0) continue;
                double dx = e.Position.X - c.Position.X, dy = e.Position.Y - c.Position.Y;
                double along = dx * fx + dy * fy;
                double side = -dx * fy + dy * fx;
                if (along >= -_p.Encirclement.RearCorridorDepthM && along < 0 && Math.Abs(side) <= _p.Encirclement.RearCorridorHalfWidthM)
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 5. 状態遷移

        void Transitions(double t)
        {
            // 壊走と、その連鎖（隣の味方の士気 −10 で次の壊走が起きうる）を収まるまで繰り返す
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var c in _companies)
                {
                    if (!c.UnderCommand) continue;
                    var tp = _p.Of(c.Training);
                    if (c.Morale <= tp.RoutThreshold)
                    {
                        Rout(c, t);
                        changed = true;
                        continue;
                    }
                    var want = c.Morale <= _p.Morale.ShakenThreshold ? CompanyState.Shaken : CompanyState.Normal;
                    if (want != c.State)
                    {
                        var prev = c.State;
                        c.State = want;
                        // 動揺すると前進を止めてその場で保持する
                        if (want == CompanyState.Shaken && c.CurrentOrder.Type == OrderType.Advance && !c.Arrived)
                        {
                            c.CurrentOrder = Order.Hold(c.Id, c.CurrentOrder.FacingRad);
                            c.Arrived = true;
                        }
                        if (_log.Enabled) LogStateChange(c, prev, t);
                    }
                }
            }

            // 壊走 → 再集結 → 指揮下へ復帰
            foreach (var c in _companies)
            {
                if (c.State == CompanyState.Routed)
                {
                    if (EnemyWithin(c, _p.Rout.RallyClearRadiusM)) c.RallyClearSec = 0;
                    else c.RallyClearSec += _p.TickSec;

                    if (c.RallyClearSec >= _p.Rout.RallyAfterSec)
                    {
                        c.State = CompanyState.Rallying;
                        if (_log.Enabled)
                        {
                            LogStateChange(c, CompanyState.Routed, t);
                            _log.Write(Ev(t, "rally", c).Add("phase", "start").Add("morale", c.Morale).Add("strength", c.Strength).ToString());
                        }
                    }
                }
                else if (c.State == CompanyState.Rallying)
                {
                    if (EnemyWithin(c, _p.Rout.RallyClearRadiusM))
                    {
                        // 敵が近づいたら再び逃げる（連鎖のショックは与えない）
                        c.State = CompanyState.Routed;
                        c.RallyClearSec = 0;
                        if (_log.Enabled) LogStateChange(c, CompanyState.Rallying, t);
                    }
                    else if (c.Morale >= _p.Rout.RallyReturnMorale)
                    {
                        c.State = c.Morale <= _p.Morale.ShakenThreshold ? CompanyState.Shaken : CompanyState.Normal;
                        c.CurrentOrder = Order.Hold(c.Id);
                        c.Arrived = true;
                        if (_log.Enabled)
                        {
                            _log.Write(Ev(t, "rally", c).Add("phase", "complete").Add("morale", c.Morale).Add("strength", c.Strength).ToString());
                            LogStateChange(c, CompanyState.Rallying, t);
                        }
                    }
                }
            }
        }

        void Rout(Company c, double t)
        {
            var prev = c.State;
            bool surrender = c.Encircled;
            c.State = surrender ? CompanyState.Surrendered : CompanyState.Routed;
            c.Pinned = false;
            c.Disengaging = false;
            c.RallyClearSec = 0;
            c.TargetId = -1;
            if (!FirstRoutTime.HasValue) FirstRoutTime = t;

            if (_log.Enabled)
            {
                LogStateChange(c, prev, t);
                _log.Write(Ev(t, "rout", c)
                    .Add("morale", c.Morale)
                    .Add("strength", c.Strength)
                    .Add("encircled", surrender)
                    .Add("morale_causes", c.Ledger.ToJson())
                    .ToString());
                if (surrender)
                    _log.Write(Ev(t, "surrender", c).Add("strength", c.Strength).ToString());
            }

            // 壊走の連鎖：300m 以内の味方に一度だけ −10（この壊走1回につき1度）
            foreach (var f in _companies)
            {
                if (f == c || f.Side != c.Side || !f.OnBoard || f.State == CompanyState.Routed) continue;
                if (Vec2.Distance(f.Position, c.Position) > _p.Morale.NeighborRoutRadiusM) continue;
                double d = _p.Morale.NeighborRoutShock * _p.Of(f.Training).MoraleSensitivity;
                f.Morale = Math.Max(0, f.Morale - d);
                f.Ledger.NeighborRout -= d;
                if (_log.Enabled)
                    _log.Write(Ev(t, "neighbor_rout_shock", f).Add("from", c.Name).Add("morale_hit", d).ToString());
            }
        }

        bool EnemyWithin(Company c, double radius)
        {
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side) continue;
                if (Vec2.Distance(c.Position, e.Position) <= radius) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 6. ログ

        JsonLineBuilder Ev(double t, string type, Company c)
        {
            return new JsonLineBuilder().Add("t", t).Add("type", type).Add("unit", c.Name);
        }

        void LogStateChange(Company c, CompanyState from, double t)
        {
            _log.Write(Ev(t, "state_change", c)
                .Add("from", Names.Of(from))
                .Add("to", Names.Of(c.State))
                .Add("morale", c.Morale)
                .Add("strength", c.Strength)
                .ToString());
        }

        void LogSnapshot(Company c, double t)
        {
            _log.Write(Ev(t, "unit_snapshot", c)
                .Add("side", Names.Of(c.Side))
                .Add("strength", c.Strength)
                .Add("morale", c.Morale)
                .Add("x", c.Position.X)
                .Add("y", c.Position.Y)
                .Add("facing_deg", c.Facing * Angles.RadToDeg)
                .Add("state", Names.Of(c.State))
                .Add("pinned", c.Pinned)
                .Add("encircled", c.Encircled)
                .Add("target", c.TargetId >= 0 ? _companies[c.TargetId].Name : null)
                .Add("order", Order.Name(c.CurrentOrder.Type))
                .Add("morale_causes", c.Ledger.ToJson())
                .ToString());
        }
    }
}
