using System;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 伝令（P2a）：命令は本部から伝令で出す。本部の旗、伝令の点と点線、未着・開始待ちの印、判断枠
    public sealed partial class BattleApp
    {
        /// <summary>直前に命令を出せなかった理由（判断枠がない等）。命令を出せたら消す。</summary>
        string _commandRefusal;

        /// <summary>新しい命令を出せるか（会戦開始前か、判断枠が空いている）。</summary>
        bool SlotAvailable => _sim.BeforeStart || _sim.RunnerSlotsUsed(Side.Blue) < _sim.MaxRunners;

        /// <summary>判断枠がないときの理由（最初に戻る予定の伝令までの見積もり）。</summary>
        string NoSlotReason()
        {
            double? eta = NextSlotEta();
            return "伝令が出払っている" + (eta.HasValue ? $"（最初に戻る予定まで あと {eta.Value:0}秒）" : "");
        }

        /// <summary>
        /// 判断枠を使っている伝令のうち、最も早く枠が空く見込み（秒）。本部の知っていることだけで見積もる：
        /// 戻ってくる予定（準備の残り＋中隊まで＋本部まで）と、受領報告なしの強制開放の早い方。
        /// </summary>
        double? NextSlotEta()
        {
            double? best = null;
            var hq = _sim.HeadquartersOf(Side.Blue);
            if (!hq.HasValue) return null;
            double v = _params.Runners.SpeedMps, now = _sim.Time;
            foreach (var r in _sim.Runners)
            {
                if (r.Side != Side.Blue || !r.CountsSlot || r.SlotReleased) continue;
                var c = _sim.Companies[r.CompanyId];
                double eta;
                if (r.State == RunnerState.Returning) eta = Vec2.Distance(r.Position, hq.Value) / v;
                else
                {
                    double prep = Math.Max(0, r.SentTime + _params.Runners.PrepSec - now);
                    eta = prep + (Vec2.Distance(r.Position, c.Position) + Vec2.Distance(c.Position, hq.Value)) / v;
                }
                eta = Math.Min(eta, Math.Max(0, r.OverdueAt - now));
                if (!best.HasValue || eta < best.Value) best = eta;
            }
            return best;
        }

        /// <summary>中隊の命令の伝令の状態（未着・開始待ち）。なければ null。</summary>
        string RunnerTag(Company c)
        {
            if (c.StartWaitOrder != null) return $"受領 開始まで {Math.Max(0, c.StartWaitAt - _sim.Time):0}秒";
            var r = _sim.InFlightTo(c.Id, out double eta);
            if (r == null) return null;
            if (r.State == RunnerState.Lost)
            {
                // 倒れた伝令（本部は知らない）：本部から中隊の今の位置までの予定で数え、過ぎたらそう出す
                var hq = _sim.HeadquartersOf(c.Side);
                eta = hq.HasValue
                    ? r.SentTime + _params.Runners.PrepSec + Vec2.Distance(hq.Value, c.Position) / _params.Runners.SpeedMps - _sim.Time
                    : 0;
                if (eta <= 0) return "未着（到着予定を過ぎた）";
            }
            return $"未着 あと {eta:0}秒";
        }

        /// <summary>上部バーの判断枠「伝令 ●●○」と、会戦開始前の表示。</summary>
        string SlotText()
        {
            if (_sim.BeforeStart) return "<color=#9fe0a0><b>開始前：命令は即座に届く</b></color>";
            int used = _sim.RunnerSlotsUsed(Side.Blue), max = _sim.MaxRunners;
            string marks = new string('●', Math.Min(used, max)) + new string('○', Math.Max(0, max - used));
            return used >= max ? $"<color=#ff9a6a>伝令 {marks}</color>" : $"伝令 {marks}";
        }

        // ---------------------------------------------------------------- 描く

        /// <summary>本部：軍隊符号の旗（竿と、陣営の色の旗）と「本部」の文字。</summary>
        void DrawHeadquarters(float px)
        {
            foreach (var side in new[] { Side.Blue, Side.Red })
            {
                var hq = _sim.HeadquartersOf(side);
                if (!hq.HasValue) continue;
                var p = V(hq.Value);
                var ink = new Color32(20, 20, 20, 240);
                _shapes.Line(p, p + new Vector2(0, 34 * px), 2.5f * px, ink);
                var top = p + new Vector2(0, 34 * px);
                var col = Palette.SideColor(side);
                _shapes.Quad(top, top + new Vector2(24 * px, 0), top + new Vector2(24 * px, -14 * px), top + new Vector2(0, -14 * px), col);
                var q = new[] { top, top + new Vector2(24 * px, 0), top + new Vector2(24 * px, -14 * px), top + new Vector2(0, -14 * px) };
                Outline(q, 1.5f * px, ink);
                _shapes.Circle(p, 3 * px, 3 * px, ink, 10);
                _texts.Draw("本部", p + new Vector2(1.2f * px, -6 * px - 1.2f * px), 13, px, new Color(0, 0, 0, 0.85f), TextAnchor.UpperCenter);
                _texts.Draw("本部", p + new Vector2(0, -6 * px), 13, px, Color.white, TextAnchor.UpperCenter);
            }
        }

        /// <summary>伝令：本部と向かう中隊を結ぶ点線と、その上を動く小さな点（行きは黄、帰りは水色）。倒れた伝令は描かない。</summary>
        void DrawRunners(float px)
        {
            var go = new Color32(255, 220, 60, 255);
            var back = new Color32(120, 210, 255, 255);
            foreach (var r in _sim.Runners)
            {
                if (r.State == RunnerState.Done || r.State == RunnerState.Lost) continue;
                var hq = _sim.HeadquartersOf(r.Side);
                if (!hq.HasValue) continue;
                var c = _sim.Companies[r.CompanyId];
                Pose(c, out var cp, out _);
                bool returning = r.State == RunnerState.Returning;
                var col = returning ? back : go;
                _shapes.DashedLine(V(hq.Value), V(returning ? r.Position : cp), 1.2f * px, 5 * px, Palette.WithAlpha(col, 150));
                _shapes.Circle(V(r.Position), 3.5f * px, 3.5f * px, col, 10);
                _shapes.Circle(V(r.Position), 4.5f * px, 1.2f * px, new Color32(20, 20, 20, 220), 12);
            }
        }

        /// <summary>中隊の駒の脇に、未着・開始待ちの印（青だけ）。</summary>
        void DrawRunnerTags(float px)
        {
            foreach (var c in _sim.Companies)
            {
                if (c.Side != Side.Blue || !c.OnBoard) continue;
                string tag = RunnerTag(c);
                string stage = StageTag(c);
                if (stage != null) tag = tag == null ? stage : tag + "　" + stage;
                if (tag == null) continue;
                Pose(c, out var pos, out double facing);
                Extent(facing, out float ex, out float ey);
                var at = V(pos) + new Vector2(0, ey + 30 * px);
                var col = tag.StartsWith("受領") ? new Color(0.75f, 0.95f, 1f) : new Color(1f, 0.88f, 0.35f);
                _texts.Draw(tag, at + new Vector2(1.2f * px, -1.2f * px), 13, px, new Color(0, 0, 0, 0.85f));
                _texts.Draw(tag, at, 13, px, col);
            }
        }
    }
}
