using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    // R6：突撃の最後の一歩（仕様「R6：突撃の最後の一歩（突入と白兵戦、射撃の延伸）（確定）」）。
    // 射撃の延伸（1）、突入の比べ（2）、白兵戦（3）。攻める側が青でも赤（逆襲・奪回の突撃）でも同じ規則（4）。
    public sealed partial class Simulation
    {
        SimParams.MeleeParams M => _p.Melee;

        // ---------------------------------------------------------------- 1. 射撃の延伸

        /// <summary>
        /// 射撃の延伸：<paramref name="target"/> の矩形から fire.lift_clearance_m 以内に、<paramref name="shooter"/> の側の指揮下の中隊
        /// （撃つ中隊自身を除く）の矩形が入っているか。撃つ中隊自身が目標と接近戦の距離にいる時は延伸しない（false）。
        /// </summary>
        public bool FireLifted(Company shooter, Company target)
        {
            if (!_p.Fire.LiftEnabled) return false;
            double clear = _p.Fire.LiftClearanceM;
            UnitRect? tr = null;
            foreach (var f in _companies)
            {
                if (f == shooter || f.Side != shooter.Side || !f.UnderCommand || f.Strength <= 0) continue;
                var fr = RectOf(f);
                if (tr == null) tr = RectOf(target);
                if (Vec2.Distance(fr.Center, tr.Value.Center) > fr.Radius + tr.Value.Radius + clear + 1) continue;
                if (UnitRect.Gap(fr, tr.Value, clear + 1) > clear) continue;
                return !InCloseCombat(shooter, target);
            }
            return false;
        }

        /// <summary>白兵戦の相手どうしか（白兵戦の間は、その相手との小銃の撃ち合いの代わりに白兵戦の損害を受ける）。</summary>
        public bool MeleeOpponents(Company a, Company b) =>
            (a.MeleeDefenderId == b.Id && b.MeleeAttackers.Contains(a.Id)) || (b.MeleeDefenderId == a.Id && a.MeleeAttackers.Contains(b.Id));

        /// <summary>白兵戦の中にいるか（守り手・突入した攻める側のどちらか）。</summary>
        public static bool InMelee(Company c) => c.MeleeAttackers.Count > 0 || c.MeleeDefenderId >= 0;

        // ---------------------------------------------------------------- 2. 突入の比べ

        /// <summary>
        /// 押す力に数える中隊か（4）：指揮下で、接近戦の距離にいて、攻撃の命令でその守り手へ、または前進の命令でその守り手か
        /// その守り手のいる陣地へ向かっている。前進の目的地が守り手の矩形から接近戦の距離（50m）以内ならその守り手、陣地の中なら
        /// その陣地にいる指揮下の守り手のうち目的地に一番近いもの。保持・拘束・後退の中隊は数えない。
        /// </summary>
        public bool AssaultsOn(Company a, Company d)
        {
            if (!a.UnderCommand || a.Side == d.Side || a.Strength <= 0) return false;
            var o = a.CurrentOrder;
            if (o.Type == OrderType.Attack)
            {
                if (o.TargetId != d.Id) return false;
            }
            else if (o.Type == OrderType.Advance && o.Destination.HasValue)
            {
                if (!AdvanceAimsAt(o.Destination.Value, d)) return false;
            }
            else return false;
            return InCloseCombat(a, d);
        }

        bool AdvanceAimsAt(Vec2 dest, Company d)
        {
            if (RectOf(d).DistanceTo(dest) <= _p.CloseCombat.RadiusM) return true;
            int pos = _positionOf != null ? PositionAt(dest) : -1;
            if (pos < 0 || PositionAt(d.Position) != pos) return false;
            double mine = Vec2.Distance(d.Position, dest);
            foreach (var e in _companies)
                if (e != d && e.Side == d.Side && e.UnderCommand && PositionAt(e.Position) == pos)
                {
                    double other = Vec2.Distance(e.Position, dest);
                    if (other < mine || (other == mine && e.Id < d.Id)) return false;
                }
            return true;
        }

        /// <summary>中隊1個の力：兵力×士気／100×（制圧下・動揺・練度の倍率）。</summary>
        double BaseForce(Company c)
        {
            double f = c.Strength * c.Morale / 100.0 * _p.Of(c.Training).FireMult;
            if (c.Suppressed) f *= 0.25;
            if (c.State == CompanyState.Shaken) f *= 0.5;
            return f;
        }

        /// <summary>攻める側の1個の押す力：<see cref="BaseForce"/> に、占領の乱れ・展開中・縮合中 ×0.5、守り手から見た向き（側面 ×1.5・背面 ×2.0）。</summary>
        public double PushForce(Company a, Company d)
        {
            double f = BaseForce(a);
            if (InOccupationDisarray(a) || ChangingFormation(a)) f *= 0.5;
            return f * _p.Fire.AspectCasualtyMult[(int)AspectOf(d, a.Position)];
        }

        /// <summary>
        /// 守る力：<see cref="BaseForce"/> に陣地の守り（<see cref="DefenseFortMult"/>）。<paramref name="attackers"/> は押す力に数える中隊
        /// （陣地の向きを見るのに使う。null なら向きを見ない）。
        /// </summary>
        public double DefendForce(Company d, IReadOnlyList<Company> attackers = null) => BaseForce(d) * DefenseFortMult(d, attackers);

        /// <summary>
        /// 守る力の陣地の倍率：塹壕のマス（構築した塹壕を含む）は melee.defense_mult.trench、村・森はその値、それ以外は plain。
        /// 陣地構築の途中は進捗に応じて塹壕の値へ、浅い掩体は進捗に応じて scrape の値へ近づく（大きい方）。
        /// 向きのある陣地（塹壕・陣地構築・浅い掩体）は、押す力に数える中隊が全て陣地の向き（R2）の正面の弧にいる時だけ効く。1個でも
        /// 弧の外（側面・背面）にいれば、その守りは効かない（R6 の報告の後に決めたこと 1）。村・森は向きを見ない。
        /// </summary>
        public double DefenseFortMult(Company d, IReadOnlyList<Company> attackers = null)
        {
            bool InArc(double? facing)
            {
                if (attackers == null) return true;
                foreach (var a in attackers)
                    if (!InFortificationArc(facing, d, a.Position)) return false;
                return true;
            }
            char ch = _map.TerrainAt(d.Position);
            if (ch == TerrainMap.Trench) return InArc(_map.FortificationFacingAt(d.Position)) ? M.DefenseTrench : M.DefensePlain;
            double f = ch == TerrainMap.Village ? M.DefenseVillage : ch == TerrainMap.Forest ? M.DefenseForest : M.DefensePlain;
            double g = f;
            // 陣地構築の途中は今の中隊の向き、浅い掩体は掘り始めたときの向きで見る（被弾の倍率と同じ）
            if (d.EntrenchProgress > 0 && M.DefenseTrench > f && InArc(d.Facing))
                g = Math.Max(g, f + (M.DefenseTrench - f) * Math.Min(1.0, d.EntrenchProgress / _p.MapRules.EntrenchSec));
            if (d.ScrapeProgress > 0 && IsScrapeGround(ch) && M.DefenseScrape > f && InArc(d.ScrapeFacing))
                g = Math.Max(g, f + (M.DefenseScrape - f) * Math.Min(1.0, d.ScrapeProgress / A.ScrapeDigSec));
            return g;
        }

        // ---------------------------------------------------------------- 3. 白兵戦

        /// <summary>
        /// 守り手ごとの比べと白兵戦（損害の同時解決の後に毎刻み）。押す力に数える中隊がいる守り手は、初めて現れた刻みと、その後
        /// melee.check_sec ごとに比べる。白兵戦の間は毎刻み、白兵戦の損害と士気の減りを受ける。
        /// </summary>
        void UpdateMelee(double t)
        {
            if (!M.Enabled) return;
            double dtMin = _p.TickSec / 60.0;
            var att = new List<Company>();
            foreach (var d in _companies)
            {
                // 守り手でなくなった（崩れた・盤上から消えた）
                if (!d.UnderCommand || d.MeleeDefenderId >= 0)
                {
                    if (d.MeleeAttackers.Count > 0) EndMelee(d, t, null);
                    d.MeleeNextCheck = double.NaN;
                    continue;
                }
                att.Clear();
                foreach (var a in _companies)
                    if (a.MeleeAttackers.Count == 0 && AssaultsOn(a, d) && (a.MeleeDefenderId < 0 || a.MeleeDefenderId == d.Id)) att.Add(a);
                if (att.Count == 0)
                {
                    if (d.MeleeAttackers.Count > 0) EndMelee(d, t, null);
                    d.MeleeNextCheck = double.NaN;
                    continue;
                }
                if (double.IsNaN(d.MeleeNextCheck) || t >= d.MeleeNextCheck - 1e-9)
                {
                    d.MeleeNextCheck = t + M.CheckSec;
                    Compare(d, att, t);
                }
                if (d.MeleeAttackers.Count > 0) MeleeLosses(d, t, dtMin);
            }
        }

        void Compare(Company d, List<Company> att, double t)
        {
            double push = 0;
            foreach (var a in att) push += PushForce(a, d);
            double def = DefendForce(d, att);
            d.MeleePush = push;
            d.MeleeDefend = def;
            foreach (var a in att)
            {
                a.MeleePush = push;
                a.MeleeDefend = def;
            }
            bool inMelee = d.MeleeAttackers.Count > 0;
            if (push > def)
            {
                // 突入の衝撃は1回の突入に1度：攻める側がいなくなって白兵戦が切れ、melee.check_sec の内に入り直したときは受けない
                if (!inMelee && t - d.MeleeEndedTime > M.CheckSec + 1e-9)
                {
                    double shock = M.EntryShock * _p.Of(d.Training).MoraleSensitivity;
                    d.Morale = Math.Max(0, d.Morale - shock);
                    d.Ledger.Melee -= shock;
                }
                // 比べのたびに、押す力に数える中隊を白兵戦の中に入れ直す（来た中隊は加わり、離れた中隊は抜ける）
                foreach (var id in d.MeleeAttackers) _companies[id].MeleeDefenderId = -1;
                d.MeleeAttackers.Clear();
                foreach (var a in att)
                {
                    d.MeleeAttackers.Add(a.Id);
                    a.MeleeDefenderId = d.Id;
                }
                d.MeleeRatio = Math.Min(M.LossRatioCap, def > 0 ? push / def : M.LossRatioCap);
                if (!inMelee && _log.Enabled)
                    foreach (var a in att)
                        _log.Write(Ev(t, "melee_entry", a).Add("side", SideName(a.Side)).Add("defender", d.Name)
                            .Add("place", PlaceName(d)).Add("push", push).Add("defend", def).ToString());
                return;
            }
            if (inMelee)
            {
                // 押し返された：白兵戦は終わり、攻める側の各中隊の士気 −10
                var was = new List<Company>();
                foreach (var id in d.MeleeAttackers) was.Add(_companies[id]);
                EndMelee(d, t, "melee_pushed_back");
                foreach (var a in was) RepulseHit(a);
                return;
            }
            // 跳ね返し
            foreach (var a in att)
            {
                RepulseHit(a);
                if (_log.Enabled)
                    _log.Write(Ev(t, "melee_repulsed", a).Add("side", SideName(a.Side)).Add("defender", d.Name)
                        .Add("push", push).Add("defend", def).ToString());
            }
        }

        /// <summary>守り手のいる所の名前（ログ用）：trench・village・forest・open。</summary>
        string PlaceName(Company d)
        {
            char ch = _map.TerrainAt(d.Position);
            return ch == TerrainMap.Trench || d.EntrenchProgress > 0 ? "trench" : ch == TerrainMap.Village ? "village" : ch == TerrainMap.Forest ? "forest" : "open";
        }

        void RepulseHit(Company a)
        {
            if (!a.UnderCommand) return;
            double hit = M.RepulseMorale * _p.Of(a.Training).MoraleSensitivity;
            a.Morale = Math.Max(0, a.Morale - hit);
            a.Ledger.Melee -= hit;
        }

        /// <summary>白兵戦を終える。<paramref name="logType"/> があれば攻める側の各中隊にその出来事を出す。</summary>
        void EndMelee(Company d, double t, string logType)
        {
            // 押し返された後の突入は新しい突入（衝撃を受ける）。攻める側がいなくなって切れたときだけ、すぐの入り直しを同じ突入とみなす
            d.MeleeEndedTime = logType == null ? t : double.NegativeInfinity;
            foreach (var id in d.MeleeAttackers)
            {
                var a = _companies[id];
                a.MeleeDefenderId = -1;
                if (logType != null && _log.Enabled)
                    _log.Write(Ev(t, logType, a).Add("side", SideName(a.Side)).Add("defender", d.Name)
                        .Add("push", d.MeleePush).Add("defend", d.MeleeDefend).ToString());
            }
            d.MeleeAttackers.Clear();
        }

        /// <summary>白兵戦の毎刻みの損害（ポアソン、戦闘の流れの乱数）と士気の減り。陣地の守りは効かず、村だけ ×melee.village_loss_mult。</summary>
        void MeleeLosses(Company d, double t, double dtMin)
        {
            double village = _map.TerrainAt(d.Position) == TerrainMap.Village ? M.VillageLossMult : 1.0;
            int dc = _rng.NextPoisson(M.LossPctDefender / 100.0 * d.Strength * d.MeleeRatio * village * dtMin);
            d.Strength = Math.Max(0, d.Strength - dc);
            double dm = M.MoraleDefender * dtMin * _p.Of(d.Training).MoraleSensitivity;
            d.Morale = Math.Max(0, d.Morale - dm);
            d.Ledger.Melee -= dm;
            d.LastTargetedTime = t;
            foreach (var id in d.MeleeAttackers)
            {
                var a = _companies[id];
                if (!a.UnderCommand) continue;
                int ac = _rng.NextPoisson(M.LossPctAttacker / 100.0 * a.Strength * village * dtMin);
                a.Strength = Math.Max(0, a.Strength - ac);
                double am = M.MoraleAttacker * dtMin * _p.Of(a.Training).MoraleSensitivity;
                a.Morale = Math.Max(0, a.Morale - am);
                a.Ledger.Melee -= am;
                a.LastTargetedTime = t;
            }
        }

        /// <summary>白兵戦の中で壊走した守り手が、その場で降伏するか（確率 melee.surrender_prob。乱数は戦闘の流れから引く）。</summary>
        bool SurrendersInMelee(Company c) => M.Enabled && c.MeleeAttackers.Count > 0 && _rng.NextDouble() < M.SurrenderProb;
    }
}
