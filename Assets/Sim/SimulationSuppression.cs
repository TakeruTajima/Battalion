using System;

namespace Battalion.Sim
{
    // R1：制圧（仕様「R1：開けた所を動く代償と制圧（確定）」の規則2）。
    // 受けている火力の量は損害の同時解決（Resolve）で積み、その直後に制圧の状態を更新する。効き目は次の刻みの移動・射撃から。
    public sealed partial class Simulation
    {
        SimParams.SuppressionParams S => _p.Suppression;

        /// <summary>
        /// 動いている姿勢か：移動を伴う命令でまだ着いていないか、自律行動の移動の途中。伏せて止まっている間も、命令はまだ
        /// 着いていないので「動いている」のまま（伏せたときのしきい値で解ける）。
        /// </summary>
        public bool MovingPosture(Company c) => (c.CurrentOrder.Moves && !c.Arrived) || c.AutoMove != AutoMove.None;

        /// <summary>
        /// 制圧のしきい値（毎分）。動いている間は性格の倍率を掛ける。接敵中にその場で向きを変えている間（R3）も動いている時の値。
        /// </summary>
        public double SuppressionThreshold(Company c)
        {
            if (!MovingPosture(c) && !c.TurningAsMoving) return S.ThresholdStationaryPerMin;
            double mult = S.MovingThresholdPersonalityMult.TryGetValue(c.Personality, out var m) ? m : 1.0;
            return S.ThresholdMovingPerMin * mult;
        }

        /// <summary>
        /// 制圧の始まりと終わり（揺れ止めつき）：火力の量がしきい値以上の刻みが <c>onset_sec</c> 続いたら制圧下、
        /// しきい値 × <c>release_ratio</c> 未満の刻みが <c>release_sec</c> 続いたら解ける。条件が途切れたら数え直す。
        /// 指揮下でなくなったら消す。
        /// </summary>
        void UpdateSuppression(double t)
        {
            double dt = _p.TickSec;
            foreach (var c in _companies)
            {
                if (!c.OnBoard || !c.UnderCommand)
                {
                    c.Suppressed = false;
                    c.SuppressOnSec = 0;
                    c.SuppressOffSec = 0;
                    continue;
                }
                double th = SuppressionThreshold(c);
                double v = c.IncomingFirePerMin;
                if (!c.Suppressed)
                {
                    if (v >= th)
                    {
                        c.SuppressOnSec += dt;
                        if (c.SuppressOnSec >= S.OnsetSec - 1e-9)
                        {
                            c.Suppressed = true;
                            c.SuppressOffSec = 0;
                            if (_log.Enabled)
                                _log.Write(Ev(t, "suppressed", c).Add("side", SideName(c.Side)).Add("fire", Math.Round(v, 3))
                                    .Add("threshold", th).Add("moving", MovingPosture(c)).ToString());
                        }
                    }
                    else c.SuppressOnSec = 0;
                }
                else
                {
                    if (v < th * S.ReleaseRatio)
                    {
                        c.SuppressOffSec += dt;
                        if (c.SuppressOffSec >= S.ReleaseSec - 1e-9)
                        {
                            c.Suppressed = false;
                            c.SuppressOnSec = 0;
                            if (_log.Enabled)
                                _log.Write(Ev(t, "suppression_end", c).Add("side", SideName(c.Side)).Add("fire", Math.Round(v, 3))
                                    .Add("threshold", th).ToString());
                        }
                    }
                    else c.SuppressOffSec = 0;
                }
            }
        }

        /// <summary>
        /// 伏せて止まるか：制圧下で、動こうとしていて（<see cref="MovingPosture"/>）、後退の命令でなく、接触の距離
        /// （矩形の隙間 50m 以内）に指揮下の敵がいない。
        /// </summary>
        public bool PinnedDown(Company c) =>
            c.Suppressed && MovingPosture(c) && c.CurrentOrder.Type != OrderType.Retreat && !InCloseCombatWithAny(c);

        bool InCloseCombatWithAny(Company c)
        {
            foreach (var e in _companies)
                if (e.OnBoard && e.UnderCommand && e.Side != c.Side && InCloseCombat(c, e)) return true;
            return false;
        }

        /// <summary>
        /// 伏せて止まる：その刻みは動かない（経路・行き先・命令の段はそのまま）。向き直った向きがあればそれを、なければ
        /// 火力の量が一番多い射手の方へ、ふつうの回る速さで向く。
        /// </summary>
        void LieDown(Company c, double maxTurn)
        {
            if (c.FacingOverride.HasValue)
            {
                c.Facing = TurnToward(c.Facing, c.FacingOverride.Value, maxTurn);
                return;
            }
            if (c.MainShooterId < 0) return;
            var s = _companies[c.MainShooterId];
            if (!s.OnBoard) return;
            var d = s.Position - c.Position;
            if (d.Length > 1e-9) c.Facing = TurnToward(c.Facing, Math.Atan2(d.Y, d.X), maxTurn);
        }
    }
}
