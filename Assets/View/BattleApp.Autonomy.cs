using System;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // P2c：中隊長の自律行動の表示。駒の性格、自律行動・独断の理由の札、独断中の色。
    public sealed partial class BattleApp
    {
        /// <summary>独断中の印の色（橙）。</summary>
        static readonly Color IndependentColor = new Color(1f, 0.565f, 0.25f);
        static readonly Color32 IndependentColor32 = new Color32(255, 144, 64, 245);

        /// <summary>止めた段の理由：動揺で停止中、または慎重の任せるが士気 60 未満で止めた。</summary>
        string HaltText(Company c) => c.State == CompanyState.Shaken ? "動揺で停止中" : "士気低下で停止中（慎重）";

        /// <summary>
        /// 自律行動・独断の理由（駒の札と状態の文に出す）。独断なら <paramref name="independent"/> が true。なければ null。
        /// </summary>
        string AutonomyTag(Company c, out bool independent)
        {
            independent = true;
            switch (c.Independent)
            {
                case IndependentAct.Pursuit:
                    return "独断：追撃";
                case IndependentAct.Attack:
                {
                    int id = c.Pursuing ? c.PursuitTargetId : c.CurrentOrder.TargetId;
                    var t = id >= 0 ? _sim.Companies[id] : null;
                    return t == null ? "独断：攻撃" : $"独断：攻撃（{TargetStateText(t)}の {t.Name}）";
                }
                case IndependentAct.Flank:
                {
                    int id = c.CurrentOrder.TargetId;
                    return id >= 0 ? $"独断：側面へ（{_sim.Companies[id].Name}）" : "独断：側面へ";
                }
            }
            if (c.IndependentWaitOrder != null)
                return $"独断：側面へ（{_sim.Companies[c.IndependentWaitOrder.TargetId].Name}）あと {Math.Max(0, c.IndependentWaitAt - _sim.Time):0}秒";
            if (c.AutoMove == AutoMove.Cover && c.CoverIndependent) return "独断：後退（物陰へ）";
            independent = false;
            if (c.IndependentStopped) return "独断の限度で停止";
            if (c.AutoMove == AutoMove.Cover) return "物陰へ（開けた所で被弾）";
            if (c.CoverWaitStart.HasValue)
                return c.CoverIndependent ? "物陰を探した（後退の準備）" : "物陰へ寄る準備（開けた所で被弾）";
            if (c.AutoMove == AutoMove.EnterPosition) return "崩した敵の陣地に入る";
            if (c.ScrapeProgress > 0)
            {
                double frac = Math.Min(1.0, c.ScrapeProgress / _params.Autonomy.ScrapeDigSec);
                return frac >= 1 ? "浅い掩体" : $"浅い掩体を掘っている {frac * 100:0}%";
            }
            if (c.AutoFacing.HasValue && Math.Abs(Angles.Wrap(c.AutoFacing.Value - c.Facing)) > 1e-6) return "敵の方へ向く";
            return null;
        }

        static string TargetStateText(Company t) =>
            t.State == CompanyState.Shaken ? "動揺中" : t.State == CompanyState.Routed ? "壊走中" : "";

        /// <summary>
        /// 青の駒の下に「B1 慎重」（性格を常に出す）と、自律行動・独断の理由の札（独断は橙、それ以外は薄緑）。
        /// </summary>
        void DrawAutonomyTags(float px)
        {
            foreach (var c in _sim.Companies)
            {
                if (c.Side != Side.Blue || !c.OnBoard) continue;
                Pose(c, out var pos, out double facing);
                Extent(facing, out float ex, out float ey);
                var at = V(pos) + new Vector2(0, -ey - 16 * px);
                string name = $"{c.Name} {PersonalityName(c.Personality)}";
                _texts.Draw(name, at + new Vector2(1.2f * px, -1.2f * px), 12, px, new Color(0, 0, 0, 0.85f));
                _texts.Draw(name, at, 12, px, new Color(0.85f, 0.92f, 1f));
                if (!c.UnderCommand) continue;
                string tag = AutonomyTag(c, out bool independent);
                if (tag == null) continue;
                var tat = at + new Vector2(0, -15 * px);
                _texts.Draw(tag, tat + new Vector2(1.2f * px, -1.2f * px), 12, px, new Color(0, 0, 0, 0.85f));
                _texts.Draw(tag, tat, 12, px, independent ? IndependentColor : new Color(0.7f, 0.95f, 0.75f));
            }
        }
    }
}
