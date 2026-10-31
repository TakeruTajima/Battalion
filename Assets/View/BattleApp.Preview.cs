using System;
using System.Collections.Generic;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 命令の下見（P1e の5）：命令を選んで地図にカーソルを重ねている間（攻撃なら方角をドラッグしている間も）、
    // 目的地（発起点）と経路を薄く描く。出した命令が受け付けられるまで（一時停止中も）、その下見を経路として描く。
    public sealed partial class BattleApp
    {
        Simulation.OrderPreview _hoverPreview;
        Order _hoverOrder;
        string _hoverKey;

        Order _pendingFor;
        long _pendingTick = -1;
        Simulation.OrderPreview _pendingPreview;

        /// <summary>カーソルの下で、今の命令の選択から出すことになる命令（出せなければ null）。</summary>
        Order HoverOrder()
        {
            var c = Selected;
            if (!CanCommand(c)) return null;
            if (!_pressing && MouseOverUi()) return null;
            var m = MouseWorld();
            bool dragged = _pressing && ((Vector2)Input.mousePosition - _pressPx).magnitude > DragThresholdPx;
            double? facing = dragged ? Math.Atan2(m.Y - _pressWorld.Y, m.X - _pressWorld.X) : (double?)null;
            switch (_mode)
            {
                case Mode.Advance:
                    return Order.Advance(c.Id, _pressing ? _pressWorld : m, facing);
                case Mode.Retreat:
                    return Order.Retreat(c.Id, _pressing ? _pressWorld : m, facing);
                case Mode.Entrench:
                {
                    var at = _pressing ? _pressWorld : m;
                    if (UnitAt(at, Side.Blue) == c && !dragged) return null; // その場で掘る
                    return Order.Entrench(c.Id, at, facing);
                }
                case Mode.Pin:
                {
                    if (Fogged)
                    {
                        var piece = PieceAt(m);
                        return piece == null ? null : Order.PinPiece(c.Id, piece.Id, piece.Position, _pinRange);
                    }
                    var tgt = UnitAt(m, Side.Red);
                    return tgt == null ? null : Order.Pin(c.Id, tgt.Id, _pinRange);
                }
                case Mode.Attack:
                {
                    if (Fogged)
                    {
                        if (AttackDragging) return Order.AttackPieceFrom(c.Id, _pressPiece.Id, _pressPiece.Position, BearingFromPiece(_pressPiece, m), _onBreak);
                        var piece = _pressing ? _pressPiece : PieceAt(m);
                        return piece == null ? null : Order.AttackPiece(c.Id, piece.Id, piece.Position, _attackAspect, _onBreak);
                    }
                    if (AttackDragging) return Order.AttackFrom(c.Id, _pressTarget.Id, BearingTo(_pressTarget, m), _onBreak);
                    var tgt = _pressing ? _pressTarget : UnitAt(m, Side.Red);
                    return tgt == null ? null : Order.Attack(c.Id, tgt.Id, _attackAspect, _onBreak);
                }
                default:
                    return null;
            }
        }

        /// <summary>下見を計算し直す。命令の中身（目的地のマス、方角は 2° 刻み）か刻みが変わったときだけ。</summary>
        void UpdateHoverPreview()
        {
            var o = HoverOrder();
            if (o == null)
            {
                _hoverOrder = null;
                _hoverPreview = null;
                _hoverKey = null;
                return;
            }
            string key = PreviewKey(o);
            if (key == _hoverKey) return;
            _hoverKey = key;
            _hoverOrder = o;
            _hoverPreview = _sim.Preview(o);
        }

        string PreviewKey(Order o)
        {
            string at = "";
            if (o.Destination.HasValue)
            {
                _sim.Map.CellOf(o.Destination.Value, out int i, out int j);
                at = $"{i},{j}";
            }
            string b = o.BearingRad.HasValue ? Math.Round(o.BearingRad.Value * Angles.RadToDeg / 2).ToString() : "";
            string f = o.FacingRad.HasValue ? Math.Round(o.FacingRad.Value * Angles.RadToDeg / 5).ToString() : "";
            return $"{_sim.Tick}|{o.CompanyId}|{o.Type}|{o.TargetId}|{o.Aspect}|{o.PinRange}|{at}|{b}|{f}";
        }

        /// <summary>
        /// 中隊のまだ始まっていない命令（次の刻みで受け付ける、開始待ち、伝令が運んでいる）の下見（なければ null）。
        /// 命令か刻みが変わったときだけ計算し直す。
        /// </summary>
        Simulation.OrderPreview PendingPreview(Company c)
        {
            var o = _sim.PendingOrderOf(c.Id) ?? c.StartWaitOrder ?? _sim.InFlightTo(c.Id, out _)?.Order;
            if (o == null) return null;
            if (!ReferenceEquals(o, _pendingFor) || _pendingTick != _sim.Tick)
            {
                _pendingFor = o;
                _pendingTick = _sim.Tick;
                _pendingPreview = _sim.Preview(o);
            }
            return _pendingPreview;
        }

        /// <summary>経路と目的地（四角）。到着後の向きがあれば矢印。</summary>
        void DrawPreviewPath(Simulation.OrderPreview pv, Order o, float px, Color32 col, Color32 shadow)
        {
            if (pv == null || pv.Stays || pv.Path.Count < 2) return;
            var pts = new List<Vector2>(pv.Path.Count);
            foreach (var p in pv.Path) pts.Add(V(p));
            _shapes.Polyline(pts, 4 * px, shadow);
            _shapes.Polyline(pts, 2 * px, col);
            var g = V(pv.Goal);
            float s = 6 * px;
            _shapes.Quad(g + new Vector2(-s, -s), g + new Vector2(s, -s), g + new Vector2(s, s), g + new Vector2(-s, s), col);
            if (o.FacingRad.HasValue) _shapes.Arrow(g, g + V(Vec2.FromAngle(o.FacingRad.Value)) * 30 * px, 2 * px, col);
        }

        /// <summary>
        /// カーソルの下の命令の下見を薄く描く。攻撃では、目標の中心から発起点への線と、結果の向き（正面・側面・背面）の文字。
        /// 方角をドラッグしている間は、目標の中心からカーソルへの矢印も描く。
        /// </summary>
        void DrawHoverPreview(float px)
        {
            UpdateHoverPreview();
            if (_hoverPreview == null) return;
            var faint = new Color32(255, 255, 255, 120);
            DrawPreviewPath(_hoverPreview, _hoverOrder, px, faint, new Color32(0, 0, 0, 60));
            if (_hoverOrder.Type != OrderType.Attack || !_hoverPreview.BearingRad.HasValue) return;
            var tgt = _sim.Companies[_hoverOrder.TargetId];
            Pose(tgt, out var tp, out _);
            var jump = tgt.Position + Vec2.FromAngle(_hoverPreview.BearingRad.Value) * _params.MapRules.AttackStandoffM;
            if (AttackDragging) _shapes.Arrow(V(tp), V(MouseWorld()), 3 * px, Palette.Selected);
            _shapes.DashedLine(V(tp), V(jump), 2 * px, 7 * px, Palette.Hover);
            _shapes.Circle(V(jump), 9 * px, 2 * px, Palette.Hover, 20);
            string label = AspectName(_hoverPreview.Aspect) + (_hoverOrder.BearingRad.HasValue ? "（方角を指定）" : "");
            _texts.Draw(label, V(jump) + new Vector2(1.5f * px, 12 * px - 1.5f * px), 14, px, new Color(0, 0, 0, 0.85f));
            _texts.Draw(label, V(jump) + new Vector2(0, 12 * px), 14, px, new Color(1f, 0.94f, 0.4f));
        }
    }
}
