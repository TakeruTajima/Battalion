using System;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // P2d：霧と報告の表示。霧の中では、地図の赤は本部の報告の駒だけ（誤差の円・何分前・5分で薄く・10分で「?」・30分で消す）。
    // 実際の赤の駒・経路・射撃の線は出さない。会戦が終わったら（戦闘詳報）、実際の赤と報告の駒を重ねて出す。F12 で「霧なし」（検証用）。
    public sealed partial class BattleApp
    {
        bool _fogOn = true;

        /// <summary>報告の駒を選ぶ当たりの最小の半径（誤差の円が小さい駒でも選べるように）。</summary>
        const double PieceHitMinM = 100;

        /// <summary>霧の中か（霧を使う設定で、会戦の途中）。</summary>
        bool Fogged => _fogOn && _phase != Phase.Ended;

        /// <summary>実際の中隊を出すか（青はいつも。赤は霧の外だけ）。</summary>
        bool ShowsActual(Company c) => c.Side == Side.Blue || !Fogged;

        /// <summary>報告の駒を出すか（霧の中と、戦闘詳報の重ね表示）。</summary>
        bool ShowsIntel => _fogOn || _phase == Phase.Ended;

        /// <summary>位置の下の報告の駒（地図から消えた駒を除く。誤差の円の中で最も近い駒）。なければ null。</summary>
        IntelPiece PieceAt(Vec2 p)
        {
            IntelPiece best = null;
            double bestD = double.MaxValue;
            foreach (var i in _sim.Intel)
            {
                if (_sim.LookOf(i, _sim.Time) == PieceLook.Forgotten) continue;
                double d = Vec2.Distance(i.Position, p);
                if (d > Math.Max(i.ErrorM, PieceHitMinM) || d >= bestD) continue;
                best = i;
                bestD = d;
            }
            return best;
        }

        /// <summary>駒の中心から見た、点の方角（地図の方角）。</summary>
        static double BearingFromPiece(IntelPiece i, Vec2 p) => Math.Atan2(p.Y - i.Position.Y, p.X - i.Position.X);

        /// <summary>報告の駒：誤差の薄い円と、赤の駒の形（向きは初めの敵情の向きか動いている向き。分からなければ西向き）。</summary>
        void DrawIntel(float px)
        {
            if (!ShowsIntel) return;
            foreach (var i in _sim.Intel)
            {
                var look = _sim.LookOf(i, _sim.Time);
                if (look == PieceLook.Forgotten && _phase != Phase.Ended) continue;
                byte a = look == PieceLook.Fresh ? (byte)220 : look == PieceLook.Faded ? (byte)120 : (byte)70;
                var col = Palette.SideColor(Side.Red);
                if (i.ErrorM > 0) _shapes.Circle(V(i.Position), (float)i.ErrorM, 1.5f * px, Palette.WithAlpha(col, (byte)(a / 2)), 48);
                double facing = i.HeadingRad ?? i.FacingRad ?? Math.PI;
                HalfSize(null, out double hd, out double hf);
                Corners(i.Position, facing, hd, hf, _q);
                var fill = i.State == ReportedState.Normal ? Palette.WithAlpha(col, (byte)(a / 3)) : Palette.WithAlpha(Palette.Gray, (byte)(a / 3));
                _shapes.Quad(_q[0], _q[1], _q[2], _q[3], fill);
                for (int k = 0; k < 4; k++) _shapes.DashedLine(_q[k], _q[(k + 1) % 4], 1.5f * px, 8 * px, Palette.WithAlpha(col, a));
                if (i.Moving && i.HeadingRad.HasValue)
                {
                    var f = Vec2.FromAngle(i.HeadingRad.Value);
                    _shapes.Line(V(i.Position), V(i.Position + f * (hd + 60)), 2 * px, Palette.WithAlpha(col, a));
                }
            }
        }

        /// <summary>報告の駒の文字：「敵 多い 3分前」、10分で「?」、壊走・降伏の印。</summary>
        void DrawIntelLabels()
        {
            if (!ShowsIntel) return;
            var area = MapArea();
            foreach (var i in _sim.Intel)
            {
                var look = _sim.LookOf(i, _sim.Time);
                if (look == PieceLook.Forgotten && _phase != Phase.Ended) continue;
                var g = WorldToGui(i.Position);
                if (!area.Contains(g)) continue;
                double min = Math.Max(0, (_sim.Time - i.SeenTime) / 60);
                string size = i.Size == EnemySize.Many ? "多い" : i.Size == EnemySize.Half ? "半ば" : "少ない";
                string state = i.State == ReportedState.Routed ? " 壊走中" : i.State == ReportedState.Surrendered ? " 降伏" : "";
                string text = look == PieceLook.Unknown || look == PieceLook.Forgotten ? $"? 敵 {min:0}分前" : $"敵 {size}{state} {min:0}分前";
                var size2 = _small.CalcSize(new GUIContent(text));
                var rect = new Rect(g.x - size2.x / 2, g.y - size2.y / 2, size2.x + 4, size2.y);
                Fill(rect, new Color(0.25f, 0, 0, look == PieceLook.Fresh ? 0.6f : 0.35f));
                GUI.Label(rect, text, _small);
            }
        }

        /// <summary>拘束・攻撃の目標を選ぶとき（霧の中）、カーソルの下の報告の駒を強調する。</summary>
        void DrawHoverPiece(float px)
        {
            var i = _mode == Mode.Attack && _pressing ? _pressPiece : (MouseOverUi() ? null : PieceAt(MouseWorld()));
            if (i == null) return;
            _shapes.Circle(V(i.Position), (float)Math.Max(i.ErrorM, PieceHitMinM), 3 * px, Palette.Hover, 48);
        }
    }
}
