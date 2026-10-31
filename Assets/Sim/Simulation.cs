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
        readonly ulong _seed;
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
            _seed = seed;
            _map = map;
            _log = log ?? NullEventSink.Instance;
            _snapshotEveryTicks = (int)Math.Round(SnapshotIntervalSec / p.TickSec);
            _exposureEveryTicks = (int)Math.Round(p.MapRules.ExposureRebuildSec / p.TickSec);
        }

        public void AddScript(ISimScript script) => _scripts.Add(script);

        /// <summary>載せた台本（表示用。読むだけ）。</summary>
        public IReadOnlyList<ISimScript> Scripts => _scripts;

        public Company AddCompany(string name, Side side, Training training, Personality personality,
            Vec2 position, double facingDeg, int? strength = null, double? morale = null)
        {
            var c = new Company(_companies.Count, name, side, training, personality,
                strength ?? _p.Movement.CompanyStrength, morale ?? _p.Morale.Initial,
                position, facingDeg * Angles.DegToRad);
            // 会戦の始めに置かれた中隊は、置かれた所で線を張り終えている（R3）
            c.StillSec = _p.Movement.LineFormedStillSec;
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

        /// <summary>
        /// 命令を伝令を使わずに渡す。次の刻みの最初に適用される（会戦開始前の命令、本部のない場面）。
        /// 伝令で運ぶ命令は <see cref="SendOrder"/>（P2a）。
        /// </summary>
        public void Issue(Order order) => IssueSheet(OrderSheet.Single(order));

        public void Step()
        {
            Tick++;
            double t = Time;
            double tickStart = t - _p.TickSec;

            if (Tick == 1) InitPositions();
            if ((Tick - 1) % _exposureEveryTicks == 0) RebuildVisibility();
            foreach (var s in _scripts) s.Update(this, tickStart);

            UpdateRunners(t, tickStart);
            ApplyOrders(t, tickStart);
            Move(t, tickStart);
            UpdateFortified();
            UpdateOccupation(t);
            var shots = SelectTargets();
            Resolve(shots, t, tickStart);
            UpdateSuppression(t);
            UpdateFacingReaction(t);
            Transitions(t);
            UpdateStages(t);
            UpdateAutonomy(t);
            UpdateDefense(t);
            UpdateFormation(t);
            UpdateDispatches(t);

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
                c.ShakenHalt = false;
                c.HaltedOrder = null;
                // 段の裁量を写し、自律行動・独断をやめる（P2c）
                c.Discretion = DiscretionOf(c, o);
                ClearAutonomy(c);
                // 動揺中は前進・攻撃を拒み、その場で保持する（拒む士気は性格と裁量で決まる。P2c）
                // 目的地のある陣地構築も移動を伴うので、同じく拒む
                if (RefusesAdvance(c) && (order.Type == OrderType.Advance || order.Type == OrderType.Attack
                                          || (order.Type == OrderType.Entrench && order.Destination.HasValue)))
                {
                    order = Order.Hold(c.Id, order.FacingRad);
                    // 拒んだ段は捨てずに「動揺で停止中」とし、正常に戻ったら始める（P2b.2）
                    c.ShakenHalt = true;
                    c.HaltedOrder = o;
                }

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
                // 持ち場：動かない命令は今の位置。動く命令は着いて止まった位置（UpdateAutonomy で決める。P2c）
                c.PostSettled = !c.CurrentOrder.Moves || c.Arrived;
                if (c.PostSettled) c.PostPoint = c.Position;
            }
        }

        // ---------------------------------------------------------------- 2. 移動と旋回

        void Move(double t, double tickStart)
        {
            double dt = _p.TickSec;
            double freeTurn = _p.Movement.TurnDegPerSec * Angles.DegToRad * dt;
            double engagedTurn = _p.Movement.TurnDegPerSecEngaged * Angles.DegToRad * dt;
            // 接敵と隊列の超越は、全中隊が動く前の位置でまとめて判定する（処理の順番で結果が変わらないように。P1e）
            UpdateContactAndPassing();

            foreach (var c in _companies)
            {
                c.MovedThisTick = false;
                c.TurningAsMoving = false;
                c.MoveStyle = MoveStyle.None;
                c.BlockedByEnemy = false;
                if (!c.OnBoard) continue;

                if (c.State == CompanyState.Routed)
                {
                    MoveRouted(c, t, dt);
                    continue;
                }
                if (c.State == CompanyState.Rallying) continue;

                var before = c.Position;
                // 線を張った中隊が接敵中に正面を変えるときは、正面の幅から決まる遅い速さ（R3）。線を張る前は速い
                bool lineFormed = c.StillSec >= _p.Movement.LineFormedStillSec - 1e-9;
                // 縦隊はいつもふつうの速さで回る（F1）
                double maxTurn = c.Engaged && lineFormed && !ColumnShape(c) ? engagedTurn : freeTurn;
                _turnedFromDeg = 0;
                // 制圧下で動こうとしている中隊は、伏せて止まる（R1。解けたら同じ段のまま動きを続ける）
                if (PinnedDown(c)) LieDown(c, maxTurn);
                else MoveUnderCommand(c, t, tickStart, dt, maxTurn);
                // 敵の矩形との隙間が 10m 未満になる手前で止まる（P1d の2）。全く進めなくても「着いた」扱いにはせず、
                // 30秒ごとの経路の引き直しを続け、道が空けば進む（P1e.1）
                c.BlockedByEnemy = ClampAgainstEnemies(c, before) && c.Position.X == before.X && c.Position.Y == before.Y
                                   && c.CurrentOrder.Moves && !c.Arrived;
                c.MovedThisTick = c.Position.X != before.X || c.Position.Y != before.Y;
                // 接敵中にその場で 20° を超えて向きを変えている間は、R1 の動いている扱い（R3）
                c.TurningAsMoving = _p.Movement.TurningCountsAsMoving && c.Engaged && lineFormed && !c.MovedThisTick &&
                                    _turnedFromDeg > _p.Movement.TurningSlowDeg + 1e-9;
            }

            // 線を張っているかは、自分の動きで止まっている長さで決める（押し離しの前に数える。R3）
            foreach (var c in _companies)
                c.StillSec = c.OnBoard && !c.MovedThisTick ? c.StillSec + dt : 0;

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
            // 味方と重なっている間は遅くなる（すり抜け。P1d の6）
            double speed = _p.Rout.FleeSpeedMps * (OverlapsFriend(c, out _) ? _p.Behavior.RoutPassSpeedMult : 1.0);
            var next = c.Position + Vec2.FromAngle(heading) * (speed * dt);
            // 川に突き当たったら、その刻みは動かない（5秒ごとの見積もりで方位が変わる）
            if (_map.Contains(next) && !_map.Passable(next)) return;
            var before = c.Position;
            c.Position = next;
            // 敵はすり抜けられない（ふさがれたら止まる。P1d の2）
            ClampAgainstEnemies(c, before);
            if (c.Position.X == before.X && c.Position.Y == before.Y) return;
            c.MovedThisTick = true;
            ApplyPassThrough(c, t);

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

        /// <summary>この刻みに、向きを変え始めた時点での向きと目標の向きの差（度）の最大（R3 の旋回中の判定）。</summary>
        double _turnedFromDeg;

        double TurnToward(double facing, double target, double maxTurn)
        {
            double diff = Angles.Wrap(target - facing);
            if (Math.Abs(diff) > 1e-9) _turnedFromDeg = Math.Max(_turnedFromDeg, Math.Abs(diff) * Angles.RadToDeg);
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
            /// <summary>接近戦（互いの矩形の隙間が 50m 以内。P1g.1）。</summary>
            public bool Close;
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
                if (!SightForFire(shooter, e)) continue; // 接触中は視線を問わない（P1g）
                // 走査は ID 昇順なので、厳密に小さいときだけ更新すれば同距離は ID の小さい方が残る
                if (d < dAny) { dAny = d; bestAny = e; }
                if (AspectOf(shooter, e.Position) == Aspect.Front && d < dFront) { dFront = d; bestFront = e; }
            }
            return bestFront ?? bestAny;
        }

        /// <summary>
        /// 距離倍率 f_range。接近戦（<paramref name="closeCombat"/>）は 2.0、中心どうし 150m 以内は 1.3、400m までは 1.0、
        /// それ以遠は 0（撃たない）。接近戦の 2.0 は 1.3 を置き換える（重ねない）。
        /// </summary>
        public double RangeMultiplier(double distance, bool closeCombat)
        {
            if (distance > _p.Fire.RangeMaxM) return 0.0;
            if (closeCombat) return _p.CloseCombat.RangeMult;
            if (distance <= _p.Fire.RangeCloseM) return _p.Fire.RangeCloseMult;
            return 1.0;
        }

        /// <summary>
        /// 接近戦か：互いの矩形の隙間が <c>close_combat.radius_m</c>（50m）以内（P1g.1。P1g までは中心どうしの距離で測っていた）。
        /// </summary>
        public bool InCloseCombat(Company a, Company b)
        {
            double r = _p.CloseCombat.RadiusM;
            return UnitRect.Gap(RectOf(a), RectOf(b), r + 1) <= r;
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
                    Close = InCloseCombat(c, target),
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
            // 受けている火力の量（R1。守りを除いた損害の式の和、毎分）と、一番多い射手
            var fireAmount = new double[n];
            var mainAmount = new double[n];
            var mainShooter = new int[n];
            for (int i = 0; i < n; i++) mainShooter[i] = -1;
            for (int i = 0; i < n; i++)
                for (int a = 0; a < 3; a++)
                    firstShooter[i, a] = -1;

            // 全射撃の損害を、刻みの開始時点の状態から計算してバッファに積む
            _fireLog.Clear();
            foreach (var s in shots)
            {
                var u = _companies[s.Shooter];
                var tg = _companies[s.Target];
                int a = (int)s.AspectOnTarget;

                double fQuality = _p.Of(u.Training).FireMult;
                bool close = s.Close;
                double fRange = RangeMultiplier(s.Distance, close);
                double fTerrain = TerrainDamageMult(tg, u.Position, close);
                // 陣地の向きの正面の弧の外から撃たれた（E1 の迂回の判定）
                if (D.Enabled && tg.Role != DefenseRole.None && !InFortificationArc(PositionFacing(tg), tg, u.Position)) tg.LastOffArcFireTime = t;
                double fAspect = _p.Fire.AspectCasualtyMult[a];
                // 射手の状態（動揺・正面の弧の外の目標・隊形の乱れ・制圧下・動きながら撃つ）。表示と同じ関数（R2.1）
                double fState = FireStateMult(u, s.TargetInFrontArc, t);

                double fExtra = 1.0;
                if (tg.State == CompanyState.Routed && s.Distance <= _p.Rout.PursuitRadiusM) fExtra *= _p.Rout.PursuitCasualtyMult;
                if (tg.Disengaging) fExtra *= _p.Pinning.DisengageCasualtyMult;

                double lambda = _p.Fire.KPerMin * u.Strength * fQuality * fRange * fTerrain * fAspect * fState * fExtra * dtMin;
                int c = _rng.NextPoisson(lambda);

                double amount = _p.Fire.KPerMin * u.Strength * fQuality * fRange * fAspect * fState * fExtra;
                fireAmount[tg.Id] += amount;
                if (amount > mainAmount[tg.Id]) { mainAmount[tg.Id] = amount; mainShooter[tg.Id] = u.Id; }

                cas[tg.Id] += c;
                double m = (double)c / tg.InitialStrength * 100.0 * _p.Morale.PerPctLoss * _p.Morale.AspectMoraleMult[a];
                if (close) m *= _p.CloseCombat.MoraleMult;
                _fireLog.Add(new FireRecord(u.Id, tg.Id, s.AspectOnTarget, lambda / dtMin, c, m, s.Distance));
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
                c.IncomingFirePerMin = fireAmount[i];
                c.MainShooterId = mainShooter[i];
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
        public EscapeEstimate EstimateEscape(Company c) => EstimateEscape(c, VisibleCellsFrom);

        EscapeEstimate EstimateEscape(Company c, Func<Company, bool[]> visibleCellsFrom)
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
            for (int f = 0; f < foes.Count; f++) sight[f] = visibleCellsFrom(foes[f]);
            // 接触の距離（隙間 50m 以内）の敵は、視線を問わず撃ってくる敵に数える（P2c.1.2）。矩形どうしの隙間を見るのは、中心どうしが届きうる距離のときだけ
            var foeRects = new UnitRect[foes.Count];
            for (int f = 0; f < foes.Count; f++) foeRects[f] = RectOf(foes[f]);
            double contactReach = 2 * RectAt(c.Position, 0).Radius + B.SightContactGapM + 1;

            // 敵の矩形（降伏を除く）。逃げる中隊の矩形との隙間が 10m 未満に詰まる方位は、そこで逃げられない（P1d の2。動きの止め方と同じ）
            var blockers = new List<UnitRect>();
            foreach (var e in _companies)
                if (BlocksAsEnemy(e, c)) blockers.Add(RectOf(e));
            double minGap = _p.Behavior.EnemyMinGapM;

            var loss = new double[ep.EscapeDirs];
            for (int k = 0; k < ep.EscapeDirs; k++)
            {
                double dirRad = EscapeDirection(k);
                var u = Vec2.FromAngle(dirRad);
                double sum = 0;
                bool blocked = false;
                int blockStep = FirstEnemyBlockedStep(c, blockers, u, dirRad, stride, steps, minGap);
                for (int s = 1; s <= steps; s++)
                {
                    var p = c.Position + u * (s * stride);
                    // 地図の外へ出られれば、そこで逃げ切り（出るまでの損害で打ち切る）
                    if (!_map.Contains(p)) break;
                    // 川に突き当たる方位は逃げられない
                    if (!_map.Passable(p)) { blocked = true; break; }
                    // 敵の矩形に突き当たる方位も逃げられない（離れていく分には構わない）
                    if (s >= blockStep) { blocked = true; break; }
                    int cell = _map.IndexOf(p);
                    for (int f = 0; f < foes.Count; f++)
                    {
                        double d = Vec2.Distance(p, foes[f].Position);
                        if (d > _p.Fire.RangeMaxM) continue;
                        if (!sight[f][cell] && !(d <= contactReach && InContactAt(p, dirRad, foeRects[f]))) continue;
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
        /// その方位へ逃げたとき、敵の矩形に 10m より詰まる最初の点の番号（1〜）。詰まらなければ steps+1。
        /// 矩形どうしの隙間の計算は重いので、5点おきに調べ、詰まった所から1点ずつ戻って最初の点を探す。
        /// 届く範囲（逃げる距離＋両方の矩形の大きさ）に敵がいなければ調べない。
        /// </summary>
        int FirstEnemyBlockedStep(Company c, List<UnitRect> blockers, Vec2 u, double dirRad, double stride, int steps, double minGap)
        {
            if (blockers.Count == 0) return steps + 1;
            double reach = steps * stride + 2 * blockers[0].Radius + minGap + 1;
            bool any = false;
            foreach (var r in blockers)
                if (Vec2.Distance(c.Position, r.Center) <= reach) { any = true; break; }
            if (!any) return steps + 1;

            // 敵ごとに、今の位置での隙間（ここから詰まる方位がふさがる）
            var here = RectAt(c.Position, dirRad);
            var gap0 = new double[blockers.Count];
            for (int b = 0; b < blockers.Count; b++) gap0[b] = UnitRect.Gap(here, blockers[b], minGap + 1);
            bool At(int s) => BlockedByEnemy(blockers, gap0, c.Position, RectAt(c.Position + u * (s * stride), dirRad), minGap);

            const int coarse = 5;
            int prev = 0;
            for (int s = coarse; ; s += coarse)
            {
                int k = Math.Min(s, steps);
                if (At(k))
                {
                    for (int j = prev + 1; j < k; j++)
                        if (At(j)) return j;
                    return k;
                }
                if (k == steps) return steps + 1;
                prev = k;
            }
        }

        /// <summary>
        /// 逃げる中隊の矩形 <paramref name="me"/> が、どれかの敵の矩形に 10m より詰まるか：隙間が 10m 未満で、出発点より狭まる
        /// （接していて隙間 0 のままなら、中心が近づく）。離れていく動きはふさがない。
        /// </summary>
        static bool BlockedByEnemy(List<UnitRect> rects, double[] gap0, Vec2 from, in UnitRect me, double minGap)
        {
            for (int b = 0; b < rects.Count; b++)
            {
                double gap = UnitRect.Gap(me, rects[b], minGap + 1);
                if (gap >= minGap) continue;
                if (gap < gap0[b]) return true;
                if (gap <= 1e-9 && gap0[b] <= 1e-9
                    && Vec2.Distance(me.Center, rects[b].Center) < Vec2.Distance(from, rects[b].Center)) return true;
            }
            return false;
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
            // 揺れ止め（P1d の7）：壊走中は、新しい最小の方位の見積もり損害が今の方位より 0.05 以上小さいときだけ替える
            int k = est.Best;
            if (c.State == CompanyState.Routed && c.FleeDirIndex >= 0
                && est.Loss[c.FleeDirIndex] - est.MinLoss < _p.Behavior.FleeSwitchMinGain)
                k = c.FleeDirIndex;
            c.FleeDirIndex = k;
            c.FleeDirection = EscapeDirection(k);
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
                        // 動揺した拘束は詰めるのをやめ、その場で目標を向いて撃つ（P1d の3）
                        if (want == CompanyState.Shaken && !c.Arrived && c.CurrentOrder.Type == OrderType.Pin)
                        {
                            c.Arrived = true;
                            c.Path = null;
                        }
                        if (_log.Enabled) LogStateChange(c, prev, t);
                    }
                    UpdateRefusal(c, t);
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
                        c.FleeDirIndex = -1; // 前の方位は古いので、揺れ止めを掛けずに選び直す
                        RefreshEscape(c, t);
                        if (_log.Enabled) LogStateChange(c, CompanyState.Rallying, t);
                    }
                    else if (c.Morale >= _p.Rout.RallyReturnMorale)
                    {
                        c.State = c.Morale <= _p.Morale.ShakenThreshold ? CompanyState.Shaken : CompanyState.Normal;
                        c.CurrentOrder = Order.Hold(c.Id);
                        c.Arrived = true;
                        // 持ち場はその場（P2c）
                        ClearAutonomy(c);
                        c.PostPoint = c.Position;
                        c.PostSettled = true;
                        // 預かっている命令書があれば、練度の遅れの後に第1段を始める。なければ命令待ち（P2b.2）
                        c.ActiveSheet = null;
                        c.ShakenHalt = false;
                        c.HaltedOrder = null;
                        if (c.HeldSheet != null)
                        {
                            c.ActiveSheet = c.HeldSheet;
                            c.HeldSheet = null;
                            c.StageIndex = 0;
                            c.StageStarted = false;
                            c.StageCompleted = false;
                            c.StartWaitOrder = c.ActiveSheet.First;
                            c.StartWaitAt = t + _p.Of(c.Training).OrderStartSec;
                            c.AwaitingWhenDelivered = true;
                        }
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
            // 壊走した時点で、実行中の命令書（残りの段を含む）は中断する（P2b.2）。命令書のない中隊はログに何も出さない
            if (c.ActiveSheet != null || c.StartWaitOrder != null)
            {
                if (_log.Enabled) _log.Write(Ev(t, "sheet_aborted", c).Add("side", SideName(c.Side)).ToString());
                c.ActiveSheet = null;
                c.StageIndex = 0;
                c.StageStarted = false;
                c.StartWaitOrder = null;
            }
            c.ShakenHalt = false;
            c.HaltedOrder = null;
            ClearAutonomy(c);
            c.ScrapeProgress = 0;
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

            // 隣の崩壊の報告（P2c.2。情報だけ）
            foreach (var f in _companies)
                if (f != c && f.Side == c.Side && f.OnBoard && Vec2.Distance(f.Position, c.Position) <= CP.NeighborRoutRadiusM)
                    NoteDispatch(f, DispatchKind.NeighborRouted, t, c);

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
                .AddIf(c.Formation != Formation.Line, "formation", "column")
                .AddIf(c.FormationChange != FormationChange.None, "formation_change",
                    c.FormationChange == FormationChange.Deploying ? "deploying" : "forming_column")
                .ToString());
        }
    }
}
