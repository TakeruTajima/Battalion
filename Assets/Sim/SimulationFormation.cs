using System;

namespace Battalion.Sim
{
    // F1：隊形（仕様「F1：隊形（縦隊と横隊、展開と縮合）（確定）」と「F1 の報告の後に決めたこと」）。
    // 展開・縮合は中隊長が決める（任せる・厳命で同じ）。移り変わる間は速さ ×0.5 で命令の経路を進み続け、撃つ力 ×0.3、
    // R1 の動いている扱い。矩形は移り変わりが終わった時に新しい形になる。縦隊になるのは、敵を知らない時の長い移動だけ。
    public sealed partial class Simulation
    {
        SimParams.FormationParams F => _p.Formation;

        /// <summary>縦隊の矩形か（縦隊で、展開の途中を含む。縮合の途中はまだ横隊の矩形）。</summary>
        public static bool ColumnShape(Company c) => c.Formation == Formation.Column;

        /// <summary>移り変わりの途中か（展開中・縮合中）。</summary>
        public static bool ChangingFormation(Company c) => c.FormationChange != FormationChange.None;

        /// <summary>移り変わりの進み具合（0〜1。表示用）。移り変わりの途中でなければ 0。</summary>
        public double FormationProgress(Company c)
        {
            double total = c.FormationChange == FormationChange.Deploying ? F.DeploySec
                : c.FormationChange == FormationChange.FormingColumn ? F.FormColumnSec : 0;
            return total > 0 ? Math.Min(1.0, c.FormationChangeSec / total) : 0;
        }

        /// <summary>中隊 <paramref name="c"/> を点 <paramref name="pos"/> に向き <paramref name="facing"/> で置いたときの矩形（隊形の形）。</summary>
        public UnitRect RectAt(Vec2 pos, double facing, Company c) =>
            c != null && ColumnShape(c)
                ? new UnitRect(pos, facing, F.ColumnFrontageM, F.ColumnDepthM)
                : RectAt(pos, facing);

        /// <summary>展開する距離（性格による）。</summary>
        double DeployDistance(Company c) => F.DeployDistanceM.TryGetValue(c.Personality, out var d) ? d : F.DeployDistanceM[Personality.Calm];

