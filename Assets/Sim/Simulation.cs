using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>
    /// 1刻みごとに、命令の適用の前に呼ばれる台本（予備隊の台本など）。命令は <see cref="Simulation.Issue"/> で渡す。
    /// </summary>
    public interface ISimScript
    {
        void Update(Simulation sim, double tickStart);
    }

    /// <summary>
    /// 戦闘シミュレーションの核。固定刻み（既定 0.5 秒）。
    /// 1刻みの処理順：（見える範囲の作り直し・台本）→ 命令の適用 → 移動と旋回（重なりの禁止、陣地構築） → 目標選択
    /// → 損害と士気（同時解決） → 状態遷移 → ログ。
    /// </summary>
    public sealed partial class Simulation
    {
        readonly SimParams _p;
        readonly Pcg32 _rng;
        readonly IEventSink _log;
        readonly TerrainMap _map;
        readonly List<Company> _companies = new List<Company>();
        readonly List<Order> _pending = new List<Order>();
        readonly List<ISimScript> _scripts = new List<ISimScript>();
        readonly int _snapshotEveryTicks;
        readonly int _exposureEveryTicks;

        public SimParams Params => _p;
        public TerrainMap Map => _map;
        public IReadOnlyList<Company> Companies => _companies;
        public long Tick { get; private set; }
        /// <summary>経過したゲーム内時間（秒）。直前に終わった刻みの終了時刻。</summary>
        public double Time => Tick * _p.TickSec;
        /// <summary>最初に壊走（降伏を含む）が起きた刻みの終了時刻（秒）。まだなら null。</summary>
        public double? FirstRoutTime { get; private set; }

        public const double SnapshotIntervalSec = 10.0;

        public Simulation(SimParams p, ulong seed, TerrainMap map, IEventSink log = null)
        {
            _p = p;
            _rng = new Pcg32(seed);
            _map = map;
            _log = log ?? NullEventSink.Instance;
            _snapshotEveryTicks = (int)Math.Round(SnapshotIntervalSec / p.TickSec);
            _exposureEveryTicks = (int)Math.Round(p.MapRules.ExposureRebuildSec / p.TickSec);
        }

        public void AddScript(ISimScript script) => _scripts.Add(script);

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

        /// <summary>命令を渡す。次の刻みの最初に即時適用される（伝令の遅延は P2）。</summary>
        public void Issue(Order order) => _pending.Add(order);

        public void Step()
        {
            Tick++;
            double t = Time;
            double tickStart = t - _p.TickSec;

            if ((Tick - 1) % _exposureEveryTicks == 0) RebuildVisibility();
            foreach (var s in _scripts) s.Update(this, tickStart);

            ApplyOrders(t, tickStart);
            Move(t, tickStart);
            UpdateFortified();
            var shots = SelectTargets();
            Resolve(shots, t, tickStart);
            Transitions(t);

            if (_log.Enabled && Tick % _snapshotEveryTicks == 0)
                foreach (var c in _companies)
                    if (c.OnBoard) LogSnapshot(c, t);
        }

        // ---------------------------------------------------------------- 1. 命令

        void ApplyOrders(double t, double tickStart)
        {
            if (_pending.Count == 0) return;
            var orders = _pending.ToArray();
            _pending.Clear();
            foreach (var o in orders)
            {
                var c = _companies[o.CompanyId];
                // 壊走・再集結中は命令を受け付けない（破棄する）
                if (!c.UnderCommand) continue;

                var order = o;
                // 動揺中は前進・攻撃を拒み、その場で保持する
                if (c.State == CompanyState.Shaken && (order.Type == OrderType.Advance || order.Type == OrderType.Attack))
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

                StartOrder(c, order, t, tickStart);
            }
        }

        // ---------------------------------------------------------------- 2. 移動と旋回

        void Move(double t, double tickStart)
        {
            double dt = _p.TickSec;
            double maxTurn = _p.Movement.TurnDegPerSec * Angles.DegToRad * dt;

            foreach (var c in _companies)
            {
                c.MovedThisTick = false;
                if (!c.OnBoard) continue;

                if (c.State == CompanyState.Routed)
                {
                    MoveRouted(c, t, dt);
                    continue;
                }
                if (c.State == CompanyState.Rallying) continue;

                var before = c.Position;
                MoveUnderCommand(c, t, tickStart, dt, maxTurn);
                c.MovedThisTick = c.Position.X != before.X || c.Position.Y != before.Y;
            }

            KeepFriendlySpacing();
            UpdateEntrenchment(t);
        }

        static void ArriveAt(Company c)
        {
            c.Arrived = true;
            c.Disengaging = false;
        }

        void MoveRouted(Company c, double t, double dt)
        {
            // 退路の見積もりで損害が最も少ない方位へ逃げる。600m 以内に敵がいなければその場に止まる。
            if (!AwayFromNearbyEnemies(c, out _)) return;
            if (!c.FleeDirection.HasValue) RefreshEscape(c, t - dt);

            double heading = c.FleeDirection.Value;
            c.Facing = Angles.Wrap(heading);
            var next = c.Position + Vec2.FromAngle(heading) * (_p.Rout.FleeSpeedMps * dt);
            // 川に突き当たったら、その刻みは動かない（5秒ごとの見積もりで方位が変わる）
            if (_map.Contains(next) && !_map.Passable(next)) return;
            c.Position = next;
            c.MovedThisTick = true;

            if (!_map.Contains(c.Position))
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
        /// 目標選択：射程内で視線が通る敵のうち、正面の弧（±60°）にいる最も近い敵。いなければそれ以外で最も近い敵。
        /// 距離が同じなら ID の小さい方。壊走中・再集結中の中隊は撃たない。
        /// 拘束の命令中と、攻撃で発起点に着いた後は、命令の目標が射程内で見えていればその敵を撃つ。
        /// </summary>
        public Company SelectTarget(Company shooter)
        {
            if (!shooter.UnderCommand || shooter.Strength <= 0) return null;
            var forced = OrderTarget(shooter);
            if (forced != null && CanFireAt(shooter, forced)) return forced;

            Company bestFront = null, bestAny = null;
            double dFront = double.MaxValue, dAny = double.MaxValue;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == shooter.Side || e.Strength <= 0) continue;
                double d = Vec2.Distance(shooter.Position, e.Position);
                if (d > _p.Fire.RangeMaxM) continue;
                if (!LineOfSight.Visible(_map, _p.Sight, shooter.Position, e.Position)) continue;
                // 走査は ID 昇順なので、厳密に小さいときだけ更新すれば同距離は ID の小さい方が残る
                if (d < dAny) { dAny = d; bestAny = e; }
                if (AspectOf(shooter, e.Position) == Aspect.Front && d < dFront) { dFront = d; bestFront = e; }
            }
            return bestFront ?? bestAny;
        }

        /// <summary>
        /// 距離倍率 f_range。50m 以内は接近戦の 2.0、150m 以内は 1.3、400m までは 1.0、それ以遠は 0（撃たない）。
        /// 接近戦の 2.0 は 1.3 を置き換える（重ねない）。
        /// </summary>
        public double RangeMultiplier(double distance)
        {
            if (distance > _p.Fire.RangeMaxM) return 0.0;
            if (distance <= _p.CloseCombat.RadiusM) return _p.CloseCombat.RangeMult;
            if (distance <= _p.Fire.RangeCloseM) return _p.Fire.RangeCloseMult;
            return 1.0;
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

        void Resolve(List<Shot> shots, double t, double tickStart)
        {
            // 退路の見積もり：刻みの開始時点の状態で、全中隊分を先に行う（同時解決）。
            // 指揮下の中隊は士気がしきい値以下のときだけ（包囲の判定）、壊走中の中隊は逃げる方向のために見積もる
            foreach (var c in _companies)
            {
                if (!c.OnBoard || tickStart < c.NextEscapeCheckTime) continue;
                bool check = c.State == CompanyState.Routed
                             || (c.UnderCommand && c.Morale <= _p.Encirclement.CheckWhenMoraleAtOrBelow);
                if (check) RefreshEscape(c, tickStart);
            }

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
                bool close = s.Distance <= _p.CloseCombat.RadiusM;
                double fRange = RangeMultiplier(s.Distance);
                double fTerrain = TerrainDamageMult(tg);
                double fAspect = _p.Fire.AspectCasualtyMult[a];
                double fState = 1.0;
                if (u.State == CompanyState.Shaken) fState *= _p.Fire.ShakenMult;
                if (!s.TargetInFrontArc) fState *= _p.Fire.NonFrontTargetMult;
                // 砲撃による制圧（×0.5）は P3

                double fExtra = 1.0;
                if (tg.State == CompanyState.Routed && s.Distance <= _p.Rout.PursuitRadiusM) fExtra *= _p.Rout.PursuitCasualtyMult;
                if (tg.Disengaging) fExtra *= _p.Pinning.DisengageCasualtyMult;

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
                    SetEncircled(c, false, t);
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
                    // 包囲：士気がしきい値以下で、直近の見積もりで逃げ道がない（見積もりは5秒ごと。間は結果を保持）
                    bool enc = c.Morale <= _p.Encirclement.CheckWhenMoraleAtOrBelow && c.NoEscape;
                    SetEncircled(c, enc, t);
                    if (enc)
                    {
                        if (!c.DespairTaken)
                        {
                            c.DespairTaken = true;
                            double shock = _p.Encirclement.DespairShock * sens;
                            c.Ledger.Encircled -= shock;
                            total += shock;
                            if (_log.Enabled)
                                _log.Write(Ev(t, "despair", c).Add("morale_hit", shock).Add("escape_loss", c.EscapeLoss).ToString());
                        }
                        double d = _p.Encirclement.MoralePerMin * dtMin * sens;
                        if (c.Fortified) d *= _p.Encirclement.FortifiedMoraleMult;
                        c.Ledger.Encircled -= d;
                        total += d;
                    }
                    c.Pinned = pinnedNow[i];
                }
                else
                {
                    SetEncircled(c, false, t);
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

        void SetEncircled(Company c, bool value, double t)
        {
            if (c.Encircled == value) return;
            c.Encircled = value;
            if (!_log.Enabled) return;
            var ev = Ev(t, value ? "encircled_start" : "encircled_end", c);
            if (value) ev.Add("reason", "no_escape").Add("escape_loss", c.EscapeLoss);
            _log.Write(ev.ToString());
        }

        /// <summary>退路の見積もりの結果。</summary>
        public readonly struct EscapeEstimate
        {
            /// <summary>方位ごとの損害割合の見積もり。添字 k の方位は東から反時計回りに 360°×k÷方位数。</summary>
            public readonly double[] Loss;
            /// <summary>損害が最も少ない方位の添字（同点なら 600m 以内の敵の平均位置から最も遠ざかる方位）。</summary>
            public readonly int Best;

            public EscapeEstimate(double[] loss, int best)
            {
                Loss = loss;
                Best = best;
            }

            public double MinLoss => Loss[Best];
        }

        public double EscapeDirection(int k) => 2.0 * Math.PI * k / _p.Encirclement.EscapeDirs;

        /// <summary>
        /// 退路の見積もり。各方位について、壊走の速さで 500m 逃げる間（1秒刻み、1刻み目の位置から）、
        /// その時点で 400m 以内にいて視線が通る指揮下の敵すべてから背面を撃たれ続けたときに失う兵の割合の期待値を積算する。
        /// 視線は、敵ごとに10秒ごとに作る「各マスの中心が見えるか」を、その点のマスで引く。
        /// 川に突き当たる方位は逃げられない（損害 1.0）。地図の外へ出られる方位は、出るまでの損害で打ち切る。
        /// 射撃の基本の式（k・兵力・練度・距離倍率・背面倍率）だけを使い、射手の動揺・非正面・接近戦の補正は入れない。
        /// 敵は動かないものとし、乱数は使わない。
        /// </summary>
        public EscapeEstimate EstimateEscape(Company c)
        {
            var ep = _p.Encirclement;
            var foes = new List<Company>();
            foreach (var e in _companies)
                if (e.OnBoard && e.Side != c.Side && e.UnderCommand && e.Strength > 0) foes.Add(e);

            var rate = new double[foes.Count];
            for (int f = 0; f < foes.Count; f++)
                rate[f] = _p.Fire.KPerMin / 60.0 * foes[f].Strength * _p.Of(foes[f].Training).FireMult;

            int steps = (int)(ep.EscapeLenM / ep.EscapeSpeedMps / ep.EscapeStepSec);
            double stride = ep.EscapeSpeedMps * ep.EscapeStepSec;
            double aspectMult = _p.Fire.AspectCasualtyMult[(int)ep.EscapeFireAspect];
            var sight = new bool[foes.Count][];
            for (int f = 0; f < foes.Count; f++) sight[f] = VisibleCellsFrom(foes[f]);

            var loss = new double[ep.EscapeDirs];
            for (int k = 0; k < ep.EscapeDirs; k++)
            {
                var u = Vec2.FromAngle(EscapeDirection(k));
                double sum = 0;
                bool blocked = false;
                for (int s = 1; s <= steps; s++)
                {
                    var p = c.Position + u * (s * stride);
                    // 地図の外へ出られれば、そこで逃げ切り（出るまでの損害で打ち切る）
                    if (!_map.Contains(p)) break;
                    // 川に突き当たる方位は逃げられない
                    if (!_map.Passable(p)) { blocked = true; break; }
                    int cell = _map.IndexOf(p);
                    for (int f = 0; f < foes.Count; f++)
                    {
                        double d = Vec2.Distance(p, foes[f].Position);
                        if (d > _p.Fire.RangeMaxM || !sight[f][cell]) continue;
                        sum += rate[f] * (d <= _p.Fire.RangeCloseM ? _p.Fire.RangeCloseMult : 1.0);
                    }
                }
                loss[k] = blocked ? _p.MapRules.BlockedEscapeLoss : sum * aspectMult * ep.EscapeStepSec / Math.Max(1, c.Strength);
            }

            // 最小の方位。同点が複数あれば、600m 以内の敵の平均位置から最も遠ざかる方位（それも同じなら添字の小さい方）
            double min = double.MaxValue;
            foreach (double l in loss) min = Math.Min(min, l);
            const double tieEps = 1e-12;
            bool hasAway = AwayFromNearbyEnemies(c, out var away);
            int best = -1;
            double bestDot = double.NegativeInfinity;
            for (int k = 0; k < loss.Length; k++)
            {
                if (loss[k] > min + tieEps) continue;
                if (best < 0) { best = k; if (!hasAway) break; }
                double dot = hasAway ? Vec2.FromAngle(EscapeDirection(k)).Dot(away) : 0;
                if (dot > bestDot + tieEps) { bestDot = dot; best = k; }
            }
            return new EscapeEstimate(loss, best);
        }

        /// <summary>
        /// 600m 以内に盤上の敵がいれば true と、その中心の平均位置から自分へ向かう向き（遠ざかる向き）を返す。
        /// 平均位置と自分が重なるときは向きを零ベクトルにする。
        /// </summary>
        bool AwayFromNearbyEnemies(Company c, out Vec2 away)
        {
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
            away = n == 0 ? new Vec2(0, 0) : c.Position - new Vec2(sx / n, sy / n);
            return n > 0;
        }

        /// <summary>退路を見積もり直し、包囲の判定と逃げる方向を更新する。次の見積もりは <paramref name="now"/> から5秒後。</summary>
        void RefreshEscape(Company c, double now)
        {
            var est = EstimateEscape(c);
            c.EscapeLoss = est.MinLoss;
            c.NoEscape = est.MinLoss >= _p.Encirclement.EscapeLossThreshold;
            c.FleeDirection = EscapeDirection(est.Best);
            c.NextEscapeCheckTime = now + _p.Encirclement.CheckIntervalSec;
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
                        // 敵が近づいたら再び逃げる（連鎖のショックは与えない）。逃げる方向はその場で見積もり直す
                        c.State = CompanyState.Routed;
                        c.RallyClearSec = 0;
                        RefreshEscape(c, t);
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
            c.EntrenchProgress = 0;
            c.Path = null;
            if (!FirstRoutTime.HasValue) FirstRoutTime = t;
            // 士気50超から一度に壊走した場合など、見積もりがない（または古い）ときはその場で見積もって逃げる方向を決める
            if (!surrender && t - _p.TickSec >= c.NextEscapeCheckTime) RefreshEscape(c, t);

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
