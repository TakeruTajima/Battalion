using System;
using System.Collections.Generic;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 地図の上に描くもの：駒（実寸の矩形、×印、向き）、選んだ中隊の経路と目的地、攻撃の側の強調、デバッグ表示
    public sealed partial class BattleApp
    {
        void LateUpdate()
        {
            if (_phase == Phase.SelectStage || _shapes == null) return;
            _shapes.Clear();
            float px = PixelsToWorld(1);

            if (_showTrigger) DrawTrigger(px);
            DrawSelectedPath(px);
            foreach (var c in _sim.Companies)
                if (c.State != CompanyState.LeftBattle) DrawUnit(c, px);
            if (_showEscape) DrawEscapes(px);
            DrawHover(px);
            DrawDragPreview(px);
            if (_sightFrom.HasValue) _shapes.Circle(V(_sightFrom.Value), 6 * px, 2 * px, Palette.Selected, 16);

            _shapes.Apply();
        }

        static Vector2 V(Vec2 p) => new Vector2((float)p.X, (float)p.Y);

        void Corners(Vec2 pos, double facing, double halfDepth, double halfFront, Vector2[] into)
        {
            UnitGeometry.Axes(facing, out var f, out var l);
            Vec2 a = pos + f * halfDepth + l * halfFront, b = pos + f * halfDepth - l * halfFront;
            Vec2 c = pos - f * halfDepth - l * halfFront, d = pos - f * halfDepth + l * halfFront;
            into[0] = V(a);
            into[1] = V(b);
            into[2] = V(c);
            into[3] = V(d);
        }

        readonly Vector2[] _q = new Vector2[4];

        /// <summary>
        /// 駒：正面幅 200m×奥行 50m の実寸の矩形（味方は青、敵は橙）、歩兵の×印、正面の縁の三角（向き）。
        /// 動揺は薄く、壊走は枠だけの破線、再集結は薄い塗りに破線、降伏は灰色。
        /// </summary>
        void DrawUnit(Company c, float px)
        {
            Pose(c, out var pos, out double facing);
            double hd = _params.Movement.DepthM / 2, hf = _params.Movement.FrontageM / 2;
            Corners(pos, facing, hd, hf, _q);

            var baseCol = Palette.SideColor(c.Side);
            var dark = Palette.SideDarkColor(c.Side);
            bool dashed = c.State == CompanyState.Routed || c.State == CompanyState.Rallying;
            Color32 fill;
            switch (c.State)
            {
                case CompanyState.Shaken: fill = Palette.Lighten(baseCol, 0.45f); break;
                case CompanyState.Routed: fill = Palette.WithAlpha(baseCol, 40); break;
                case CompanyState.Rallying: fill = Palette.WithAlpha(Palette.Lighten(baseCol, 0.5f), 120); break;
                case CompanyState.Surrendered: fill = Palette.Gray; dark = new Color32(80, 80, 80, 255); break;
                default: fill = baseCol; break;
            }

            _shapes.Quad(_q[0], _q[1], _q[2], _q[3], fill);
            float w = 1.5f * px;
            for (int k = 0; k < 4; k++)
            {
                if (dashed) _shapes.DashedLine(_q[k], _q[(k + 1) % 4], w, 6 * px, dark);
                else _shapes.Line(_q[k], _q[(k + 1) % 4], w, dark);
            }
            // 歩兵の×印
            _shapes.Line(_q[0], _q[2], w, dark);
            _shapes.Line(_q[1], _q[3], w, dark);
            // 向き：正面の縁の中央から前へ出る三角
            UnitGeometry.Axes(facing, out var fv, out var lv);
            var tip = V(pos + fv * (hd + Math.Max(18, 8 * px)));
            var b1 = V(pos + fv * hd + lv * Math.Max(14, 6 * px));
            var b2 = V(pos + fv * hd - lv * Math.Max(14, 6 * px));
            _shapes.Triangle(tip, b1, b2, dark);

            if (c.Id == _selected)
            {
                Corners(pos, facing, hd + 6 * px, hf + 6 * px, _q);
                for (int k = 0; k < 4; k++) _shapes.Line(_q[k], _q[(k + 1) % 4], 2.5f * px, Palette.Selected);
            }
        }

        /// <summary>選んだ中隊の経路（残りの点の列）と目的地。攻撃・拘束は目標への破線も描く。</summary>
        void DrawSelectedPath(float px)
        {
            var c = Selected;
            if (c == null || !c.OnBoard) return;
            Pose(c, out var pos, out _);
            var o = c.CurrentOrder;
            if (o.Moves && !c.Arrived && c.Path != null && c.PathIndex < c.Path.Count)
            {
                var pts = new List<Vector2> { V(pos) };
                for (int k = c.PathIndex; k < c.Path.Count; k++) pts.Add(V(c.Path[k]));
                _shapes.Polyline(pts, 4 * px, Palette.PathShadow);
                _shapes.Polyline(pts, 2 * px, Palette.Path);
                var g = V(c.MoveGoal);
                float s = 6 * px;
                _shapes.Quad(g + new Vector2(-s, -s), g + new Vector2(s, -s), g + new Vector2(s, s), g + new Vector2(-s, s), Palette.Path);
                if (o.FacingRad.HasValue)
                    _shapes.Arrow(g, g + new Vector2(Mathf.Cos((float)o.FacingRad.Value), Mathf.Sin((float)o.FacingRad.Value)) * 30 * px, 2 * px, Palette.Path);
            }
            if ((o.Type == OrderType.Attack || o.Type == OrderType.Pin) && o.TargetId >= 0)
            {
                var t = _sim.Companies[o.TargetId];
                if (t.OnBoard)
                {
                    Pose(t, out var tp, out _);
                    _shapes.DashedLine(V(pos), V(tp), 2 * px, 8 * px, Palette.Hover);
                }
            }
        }

        /// <summary>
        /// 拘束・攻撃の目標を選ぶとき、カーソルの下の敵の駒を強調する。攻撃で側面を選んでいるときは、
        /// カーソルが長辺の端の 1/3 にあればその側の端を、中央ならその旨を示す（Sim が経路コストで選ぶ）。
        /// </summary>
        void DrawHover(float px)
        {
            if (_mode != Mode.Attack && _mode != Mode.Pin) return;
            if (MouseOverUi()) return;
            var m = MouseWorld();
            var tgt = UnitAt(m, Side.Red);
            if (tgt == null) return;
            Pose(tgt, out var pos, out double facing);
            double hd = _params.Movement.DepthM / 2, hf = _params.Movement.FrontageM / 2;
            float w = 3 * px;

            if (_mode == Mode.Attack && _attackAspect == AttackAspect.Flank)
            {
                int side = UnitGeometry.FlankSideAt(pos, facing, _params.Movement.FrontageM, m);
                if (side != 0)
                {
                    // その側の端の 1/3 を囲み、側面の向きに矢印を出す
                    UnitGeometry.Axes(facing, out var fv, out var lv);
                    double inner = hf / 3, outer = hf + 4 * px;
                    var a = V(pos + fv * (hd + 4 * px) + lv * (side * inner));
                    var b = V(pos + fv * (hd + 4 * px) + lv * (side * outer));
                    var c = V(pos - fv * (hd + 4 * px) + lv * (side * outer));
                    var d = V(pos - fv * (hd + 4 * px) + lv * (side * inner));
                    _shapes.Line(a, b, w, Palette.Hover);
                    _shapes.Line(b, c, w, Palette.Hover);
                    _shapes.Line(c, d, w, Palette.Hover);
                    _shapes.Line(d, a, w, Palette.Hover);
                    var mid = V(pos + lv * (side * outer));
                    _shapes.Arrow(V(pos + lv * (side * (outer + 60))), mid, w, Palette.Hover);
                    return;
                }
            }
            Corners(pos, facing, hd + 4 * px, hf + 4 * px, _q);
            for (int k = 0; k < 4; k++) _shapes.Line(_q[k], _q[(k + 1) % 4], w, Palette.Hover);
        }

        /// <summary>ドラッグ中の向きの矢印（前進・後退・保持）。</summary>
        void DrawDragPreview(float px)
        {
            if (!_pressing || (_mode != Mode.Advance && _mode != Mode.Retreat && _mode != Mode.HoldFacing)) return;
            if (((Vector2)Input.mousePosition - _pressPx).magnitude <= DragThresholdPx) return;
            _shapes.Arrow(V(_pressWorld), V(MouseWorld()), 3 * px, Palette.Selected);
        }

        /// <summary>予備隊の台本の引き金の円（発動したら灰色）。</summary>
        void DrawTrigger(float px)
        {
            foreach (var s in _sim.Scripts)
            {
                if (!(s is ReserveScript r)) continue;
                _shapes.Circle(V(r.Center), (float)r.Radius, 3 * px, r.Triggered ? Palette.TriggerFired : Palette.Trigger, 96);
                _shapes.Circle(V(r.Center), 5 * px, 3 * px, r.Triggered ? Palette.TriggerFired : Palette.Trigger, 12);
            }
        }

        /// <summary>
        /// 退路の見積もり：指揮下と壊走中の中隊ごとに、16方位の線（短い・灰色ほど安全、長い・赤ほど危ない）と、
        /// 最も損害の少ない方位の矢印（緑。逃げ道がなければ赤）。
        /// </summary>
        void DrawEscapes(float px)
        {
            double threshold = _params.Encirclement.EscapeLossThreshold;
            foreach (var c in _sim.Companies)
            {
                if (!c.OnBoard || !(c.UnderCommand || c.State == CompanyState.Routed)) continue;
                var est = EscapeOf(c);
                Pose(c, out var pos, out _);
                var p = V(pos);
                for (int k = 0; k < est.Loss.Length; k++)
                {
                    double r = Math.Min(1.0, est.Loss[k] / threshold);
                    var dir = V(Vec2.FromAngle(_sim.EscapeDirection(k)));
                    var col = r >= 1.0 ? Palette.EscapeNone : Palette.WithAlpha(Palette.EscapeRay, (byte)(80 + 140 * r));
                    _shapes.Line(p, p + dir * (float)(40 + 200 * r), 1.5f * px, col);
                }
                bool none = est.MinLoss >= threshold;
                var best = V(Vec2.FromAngle(_sim.EscapeDirection(est.Best)));
                _shapes.Arrow(p, p + best * 260, 3 * px, none ? Palette.EscapeNone : Palette.EscapeBest);
            }
        }
    }
}