        /// <summary>気付いている指揮下の敵のうち、<paramref name="within"/> 以内で最も近い敵（いなければ null）。</summary>
        Company NearestAwareEnemy(Company c, double within)
        {
            Company best = null;
            double bestD = within + 1e-9;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || !e.UnderCommand || e.Side == c.Side) continue;
                double d = Vec2.Distance(c.Position, e.Position);
                if (d > bestD) continue;
                if (!Aware(c, e)) continue;
                best = e;
                bestD = d;
            }
            return best;
        }

        /// <summary>残りの経路の長さ（今の位置から経路の終わりまで）。経路がなければ 0。</summary>
        static double RemainingPathLength(Company c)
        {
            if (c.Path == null) return 0;
            double len = 0;
            var a = c.Position;
            for (int k = c.PathIndex; k < c.Path.Count; k++)
            {
                len += Vec2.Distance(a, c.Path[k]);
                a = c.Path[k];
            }
            return len;
        }

        /// <summary>移動の命令で動いている途中か（前進・後退・攻撃・拘束で発起点へ、目的地のある陣地構築。自律の小さな動きは数えない）。</summary>
        static bool MovingByOrder(Company c) => c.CurrentOrder.Moves && !c.Arrived && c.AutoMove == AutoMove.None;

        /// <summary>
        /// 中隊長の隊形の判断（刻みの終わり。状態遷移の後）。壊走・再集結中は隊形を持たない（横隊に戻す）。
        /// </summary>
        void UpdateFormation(double t)
        {
            if (!F.Enabled) return;
            double dt = _p.TickSec;
            foreach (var c in _companies)
            {
                if (!c.OnBoard || !c.UnderCommand)
                {
                    c.Formation = Formation.Line;
                    c.FormationChange = FormationChange.None;
                    c.FormationChangeSec = 0;
                    c.ColumnReadySec = 0;
                    continue;
                }
                bool shot = c.LastTargetedTime == t;

                if (c.FormationChange == FormationChange.Deploying)
                {
                    c.FormationChangeSec += dt;
                    if (c.FormationChangeSec >= F.DeploySec - 1e-9) FinishDeploy(c, t);
                    continue;
                }

                Company near = NearestAwareEnemy(c, DeployDistance(c));
                if (c.FormationChange == FormationChange.FormingColumn)
                {
                    // 縮合の途中で敵に気付いた（距離によらない）・撃たれた：やめて横隊のまま（矩形はまだ横隊）
                    var any = near ?? NearestAwareEnemy(c, double.MaxValue);
                    if (any != null || shot)
                    {
                        c.FormationChange = FormationChange.None;
                        c.FormationChangeSec = 0;
                        c.ColumnReadySec = 0;
                        LogFormation(c, t, "column_cancel", shot ? "fired_on" : "enemy", any);
                        continue;
                    }
                    c.FormationChangeSec += dt;
                    if (c.FormationChangeSec >= F.FormColumnSec - 1e-9)
                    {
                        c.Formation = Formation.Column;
                        c.FormationChange = FormationChange.None;
                        c.FormationChangeSec = 0;
                        c.ColumnReadySec = 0;
                        LogFormation(c, t, "column_formed", null, null);
                    }
                    continue;
                }

                if (c.Formation == Formation.Column)
                {
                    // 展開する（どれか一つで）
                    string reason = null;
                    if (shot) reason = "fired_on";
                    else if (near != null) reason = "enemy";
                    else if (!MovingByOrder(c)) reason = "halt";
                    else if (c.Path != null && RemainingPathLength(c) <= F.DeployBeforeDestM + 1e-9) reason = "near_goal";
                    if (reason != null)
                    {
                        c.FormationChange = FormationChange.Deploying;
                        c.FormationChangeSec = 0;
                        c.ColumnReadySec = 0;
                        LogFormation(c, t, "deploy_start", reason, reason == "enemy" ? near : null);
                    }
                    continue;
                }

                // 横隊：縮合する条件が続いた秒数を数える（揺れ止め）。気付いている指揮下の敵が1つもいないこと（距離によらない）
                bool ready = MovingByOrder(c) && c.Path != null && !c.Engaged && !shot && !PinnedDown(c)
                             && RemainingPathLength(c) >= F.ColumnMinRouteM - 1e-9 && NearestAwareEnemy(c, double.MaxValue) == null;
                c.ColumnReadySec = ready ? c.ColumnReadySec + dt : 0;
                if (c.ColumnReadySec >= F.ColumnQuietSec - 1e-9)
                {
                    c.FormationChange = FormationChange.FormingColumn;
                    c.FormationChangeSec = 0;
                    c.ColumnReadySec = 0;
                    LogFormation(c, t, "column_start", null, null);
                }
            }
        }

        /// <summary>
        /// 展開を終える：横隊になり、向きを決める（気付いている最も近い指揮下の敵の方。いなければ攻撃なら目標、動いていれば進む方角、
        /// ほかは命令の向き）。線を張っている 60秒（R3）は満たした扱い。
        /// </summary>
        void FinishDeploy(Company c, double t)
        {
            c.Formation = Formation.Line;
            c.FormationChange = FormationChange.None;
            c.FormationChangeSec = 0;
            c.ColumnReadySec = 0;
            var e = NearestAwareEnemy(c, double.MaxValue);
            Vec2? toward = null;
            if (e != null) toward = e.Position;
            else if (c.CurrentOrder.Type == OrderType.Attack && EnemyOf(c, c.CurrentOrder.TargetId) is Company tgt) toward = tgt.Position;
            else if (MovingByOrder(c) && c.Path != null && c.PathIndex < c.Path.Count) toward = c.Path[c.PathIndex];
            if (toward.HasValue && (toward.Value - c.Position).Length > 1e-9)
            {
                var d = toward.Value - c.Position;
                c.Facing = Math.Atan2(d.Y, d.X);
            }
            else if (c.CurrentOrder.FacingRad.HasValue) c.Facing = Angles.Wrap(c.CurrentOrder.FacingRad.Value);
            c.StillSec = Math.Max(c.StillSec, _p.Movement.LineFormedStillSec);
            LogFormation(c, t, "deploy_end", null, e);
        }

        void LogFormation(Company c, double t, string what, string reason, Company enemy)
        {
            if (!_log.Enabled) return;
            var b = Ev(t, "formation", c).Add("side", SideName(c.Side)).Add("event", what);
            if (reason != null) b.Add("reason", reason);
            if (enemy != null) b.Add("enemy", enemy.Name);
            _log.Write(b.ToString());
        }
    }
}
