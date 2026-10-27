using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>守りの計画での中隊の役目（E1）。</summary>
    public enum DefenseRole { None, Front, Garrison, Reserve }

    /// <summary>守りの計画の、中隊ひとつ分（E1。地図ファイルの defense_plan.units）。</summary>
    public sealed class DefenseUnitPlan
    {
        public DefenseRole Role;
        /// <summary>前線：後退先と、着いた後の向き（度。0＝東）。</summary>
        public Vec2 Fallback;
        public double FallbackFacingDeg;
        /// <summary>予備：戻る位置と、逆襲の引き金の円。</summary>
        public Vec2 Post;
        public Vec2 TriggerCenter;
        public double TriggerRadiusM;
    }

    /// <summary>守りの計画（E1。地図ファイルの defense_plan）：守る地点、集結点、中隊ごとの役目。</summary>
    public sealed class DefensePlan
    {
        public Vec2 ObjectiveCenter;
        public Vec2 Assembly;
        public readonly Dictionary<string, DefenseUnitPlan> Units = new Dictionary<string, DefenseUnitPlan>();
    }

    /// <summary>予備の状態（E1 の規則2）。</summary>
    public enum ReserveState { Waiting, Attacking, AfterAttack, Returning, Spent }

    // E1：赤の守り方（最小版）（仕様「E1：赤の守り方（最小版）（確定）」）と、C12 の占領の乱れ。
    // 前線の後退と即座の逆襲は中隊の判断（伝令なし）、奪回は赤の本部の判断（伝令で届く）。赤が判断に使うのは、
    // 赤の中隊のどれかから視線が通る（気付いている）青だけ。
    public sealed partial class Simulation
    {
        SimParams.DefenseParams D => _p.Defense;

        DefensePlan _defense;
        Side _defenseSide = Side.Red;
        double _nextHqEval;

        /// <summary>奪回の1回分（E1 の規則3）。</summary>
        sealed class Retake
        {
            public int Id;
            public double StartTime;
            public readonly List<int> Ordered = new List<int>();
            public bool Active;
            public bool Launched;
            public readonly HashSet<int> Attacking = new HashSet<int>();
        }

        readonly List<Retake> _retakes = new List<Retake>();
        Retake ActiveRetake => _retakes.Count > 0 && _retakes[_retakes.Count - 1].Active ? _retakes[_retakes.Count - 1] : null;

        public DefensePlan DefensePlan => _defense;

        /// <summary>守りの計画を載せる（E1）。計画にある中隊に役目を付ける。defense.enabled のときだけ動く。</summary>
        public void SetDefensePlan(DefensePlan plan, Side side = Side.Red)
        {
            _defense = plan;
            _defenseSide = side;
            foreach (var c in _companies)
                if (c.Side == side && plan.Units.TryGetValue(c.Name, out var u)) c.Role = u.Role;
        }

        DefenseUnitPlan PlanOf(Company c) => _defense != null && _defense.Units.TryGetValue(c.Name, out var u) ? u : null;

        // ---------------------------------------------------------------- 見えている敵

        /// <summary>陣営 <paramref name="side"/> の中隊のどれか（指揮下）から気付かれている（視線が通るか接触の距離）か。</summary>
        public bool SeenBySide(Company e, Side side)
        {
            foreach (var r in _companies)
                if (r.Side == side && r.OnBoard && r.UnderCommand && Aware(r, e)) return true;
            return false;
        }

        /// <summary>赤（守りの側）から見えている、指揮下の敵の中隊。</summary>
        IEnumerable<Company> SeenEnemies(Side side)
        {
            foreach (var e in _companies)
                if (e.Side != side && e.OnBoard && e.UnderCommand && SeenBySide(e, side)) yield return e;
        }

        /// <summary>中隊が今いる陣地の向き。陣地がなければ中隊の向き（E1 の迂回・拘束の判定）。</summary>
        double PositionFacing(Company c) => FortificationFacingOf(c) ?? c.Facing;

        /// <summary>
        /// 拘束されているか（E1）：見えている指揮下の敵が、向き <paramref name="facing"/> の正面の弧（±fire.front_arc_deg）の中で
        /// defense.pin_radius_m（400m）以内にいる。
        /// </summary>
        bool HeldByFront(Company c, double facing)
        {
            foreach (var e in SeenEnemies(c.Side))
            {
                var d = e.Position - c.Position;
                if (d.Length > D.PinRadiusM + 1e-9) continue;
                double deg = Math.Abs(Angles.Wrap(Math.Atan2(d.Y, d.X) - facing)) * Angles.RadToDeg;
                if (deg <= _p.Fire.FrontArcDeg + 1e-9) return true;
            }
            return false;
        }

        /// <summary>
        /// 迂回されたか（E1 の規則1 の A）：見えている指揮下の敵が 600m 以内で陣地の線より 50m 以上後ろにいる（理由の方角を返す）、
        /// または直近 30秒に陣地の正面の弧の外から撃たれた（"flank_fire"）。どちらでもなければ null。
        /// </summary>
        string BypassReason(Company c, double t)
        {
            double f = PositionFacing(c);
            var fwd = Vec2.FromAngle(f);
            foreach (var e in SeenEnemies(c.Side))
            {
                var d = e.Position - c.Position;
                if (d.Length > D.BypassRadiusM + 1e-9) continue;
                if (d.Dot(fwd) > -D.BypassBehindM + 1e-9) continue;
                // どの方角へ回られたか（自分から見た敵の方角）
                return CompassName(Math.Atan2(d.Y, d.X));
            }
            if (t - c.LastOffArcFireTime <= D.FlankFireMemorySec + 1e-9) return "flank_fire";
            return null;
        }

        static string CompassName(double rad)
        {
            string[] names = { "east", "northeast", "north", "northwest", "west", "southwest", "south", "southeast" };
            int k = (int)Math.Round((rad * Angles.RadToDeg % 360 + 360) % 360 / 45.0) % 8;
            return names[k];
        }

        /// <summary>予備の余力（E1）：士気が morale.shaken_threshold より上で、損害が初めの兵力の defense.reserve_spent_loss 未満。</summary>
        public bool HasReserveStrength(Company c) =>
            c.Morale > _p.Morale.ShakenThreshold && c.LossRate < D.ReserveSpentLoss - 1e-12;

        bool NormalUnderCommand(Company c) => c.OnBoard && c.UnderCommand && c.State == CompanyState.Normal;

        // ---------------------------------------------------------------- 中隊が自分に出す命令

        /// <summary>
        /// 中隊が自分の判断で命令書を始める（伝令なし、判断枠なし）。伝令が着いたときと同じく、練度の命令開始の遅れの後に始める。
        /// </summary>
        void SelfOrder(Company c, OrderSheet sheet, double t)
        {
            c.ShakenHalt = false;
            c.HaltedOrder = null;
            c.ReceivedSeq = ++_orderSeq;
            c.ActiveSheet = sheet;
            c.StageIndex = 0;
            c.StageStarted = false;
            c.StageCompleted = false;
            c.StartWaitOrder = sheet.First;
            c.StartWaitAt = t + _p.Of(c.Training).OrderStartSec;
        }

        /// <summary>守る地点の方角（ラジアン）。</summary>
        double FacingToObjective(Vec2 from)
        {
            var d = _defense.ObjectiveCenter - from;
            return d.Length < 1e-9 ? 0 : Math.Atan2(d.Y, d.X);
        }

        // ---------------------------------------------------------------- 毎刻みの判断

        /// <summary>E1 の守り方（刻みの終わり）。defense.enabled で、守りの計画があるときだけ。</summary>
        void UpdateDefense(double t)
        {
            if (!D.Enabled || _defense == null) return;
            UpdateAssaults(t);
            foreach (var c in _companies)
            {
                // 一度でも壊走したら、役目を外す（3回目の直しの1。奪回には命じられる）
                if (c.Side == _defenseSide && c.Role != DefenseRole.None && c.State == CompanyState.Routed)
                {
                    if (_log.Enabled) _log.Write(Ev(t, "defense_role_lost", c).Add("side", SideName(c.Side)).Add("role", RoleName(c.Role)).ToString());
                    c.Role = DefenseRole.None;
                }
                if (c.Side != _defenseSide || !c.OnBoard || !c.UnderCommand) continue;
                // 奪回で命じられている間は、本部の命令に従う
                if (InActiveRetake(c)) continue;
                if (t < c.NextDefenseCheck - 1e-9) continue;
                c.NextDefenseCheck = t + D.CheckSec;
                if (c.Role == DefenseRole.Front) UpdateFront(c, t);
                else if (c.Role == DefenseRole.Reserve) UpdateReserve(c, t);
            }
            UpdateRetake(t);
        }

        // ---- 突撃（E1 の報告の後に決めたこと 1）

        /// <summary>
        /// 突撃を始める：目標の中隊の今いる位置への前進（S7 と同じ形。接近戦の距離まで詰める）。伝令なしで、練度の遅れの後に動く。
        /// </summary>
        void Assault(Company c, Company tgt, double t, bool counter)
        {
            var sheet = OrderSheet.Single(AssaultOrder(c, tgt));
            c.AssaultTargetId = tgt.Id;
            c.AssaultIsCounter = counter;
            ResetStall(c, tgt, t);
            c.AssaultSheet = sheet;
            c.NextAssaultReplan = t + D.CheckSec;
            SelfOrder(c, sheet, t);
        }

        static string RoleName(DefenseRole r) =>
            r == DefenseRole.Front ? "front" : r == DefenseRole.Garrison ? "garrison" : r == DefenseRole.Reserve ? "reserve" : "none";

        Order AssaultOrder(Company c, Company tgt)
        {
            var d = tgt.Position - c.Position;
            return Order.Advance(c.Id, tgt.Position, d.Length < 1e-9 ? c.Facing : Math.Atan2(d.Y, d.X));
        }

        /// <summary>
        /// 突撃の途中（毎刻み）：目標が指揮下でなくなったら、その場で確保（保持）する。目標が動いたら、5秒ごとに目標の位置へ引き直す
        /// （命令は出し直さないので、練度の遅れは掛からない）。突撃の命令書が始まる前（伝令が着く前、練度の遅れの間）は待つ。
        /// 追う限り（直しの後に決めたこと 2）：即座の逆襲は、目標の中心が引き金の円の外に出たら、やめて戻る位置へ戻る。奪回の攻撃は、
        /// 目標が村のマスから出たら、村のマスにいるほかの敵へ目標を移し、いなければ村の中心へ前進して保持する。
        /// </summary>
        void UpdateAssaults(double t)
        {
            foreach (var c in _companies)
            {
                if (c.AssaultTargetId < 0) continue;
                if (!c.OnBoard || !c.UnderCommand)
                {
                    c.AssaultTargetId = -1;
                    continue;
                }
                if (!ReferenceEquals(c.ActiveSheet, c.AssaultSheet))
                {
                    // ほかの命令書に替わった
                    if (c.ActiveSheet != null && c.StageStarted) c.AssaultTargetId = -1;
                    continue;
                }
                var tgt = _companies[c.AssaultTargetId];
                if (c.StartWaitOrder != null || !c.StageStarted)
                {
                    // 動き出すまで（伝令・練度の遅れ）は、止められた突撃の時計を進めない
                    ResetStall(c, tgt, t);
                    continue;
                }
                if (!tgt.OnBoard || !tgt.UnderCommand)
                {
                    c.AssaultTargetId = -1;
                    HoldInPlace(c);
                    c.StageCompleted = true;
                    if (_log.Enabled) _log.Write(Ev(t, "defense_assault_done", c).Add("side", SideName(c.Side)).Add("target", tgt.Name).ToString());
                    continue;
                }
                // 止められた突撃はやめる（3回目の直しの3）
                double dist = Vec2.Distance(c.Position, tgt.Position);
                if (dist < c.AssaultBestDist - D.AssaultStallProgressM - 1e-9) { c.AssaultBestDist = dist; c.AssaultBestTime = t; }
                string stop = !HasReserveStrength(c) ? "spent" : t - c.AssaultBestTime >= D.AssaultStallSec - 1e-9 ? "stalled" : null;
                if (stop != null)
                {
                    StopAssault(c, tgt, stop, t);
                    continue;
                }
                if (t < c.NextAssaultReplan - 1e-9) continue;
                c.NextAssaultReplan = t + D.CheckSec;
                if (c.AssaultIsCounter)
                {
                    var plan = PlanOf(c);
                    if (plan != null && Vec2.Distance(tgt.Position, plan.TriggerCenter) > plan.TriggerRadiusM + 1e-9)
                    {
                        if (_log.Enabled)
                            _log.Write(Ev(t, "defense_assault_stop", c).Add("side", SideName(c.Side)).Add("target", tgt.Name)
                                .Add("kind", "counter").Add("reason", "left_trigger").ToString());
                        ReturnToPost(c, plan, t);
                        c.Reserve = ReserveState.Returning;
                        continue;
                    }
                }
                else if (_map.TerrainAt(tgt.Position) != TerrainMap.Village)
                {
                    var next = RetakeTarget();
                    if (next == null)
                    {
                        // 村に敵がいない：村の中心へ前進して保持する（村に入って守る）
                        c.AssaultTargetId = -1;
                        var d = _defense.ObjectiveCenter - c.Position;
                        StartOrder(c, Order.Advance(c.Id, _defense.ObjectiveCenter, d.Length < 1e-9 ? c.Facing : Math.Atan2(d.Y, d.X)), t, t);
                        if (_log.Enabled) _log.Write(Ev(t, "retake_occupy", c).Add("side", SideName(c.Side)).ToString());
                        continue;
                    }
                    if (next != tgt && _log.Enabled)
                        _log.Write(Ev(t, "retake_retarget", c).Add("side", SideName(c.Side)).Add("target", next.Name).ToString());
                    c.AssaultTargetId = next.Id;
                    if (next != tgt) ResetStall(c, next, t);
                    tgt = next;
                }
                var o = c.CurrentOrder;
                if (o.Type != OrderType.Advance || !o.Destination.HasValue) continue;
                if (Vec2.Distance(o.Destination.Value, tgt.Position) < 1.0) continue;
                StartOrder(c, AssaultOrder(c, tgt), t, t);
            }
        }

        void ResetStall(Company c, Company tgt, double t)
        {
            c.AssaultBestDist = Vec2.Distance(c.Position, tgt.Position);
            c.AssaultBestTime = t;
        }

        /// <summary>
        /// 止められた突撃をやめる（3回目の直しの3）：余力がなくなった（"spent"）か、defense.assault_stall_sec の間に目標との距離が
        /// defense.assault_stall_progress_m 以上縮まらない（"stalled"）。
        /// 即座の逆襲：進めずにやめたら、その場で目標の方を向いて保持し、「攻撃が終わった後」に移る。やめた時の目標へは
        /// defense.assault_stall_sec の間は出直さない（4回目の直しの1）。余力がなくてやめたら、戻る位置へ戻る（以後は逆襲しない）。
        /// 奪回：その場で保持する（本部が条件を見て出し直す）。
        /// </summary>
        void StopAssault(Company c, Company tgt, string reason, double t)
        {
            bool counter = c.AssaultIsCounter;
            c.AssaultTargetId = -1;
            if (_log.Enabled)
                _log.Write(Ev(t, "defense_assault_stop", c).Add("side", SideName(c.Side)).Add("target", tgt.Name)
                    .Add("kind", counter ? "counter" : "retake").Add("reason", reason).ToString());
            var plan = PlanOf(c);
            if (counter && plan != null && c.Role == DefenseRole.Reserve && reason == "spent")
            {
                ReturnToPost(c, plan, t);
                c.Reserve = ReserveState.Spent;
                return;
            }
            var d = tgt.Position - c.Position;
            c.CurrentOrder = Order.Hold(c.Id, d.Length < 1e-9 ? c.Facing : Math.Atan2(d.Y, d.X));
            c.Arrived = true;
            c.Path = null;
            c.StageCompleted = true;
            if (counter && c.Role == DefenseRole.Reserve)
            {
                c.Reserve = ReserveState.AfterAttack;
                c.CounterBannedTargetId = tgt.Id;
                c.CounterBannedUntil = t + D.AssaultStallSec;
            }
        }

        // ---- 規則1：前線の後退

        void UpdateFront(Company c, double t)
        {
            var plan = PlanOf(c);
            if (plan == null) return;
            if (c.FellBack)
            {
                // 着いたら、後退先の向きで保持し、役目を守備に変える
                if (c.CurrentOrder.Type == OrderType.Retreat && c.Arrived && c.StartWaitOrder == null)
                {
                    c.Role = DefenseRole.Garrison;
                    if (_log.Enabled) _log.Write(Ev(t, "defense_fallback_done", c).Add("side", SideName(c.Side)).ToString());
                }
                return;
            }
            if (c.State != CompanyState.Normal || c.Suppressed) return;
            // 陣地（初めの位置の autonomy.post_radius_m 以内）にいる間だけ判断する（3回目の直しの1）
            if (Vec2.Distance(c.Position, c.InitialPosition) > _p.Autonomy.PostRadiusM + 1e-9) return;
            string reason = BypassReason(c, t);
            if (reason == null) return;
            if (HeldByFront(c, PositionFacing(c))) return;
            c.FellBack = true;
            SelfOrder(c, OrderSheet.Single(Order.Retreat(c.Id, plan.Fallback, plan.FallbackFacingDeg * Angles.DegToRad)), t);
            if (_log.Enabled)
                _log.Write(Ev(t, "defense_fallback", c).Add("side", SideName(c.Side)).Add("reason", reason)
                    .Add("x", plan.Fallback.X).Add("y", plan.Fallback.Y).ToString());
        }

        // ---- 規則2：即座の逆襲

        /// <summary>
        /// 引き金の円の中の、見えている指揮下の敵のうち、弱っていて（占領の乱れ・動揺・制圧下）孤立している（300m 以内にほかの指揮下の
        /// 敵が1個以下）中隊のうち、守る地点に最も近い中隊（E1 の報告の後に決めたこと 4。強い所へは出ない）。
        /// </summary>
        Company ReserveTarget(Company c, DefenseUnitPlan plan, double t)
        {
            Company best = null;
            double bestD = double.MaxValue;
            foreach (var e in SeenEnemies(c.Side))
            {
                if (!CounterTargetable(e, plan)) continue;
                // 進めずにやめた目標へは、defense.assault_stall_sec の間は出直さない（4回目の直しの1）
                if (e.Id == c.CounterBannedTargetId && t < c.CounterBannedUntil - 1e-9) continue;
                double d = Vec2.Distance(e.Position, _defense.ObjectiveCenter);
                if (d < bestD - 1e-9) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>引き金の円の中で、弱っていて孤立している（即座の逆襲の目標になりうる）。</summary>
        bool CounterTargetable(Company e, DefenseUnitPlan plan) =>
            Vec2.Distance(e.Position, plan.TriggerCenter) <= plan.TriggerRadiusM + 1e-9 && WeakTarget(e) && IsolatedTarget(e);

        bool BannedTargetStillThere(Company c, DefenseUnitPlan plan, double t)
        {
            if (c.CounterBannedTargetId < 0 || t >= c.CounterBannedUntil - 1e-9) return false;
            var e = _companies[c.CounterBannedTargetId];
            return e.OnBoard && e.UnderCommand && SeenBySide(e, c.Side) && CounterTargetable(e, plan);
        }

        void UpdateReserve(Company c, double t)
        {
            var plan = PlanOf(c);
            if (plan == null) return;
            bool able = c.State == CompanyState.Normal && HasReserveStrength(c);
            // 最後の守り手として残っていた予備は、ほかに守り手が来たら、今の規則どおり戻る位置へ戻る（5回目の直しの1）
            if (c.PendingReturn)
            {
                if (LastDefender(c)) return;
                ReturnToPost(c, plan, t);
            }
            switch (c.Reserve)
            {
                case ReserveState.Waiting:
                case ReserveState.Returning:
                {
                    if (!able || LastDefender(c)) return;
                    var tgt = ReserveTarget(c, plan, t);
                    if (tgt != null) Counterattack(c, tgt, t);
                    return;
                }
                case ReserveState.Attacking:
                    // 突撃が終わった（目標が指揮下でなくなって確保に移った、または別の命令に替わった）
                    if (c.StartWaitOrder == null && c.AssaultTargetId < 0)
                    {
                        c.Reserve = ReserveState.AfterAttack;
                        goto case ReserveState.AfterAttack;
                    }
                    return;
                case ReserveState.AfterAttack:
                {
                    bool held = HeldByFront(c, c.Facing);
                    if (!able)
                    {
                        // 余力がない：拘束されていない時に戻る位置へ戻り、以後は逆襲しない
                        if (held) return;
                        ReturnToPost(c, plan, t);
                        c.Reserve = ReserveState.Spent;
                        return;
                    }
                    var tgt = LastDefender(c) ? null : ReserveTarget(c, plan, t);
                    if (tgt != null)
                    {
                        if (!held) Counterattack(c, tgt, t);
                        return;
                    }
                    // 進めずにやめた目標が、まだ引き金の円の中で弱って孤立していれば、その場で保持を続ける（出直さないだけ）
                    if (BannedTargetStillThere(c, plan, t)) return;
                    ReturnToPost(c, plan, t);
                    c.Reserve = ReserveState.Returning;
                    return;
                }
            }
        }

        /// <summary>
        /// 守り手（5回目の直しの2）：指揮下で通常（動揺・壊走・再集結中でない）の、役目が守備か予備の味方の中隊。
        /// 壊走して役目を外れた中隊は数えない。
        /// </summary>
        bool IsDefender(Company o, Side side) =>
            o.Side == side && o.OnBoard && o.State == CompanyState.Normal &&
            (o.Role == DefenseRole.Garrison || o.Role == DefenseRole.Reserve);

        static bool WithinObjective(Vec2 p, DefensePlan plan) =>
            Vec2.Distance(p, plan.ObjectiveCenter) <= MapScenario.VillageHoldRadiusM + 1e-9;

        /// <summary>
        /// 最後の守り手（5回目の直しの1。G7・G9。2回目の直しの1「守る地点を空けない」を置き換えた）：守備か予備の中隊が守る地点の
        /// 250m（勝ちの判定の円）以内にいて、自分のほかに 250m 以内に守り手がいない。最後の守り手は、250m の外へ出る動き（逆襲に出る、
        /// 戻る位置へ戻る）をしない。
        /// </summary>
        bool LastDefender(Company c)
        {
            if (c.Role != DefenseRole.Garrison && c.Role != DefenseRole.Reserve) return false;
            if (!c.OnBoard || !c.UnderCommand || !WithinObjective(c.Position, _defense)) return false;
            foreach (var o in _companies)
                if (o != c && IsDefender(o, c.Side) && WithinObjective(o.Position, _defense)) return false;
            return true;
        }

        /// <summary>弱っている：占領の乱れ、動揺、制圧下のどれか。</summary>
        bool WeakTarget(Company e) => InOccupationDisarray(e) || e.State == CompanyState.Shaken || e.Suppressed;

        /// <summary>孤立している：中心どうし defense.counter_isolation_radius_m（300m）以内の、ほかの指揮下の敵が defense.counter_max_support（1）個以下。</summary>
        bool IsolatedTarget(Company e)
        {
            int n = 0;
            foreach (var o in _companies)
                if (o != e && o.Side == e.Side && o.OnBoard && o.UnderCommand &&
                    Vec2.Distance(o.Position, e.Position) <= D.CounterIsolationRadiusM + 1e-9) n++;
            return n <= D.CounterMaxSupport;
        }

        void Counterattack(Company c, Company tgt, double t)
        {
            c.Reserve = ReserveState.Attacking;
            c.ThreatWeightOverride = _p.MapRules.ScriptedThreatWeight;
            Assault(c, tgt, t, true);
            if (_log.Enabled)
                _log.Write(Ev(t, "defense_counterattack", c).Add("side", SideName(c.Side)).Add("target", tgt.Name).ToString());
        }

        void ReturnToPost(Company c, DefenseUnitPlan plan, double t)
        {
            c.AssaultTargetId = -1;
            // 最後の守り手は、守る地点の 250m の外の戻る位置へは戻らず、その場で保持する（5回目の直しの1）
            if (!WithinObjective(plan.Post, _defense) && LastDefender(c))
            {
                if (!c.PendingReturn)
                {
                    c.PendingReturn = true;
                    HoldInPlace(c);
                    c.StageCompleted = true;
                    if (_log.Enabled) _log.Write(Ev(t, "defense_last_hold", c).Add("side", SideName(c.Side)).ToString());
                }
                return;
            }
            c.PendingReturn = false;
            SelfOrder(c, OrderSheet.Single(Order.Advance(c.Id, plan.Post, FacingToObjective(plan.Post))), t);
            if (_log.Enabled)
                _log.Write(Ev(t, "defense_return", c).Add("side", SideName(c.Side)).Add("spent", !HasReserveStrength(c)).ToString());
        }

        // ---- 規則3：奪回

        bool InActiveRetake(Company c)
        {
            var r = ActiveRetake;
            return r != null && r.Ordered.Contains(c.Id);
        }

        /// <summary>
        /// 奪回の条件（E1 の報告の後に決めたこと 5）：見えている指揮下の敵の中心が村のマスにあり、村のマスに指揮下の味方の中心がない
        /// （250m の円ではなく村そのもので見る）。
        /// </summary>
        bool RetakeNeeded()
        {
            foreach (var c in _companies)
                if (c.Side == _defenseSide && c.OnBoard && c.UnderCommand && _map.TerrainAt(c.Position) == TerrainMap.Village) return false;
            return RetakeTarget() != null;
        }

        /// <summary>村のマスにいる、見えている指揮下の敵のうち、守る地点に最も近い中隊。</summary>
        Company RetakeTarget()
        {
            Company best = null;
            double bestD = double.MaxValue;
            foreach (var e in SeenEnemies(_defenseSide))
            {
                if (_map.TerrainAt(e.Position) != TerrainMap.Village) continue;
                double d = Vec2.Distance(e.Position, _defense.ObjectiveCenter);
                if (d < bestD - 1e-9) { bestD = d; best = e; }
            }
            return best;
        }

        void UpdateRetake(double t)
        {
            var active = ActiveRetake;
            // 攻撃に移った中隊が全て突撃を終えた（崩した・やめた）ら、この奪回は終わり。条件が続いていれば、本部が次の判断で出し直す
            if (active != null && active.Launched && AllAssaultsOver(active)) active.Active = false;
            active = ActiveRetake;
            if (active != null)
            {
                if (!RetakeNeeded())
                {
                    // 条件が消えた：集結中の中隊は集結点で保持する（前進の命令のまま着いて止まる）
                    active.Active = false;
                    foreach (int id in active.Ordered)
                    {
                        var c = _companies[id];
                        if (c.Role == DefenseRole.Reserve && c.Reserve != ReserveState.Spent) c.Reserve = ReserveState.AfterAttack;
                    }
                }
                else if (D.RetakeAssemble) UpdateAssembly(active, t);
            }

            if (t < _nextHqEval - 1e-9) return;
            _nextHqEval = t + D.HqEvalSec;
            if (ActiveRetake != null || !RetakeNeeded()) return;

            var target = RetakeTarget();
            var r = new Retake { Id = _retakes.Count, StartTime = t, Active = true };
            foreach (var c in _companies)
            {
                if (c.Side != _defenseSide || !NormalUnderCommand(c) || !HasReserveStrength(c)) continue;
                if (HeldByFront(c, PositionFacing(c))) continue;
                if (c.Role == DefenseRole.Reserve && c.Reserve == ReserveState.Attacking) continue;
                var sheet = D.RetakeAssemble
                    ? OrderSheet.Single(Order.Advance(c.Id, _defense.Assembly, FacingToObjective(_defense.Assembly)))
                    : OrderSheet.Single(AssaultOrder(c, target));
                if (SendSheet(sheet, false) == SendResult.Invalid) continue;
                r.Ordered.Add(c.Id);
                c.AssaultTargetId = -1;
                if (!D.RetakeAssemble)
                {
                    // 小出しの版：伝令が着いたら、その中隊がすぐ突撃する
                    c.AssaultTargetId = target.Id;
                    c.AssaultSheet = sheet;
                    c.AssaultIsCounter = false;
                    c.NextAssaultReplan = t;
                }
                // 前線の中隊も、本部の命令に従った後は守備（自分では下がらない）
                if (c.Role == DefenseRole.Front) { c.Role = DefenseRole.Garrison; c.FellBack = true; }
            }
            if (r.Ordered.Count == 0) return;
            if (!D.RetakeAssemble)
            {
                r.Launched = true;
                foreach (int id in r.Ordered) r.Attacking.Add(id);
            }
            _retakes.Add(r);
            if (_log.Enabled)
            {
                var names = new List<string>();
                foreach (int id in r.Ordered) names.Add(_companies[id].Name);
                _log.Write(new JsonLineBuilder().Add("t", t).Add("type", "retake_order").Add("side", SideName(_defenseSide))
                    .Add("units", string.Join(",", names)).Add("mode", D.RetakeAssemble ? "assemble" : "immediate").ToString());
            }
        }

        /// <summary>
        /// 集めてから出す（assemble）：揃ったら（2個 または 600秒）着いている中隊が同時に攻撃し、後から着いた中隊も攻撃に移る。
        /// 600秒たっても1個も着いていなければ、命じた中隊のうち余力があり拘束されていないものが、今いる所から同時に突撃する
        /// （直しの後に決めたこと 3）。
        /// </summary>
        void UpdateAssembly(Retake r, double t)
        {
            var arrived = new List<Company>();
            int alive = 0;
            foreach (int id in r.Ordered)
            {
                var c = _companies[id];
                if (!c.OnBoard || !c.UnderCommand) continue;
                alive++;
                if (!r.Attacking.Contains(id) && Vec2.Distance(c.Position, _defense.Assembly) <= D.AssembleRadiusM + 1e-9) arrived.Add(c);
            }
            if (!r.Launched)
            {
                int need = Math.Min(D.AssembleMinCompanies, r.Ordered.Count);
                bool timedOut = t - r.StartTime >= D.AssembleMaxWaitSec - 1e-9;
                bool ready = (arrived.Count >= need && arrived.Count > 0) || timedOut;
                if (!ready) return;
                int fromPlace = 0;
                if (arrived.Count == 0)
                {
                    // 集まれなくても出る：今いる所から突撃する
                    foreach (int id in r.Ordered)
                    {
                        var c = _companies[id];
                        if (!NormalUnderCommand(c) || !HasReserveStrength(c) || HeldByFront(c, c.Facing)) continue;
                        arrived.Add(c);
                    }
                    if (arrived.Count == 0) return;
                    fromPlace = arrived.Count;
                }
                r.Launched = true;
                if (_log.Enabled)
                    _log.Write(new JsonLineBuilder().Add("t", t).Add("type", "retake_attack").Add("side", SideName(_defenseSide))
                        .Add("assembled", fromPlace > 0 ? 0 : arrived.Count).Add("from_place", fromPlace).ToString());
            }
            var target = RetakeTarget();
            if (target == null) return;
            foreach (var c in arrived)
            {
                r.Attacking.Add(c.Id);
                // 命令書に言い含めてあるので伝令は使わない（練度の遅れの後に突撃に移る）
                Assault(c, target, t, false);
                if (_log.Enabled)
                    _log.Write(Ev(t, "retake_attack_unit", c).Add("side", SideName(c.Side)).Add("target", target.Name).ToString());
            }
        }

        bool AllAssaultsOver(Retake r)
        {
            foreach (int id in r.Attacking)
                if (_companies[id].AssaultTargetId >= 0) return false;
            return true;
        }

        /// <summary>奪回の回数（命令・攻撃）。通し実行の出来事の回数に使う。</summary>
        public int RetakeOrders => _retakes.Count;
        public int RetakeLaunches { get { int n = 0; foreach (var r in _retakes) if (r.Launched) n++; return n; } }

        // ---------------------------------------------------------------- C12：占領の乱れ

        int[] _positionOf;
        readonly List<Side?> _positionOwner = new List<Side?>();
        readonly List<char> _positionKind = new List<char>();
        readonly List<(Vec2 point, Side side)> _initialOwners = new List<(Vec2, Side)>();

        /// <summary>陣地の初めの持ち主を決める（場面の定義用。会戦開始時に中隊がいない陣地を持ち主つきにする）。</summary>
        public void SetInitialOwner(Vec2 point, Side side) => _initialOwners.Add((point, side));

        /// <summary>
        /// 陣地（塹壕の1本・つながった村）を分け、持ち主を決める（最初の刻みの初めに1回）。持ち主は会戦開始時に中心がそこにある
        /// 中隊の側（いなければ持ち主なし）。<see cref="SetInitialOwner"/> で与えた持ち主が先。
        /// </summary>
        void InitPositions()
        {
            _positionOf = new int[_map.Width * _map.Height];
            for (int k = 0; k < _positionOf.Length; k++) _positionOf[k] = -1;
            for (int j0 = 0; j0 < _map.Height; j0++)
                for (int i0 = 0; i0 < _map.Width; i0++)
                {
                    char ch = _map.TerrainAt(i0, j0);
                    if ((ch != TerrainMap.Trench && ch != TerrainMap.Village) || _positionOf[j0 * _map.Width + i0] >= 0) continue;
                    int id = _positionOwner.Count;
                    _positionOwner.Add(null);
                    _positionKind.Add(ch);
                    var stack = new Stack<(int, int)>();
                    stack.Push((i0, j0));
                    _positionOf[j0 * _map.Width + i0] = id;
                    while (stack.Count > 0)
                    {
                        var (i, j) = stack.Pop();
                        for (int di = -1; di <= 1; di++)
                            for (int dj = -1; dj <= 1; dj++)
                            {
                                int a = i + di, b = j + dj;
                                if (!_map.InBounds(a, b) || _positionOf[b * _map.Width + a] >= 0 || _map.TerrainAt(a, b) != ch) continue;
                                _positionOf[b * _map.Width + a] = id;
                                stack.Push((a, b));
                            }
                    }
                }
            foreach (var (point, side) in _initialOwners)
            {
                int id = PositionAt(point);
                if (id >= 0) _positionOwner[id] = side;
            }
            foreach (var c in _companies)
            {
                int id = PositionAt(c.Position);
                if (id >= 0 && !_positionOwner[id].HasValue) _positionOwner[id] = c.Side;
            }
        }

        int PositionAt(Vec2 p)
        {
            _map.CellOf(p, out int i, out int j);
            return _map.InBounds(i, j) ? _positionOf[j * _map.Width + i] : -1;
        }

        /// <summary>点 <paramref name="p"/> の陣地の持ち主（陣地でなければ、または持ち主なしなら null）。</summary>
        public Side? PositionOwnerAt(Vec2 p)
        {
            if (_positionOf == null) return null;
            int id = PositionAt(p);
            return id >= 0 ? _positionOwner[id] : null;
        }

        /// <summary>占領の乱れの間か（C12）。</summary>
        public bool InOccupationDisarray(Company c) => _p.Occupation.Enabled && Time < c.OccupationDisarrayUntil - 1e-9;

        /// <summary>
        /// 占領（移動の後）：中隊の中心が、ほかの側が持ち主の陣地に入り、持ち主の側の指揮下の中隊がその陣地にいなければ、持ち主は
        /// その側に変わる。敵の側が持ち主だったら、その陣地にいる側の中隊は占領の乱れになる。持ち主なしなら乱れにはならない。
        /// </summary>
        void UpdateOccupation(double t)
        {
            if (!_p.Occupation.Enabled || _positionOf == null) return;
            foreach (var c in _companies)
            {
                if (!c.OnBoard || !c.UnderCommand) continue;
                int id = PositionAt(c.Position);
                if (id < 0) continue;
                var owner = _positionOwner[id];
                if (owner == c.Side) continue;
                if (owner.HasValue && OwnerHolds(id, owner.Value)) continue;
                _positionOwner[id] = c.Side;
                if (!owner.HasValue) continue;
                foreach (var o in _companies)
                {
                    if (o.Side != c.Side || !o.OnBoard || !o.UnderCommand || PositionAt(o.Position) != id) continue;
                    o.OccupationDisarrayUntil = t + _p.Occupation.DisarraySec;
                    if (_log.Enabled)
                        _log.Write(Ev(t, "occupation_disarray", o).Add("side", SideName(o.Side))
                            .Add("position", _positionKind[id] == TerrainMap.Village ? "village" : "trench").ToString());
                }
            }
        }

        bool OwnerHolds(int id, Side owner)
        {
            foreach (var o in _companies)
                if (o.Side == owner && o.OnBoard && o.UnderCommand && PositionAt(o.Position) == id) return true;
            return false;
        }
    }
}
