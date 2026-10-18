using System;
using System.Collections.Generic;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 地図の上に描くもの：村の目標、射撃線、駒（実寸の矩形、×印、向き、行動の印、バー、明滅）、選んだ中隊の経路と目的地、
    // 命令の下見、デバッグ表示、浮かぶ文字（損害の数字・大きな出来事）
    public sealed partial class BattleApp
    {
        TextPool _texts;

        // 射撃線の破線が流れる速さ（現実の時間で一定。倍速を上げても読める速さのまま）
        const float TracerSpeedPx = 70f;
        const float TracerDashPx = 9f;

        void LateUpdate()
        {
            if (_phase == Phase.SelectStage || _shapes == null) return;
            if (_texts == null) _texts = new TextPool(_world, UiFont());
            _shapes.Clear();
            _texts.Begin();
            float px = PixelsToWorld(1);
            float now = Time.unscaledTime;

            DrawContourLabels(px);
            DrawHeadquarters(px);
            DrawVillage(px);
            if (_showTrigger) DrawTrigger(px);
            DrawBlockedSights(px);
            DrawFireLines(px, now);
            DrawSelectedPath(px);
            DrawSheetPreviews(px);
            foreach (var c in _sim.Companies)
                if (c.State != CompanyState.LeftBattle) DrawUnit(c, px, now);
            if (_showEscape) DrawEscapes(px);
            DrawRunners(px);
            DrawRunnerTags(px);
            DrawHover(px);
            DrawHoverPreview(px);
            DrawDragPreview(px);
            if (_sightFrom.HasValue) _shapes.Circle(V(_sightFrom.Value), 6 * px, 2 * px, Palette.Selected, 16);
            DrawFloatTexts(px, now);
            DrawProfile(px);

            _shapes.Apply();
            _texts.End();
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

        void Outline(Vector2[] q, float w, Color32 col)
        {
            for (int k = 0; k < 4; k++) _shapes.Line(q[k], q[(k + 1) % 4], w, col);
        }

        /// <summary>駒の矩形の、世界の x・y 方向の半分の広がり（向きで変わる）。印やバーを駒の外に置くのに使う。</summary>
        void Extent(double facing, out float ex, out float ey)
        {
            UnitGeometry.Axes(facing, out var f, out var l);
            double hd = _params.Movement.DepthM / 2, hf = _params.Movement.FrontageM / 2;
            ex = (float)(Math.Abs(f.X) * hd + Math.Abs(l.X) * hf);
            ey = (float)(Math.Abs(f.Y) * hd + Math.Abs(l.Y) * hf);
        }

        // ---------------------------------------------------------------- 村の目標

        /// <summary>村の中心に旗、中心から 250m の円（判定の範囲）。旗の色は状態（青が確保・赤が確保・争奪中・空き）。</summary>
        void DrawVillage(float px)
        {
            var center = V(MapScenario.VillageCenter);
            var st = _objective.State;
            _shapes.Circle(center, (float)MapScenario.VillageHoldRadiusM, 1.5f * px, new Color32(255, 255, 255, 70), 96);

            // 旗竿と旗（画面で一定の大きさ）
            float h = 34 * px, fw = 22 * px, fh = 14 * px;
            var top = center + new Vector2(0, h);
            _shapes.Line(center, top, 2.5f * px, new Color32(30, 30, 30, 255));
            var a = top + new Vector2(0, -fh);
            var b = top + new Vector2(fw, -fh);
            var c = top + new Vector2(fw, 0);
            switch (st)
            {
                case VillageState.Blue:
                    _shapes.Quad(a, b, c, top, Palette.Blue);
                    break;
                case VillageState.Red:
                    _shapes.Quad(a, b, c, top, Palette.Red);
                    break;
                case VillageState.Contested:
                    // 争奪中：青と赤の縞
                    for (int k = 0; k < 4; k++)
                    {
                        float x0 = fw * k / 4, x1 = fw * (k + 1) / 4;
                        _shapes.Quad(a + new Vector2(x0, 0), a + new Vector2(x1, 0), top + new Vector2(x1, 0), top + new Vector2(x0, 0),
                            k % 2 == 0 ? Palette.Blue : Palette.Red);
                    }
                    break;
                default:
                    // 空き：白抜き
                    _shapes.Quad(a, b, c, top, new Color32(240, 240, 240, 200));
                    break;
            }
            var q = new[] { a, b, c, top };
            Outline(q, 1.5f * px, new Color32(30, 30, 30, 255));
            var label = "村：" + ObjectiveStatus.Name(st);
            var at = center + new Vector2(0, -6 * px);
            _texts.Draw(label, at + new Vector2(1.5f * px, -1.5f * px), 15, px, new Color(0, 0, 0, 0.85f), TextAnchor.UpperCenter);
            _texts.Draw(label, at, 15, px, Color.Lerp(VillageColor(st), Color.white, 0.35f), TextAnchor.UpperCenter);
        }

        // ---------------------------------------------------------------- 射撃

        /// <summary>
        /// 射撃線：直前の刻みに撃った組だけ。色は撃たれる側から見た向き（正面＝白、側面＝橙、背面＝赤）、太さは損害の期待値（毎分）。
        /// 線の上を、目標へ向かって破線が流れる（曳光弾）。選んだ中隊が撃つ・撃たれる線を強く、それ以外を弱く描く。
        /// </summary>
        void DrawFireLines(float px, float now)
        {
            if (_lastFire.Count == 0) return;
            bool anySelected = Selected != null;
            foreach (var f in _lastFire)
            {
                var s = _sim.Companies[f.Shooter];
                var t = _sim.Companies[f.Target];
                Pose(s, out var sp, out double sf);
                Pose(t, out var tp, out _);
                // 射手の正面の縁の中央から出す
                UnitGeometry.Axes(sf, out var fv, out _);
                var from = V(sp + fv * (_params.Movement.DepthM / 2));
                var to = V(tp);

                bool focus = !anySelected || f.Shooter == _selected || f.Target == _selected;
                byte alpha = focus ? (byte)235 : (byte)140;
                var col = Palette.WithAlpha(AspectColor32(f.Aspect), alpha);
                float w = Mathf.Clamp(1f + (float)f.ExpectedPerMin * 0.9f, 1f, 8f) * px;
                _shapes.Line(from, to, w, Palette.WithAlpha(col, (byte)(alpha / 3)));
                _shapes.DashedLine(from, to, w * 1.3f, TracerDashPx * px, now * TracerSpeedPx * px, col);
            }
        }

        static Color32 AspectColor32(Aspect a)
        {
            switch (a)
            {
                case Aspect.Flank: return new Color32(255, 158, 40, 255);
                case Aspect.Rear: return new Color32(255, 60, 50, 255);
                default: return new Color32(255, 255, 255, 255);
            }
        }

        // ---------------------------------------------------------------- 駒

        /// <summary>
        /// 駒：正面幅 200m×奥行 50m の実寸の矩形（味方は青、敵は橙）、歩兵の×印、正面の縁の三角（向き）。
        /// 動揺は薄く、壊走は枠だけの破線、再集結は薄い塗りに破線、降伏は灰色に白旗。
        /// 重ねて：籠城は二重枠、拘束されているときは点線の輪、包囲は脈打つ赤い輪、被弾の明滅、発砲の明滅、行動の印、兵力と士気のバー。
        /// </summary>
        void DrawUnit(Company c, float px, float now)
        {
            Pose(c, out var pos, out double facing);
            double hd = _params.Movement.DepthM / 2, hf = _params.Movement.FrontageM / 2;
            Corners(pos, facing, hd, hf, _q);
            var fx = _fx[c.Id];

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

            // 被弾の明滅（枠の外側を光らせる。側面・背面からは太く強く）
            float hitAge = now - fx.HitTime;
            if (c.OnBoard && hitAge < HitFlashSec)
            {
                float k = 1 - hitAge / HitFlashSec;
                bool strong = fx.HitAspect != Aspect.Front;
                var hc = AspectColor32(fx.HitAspect);
                Corners(pos, facing, hd + (strong ? 5 : 3) * px, hf + (strong ? 5 : 3) * px, _q);
                Outline(_q, (strong ? 6f : 3f) * px, Palette.WithAlpha(hc, (byte)((strong ? 230 : 150) * k)));
                Corners(pos, facing, hd, hf, _q);
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

            if (c.State == CompanyState.Surrendered)
            {
                DrawWhiteFlag(V(pos), px);
                return;
            }
            if (!c.OnBoard) return;

            // 籠城：二重枠
            if (c.UnderCommand && c.Fortified)
            {
                Corners(pos, facing, hd + 4 * px, hf + 4 * px, _q);
                Outline(_q, 1.5f * px, dark);
            }

            Extent(facing, out float ex, out float ey);
            float ring = Mathf.Sqrt(ex * ex + ey * ey);
            // 拘束されている（150m 以内から撃たれている）：細い点線の輪
            if (c.UnderCommand && c.Pinned) DottedCircle(V(pos), ring + 10 * px, 1.5f * px, 5 * px, new Color32(255, 255, 255, 180));
            // 包囲：脈打つ赤い輪
            if (c.UnderCommand && c.Encircled)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(now * 6f);
                _shapes.Circle(V(pos), ring + (18 + 5 * pulse) * px, (2.5f + 2f * pulse) * px, new Color32(235, 40, 40, (byte)(150 + 100 * pulse)), 64);
            }

            if (c.Id == _selected)
            {
                Corners(pos, facing, hd + 7 * px, hf + 7 * px, _q);
                Outline(_q, 2.5f * px, Palette.Selected);
            }

            DrawMuzzle(c, pos, fv, lv, hd, hf, px, now);
            DrawDisarray(c, pos, facing, px);
            if (c.UnderCommand) DrawAction(c, pos, facing, fv, lv, hd, hf, ex, ey, px);
            DrawBars(c, pos, ey, px);
        }

        void DottedCircle(Vector2 center, float radius, float width, float dotPx, Color32 col)
        {
            int n = Mathf.Clamp((int)(2 * Mathf.PI * radius / (dotPx * 2)), 12, 120);
            for (int k = 0; k < n; k++)
            {
                float a0 = Mathf.PI * 2 * k / n, a1 = a0 + Mathf.PI / n;
                _shapes.Line(center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius,
                    center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius, width, col);
            }
        }

        void DrawWhiteFlag(Vector2 at, float px)
        {
            var top = at + new Vector2(0, 30 * px);
            _shapes.Line(at, top, 2 * px, new Color32(40, 40, 40, 255));
            var a = top + new Vector2(0, -12 * px);
            var b = top + new Vector2(18 * px, -12 * px);
            var c = top + new Vector2(18 * px, 0);
            _shapes.Quad(a, b, c, top, new Color32(250, 250, 250, 255));
            Outline(new[] { a, b, c, top }, 1f * px, new Color32(40, 40, 40, 255));
        }

        /// <summary>発砲の明滅：直前の刻みに撃った中隊の正面の辺に沿って、小さな明るい点をちらつかせる（数は兵力に比例）。</summary>
        void DrawMuzzle(Company c, Vec2 pos, Vec2 fv, Vec2 lv, double hd, double hf, float px, float now)
        {
            // 直前の刻みに撃った中隊だけ（一時停止中も、止めた刻みに撃っていた中隊はちらつかせ続ける）
            if (!_lastFire.Exists(f => f.Shooter == c.Id)) return;
            int n = Mathf.Clamp(c.Strength / 15, 1, 10);
            float s = 2.5f * px;
            for (int k = 0; k < n; k++)
            {
                if (_fxRng.NextDouble() < 0.45) continue;
                double along = (k + 0.5) / n * 2 - 1 + (_fxRng.NextDouble() - 0.5) * 0.12;
                var p = V(pos + fv * (hd + 4 * px) + lv * (along * hf * 0.95));
                var col = _fxRng.NextDouble() < 0.5 ? new Color32(255, 250, 200, 255) : new Color32(255, 210, 90, 255);
                _shapes.Quad(p + new Vector2(-s, -s), p + new Vector2(s, -s), p + new Vector2(s, s), p + new Vector2(-s, s), col);
            }
        }

        /// <summary>
        /// 行動の印（形で示す）：移動中は進む向きの山形、保持は短い横棒、拘束の命令は目標へ向けた杭、
        /// 陣地構築は正面の辺に沿って進捗に比例して伸びる斜線の帯。
        /// 動き方（P1e の2）：横歩きは山形を橙に、後ろ向きは山形の根元に横棒、旋回中は駒の脇に今の向きから進む方角への白い弧。
        /// </summary>
        void DrawAction(Company c, Vec2 pos, double facing, Vec2 fv, Vec2 lv, double hd, double hf, float ex, float ey, float px)
        {
            var o = c.CurrentOrder;
            var ink = new Color32(20, 20, 20, 230);
            var moved = c.Position - _prevPos[c.Id];
            bool moving = moved.Length > 1e-6 || (o.Moves && !c.Arrived);

            // 向き直り（P1d の5）：待ちの間は点線の弧、旋回中は実線の弧の矢印
            DrawTurn(c, pos, facing, ex, ey, px);

            // 追撃中（P1d の4）：赤い山形と、追っている中隊への細い破線
            if (c.Pursuing && c.PursuitTargetId >= 0)
            {
                var pt = _sim.Companies[c.PursuitTargetId];
                Pose(pt, out var tp, out _);
                var red = new Color32(220, 40, 30, 240);
                _shapes.DashedLine(V(pos), V(tp), 1.5f * px, 6 * px, red);
                var dir = tp - pos;
                if (dir.Length > 1e-6)
                {
                    var u = dir * (1.0 / dir.Length);
                    var n = new Vec2(-u.Y, u.X);
                    double reach = Math.Abs(u.X) * ex + Math.Abs(u.Y) * ey;
                    for (int k = 0; k < 2; k++)
                    {
                        var apex = pos + u * (reach + (16 + 9 * k) * px);
                        var back = apex - u * (8 * px);
                        _shapes.Line(V(back + n * (7 * px)), V(apex), 3f * px, red);
                        _shapes.Line(V(back - n * (7 * px)), V(apex), 3f * px, red);
                    }
                }
                return;
            }

            if (o.Type == OrderType.Entrench && (!o.Moves || c.Arrived))
            {
                double frac = Math.Min(1.0, c.EntrenchProgress / _params.MapRules.EntrenchSec);
                DrawHatch(pos + fv * (hd + 9 * px), lv, hf, frac, 7 * px, px, new Color32(120, 85, 40, 255));
                return;
            }
            if (o.Type == OrderType.Pin && o.TargetId >= 0)
            {
                var t = _sim.Companies[o.TargetId];
                var dir = t.Position - pos;
                if (dir.Length > 1e-6)
                {
                    var u = dir * (1.0 / dir.Length);
                    var n = new Vec2(-u.Y, u.X);
                    var p0 = pos + fv * hd;
                    var p1 = p0 + u * (26 * px);
                    _shapes.Line(V(p0), V(p1), 2.5f * px, ink);
                    _shapes.Line(V(p1 - n * (8 * px)), V(p1 + n * (8 * px)), 2.5f * px, ink);
                }
                return;
            }
            if (moving)
            {
                // 進む向き（今の刻みの動き。止まって旋回中なら目的地への向き）
                Vec2 dir = moved.Length > 1e-6 ? moved : (c.Path != null && c.PathIndex < c.Path.Count ? c.Path[c.PathIndex] - pos : fv);
                if (dir.Length < 1e-9) return;
                var u = dir * (1.0 / dir.Length);
                var n = new Vec2(-u.Y, u.X);
                double reach = Math.Abs(u.X) * ex + Math.Abs(u.Y) * ey;
                var col = c.MoveStyle == MoveStyle.Sideways ? new Color32(235, 140, 20, 245) : ink;
                for (int k = 0; k < 2; k++)
                {
                    var apex = pos + u * (reach + (16 + 9 * k) * px);
                    var back = apex - u * (8 * px);
                    _shapes.Line(V(back + n * (7 * px)), V(apex), 2.5f * px, col);
                    _shapes.Line(V(back - n * (7 * px)), V(apex), 2.5f * px, col);
                }
                if (c.MoveStyle == MoveStyle.Backward)
                {
                    // 後ろ向き：山形の根元に横棒（敵に正面を向けたまま下がる）
                    var bar = pos + u * (reach + 5 * px);
                    _shapes.Line(V(bar + n * (9 * px)), V(bar - n * (9 * px)), 2.5f * px, ink);
                }
                if (c.MoveStyle == MoveStyle.Turning)
                    DrawArcArrow(pos, facing, Math.Atan2(u.Y, u.X), Mathf.Sqrt(ex * ex + ey * ey) + 10 * px, new Color32(255, 255, 255, 235), px);
                if (c.SeekingSight) DrawEye(pos + u * (reach + 46 * px), px);
                return;
            }
            if (o.Type == OrderType.Hold || ((o.Type == OrderType.Advance || o.Type == OrderType.Retreat) && c.Arrived))
            {
                // 保持：駒の右に短い横棒
                var p = V(pos) + new Vector2(ex + 12 * px, 0);
                _shapes.Line(p, p + new Vector2(14 * px, 0), 3 * px, ink);
            }
        }

        /// <summary>向き直りの印：駒の脇に、今の向きから向き直る向きへの弧（待ちの間は点線、旋回中は実線で矢じり付き）。</summary>
        void DrawTurn(Company c, Vec2 pos, double facing, float ex, float ey, float px)
        {
            bool waiting = c.TurnWaitStart.HasValue;
            bool turning = c.FacingOverride.HasValue && Math.Abs(Angles.Wrap(c.FacingOverride.Value - c.Facing)) > 1e-6;
            if (!waiting && !turning) return;
            float r = Mathf.Sqrt(ex * ex + ey * ey) + 14 * px;
            double from = facing;
            double to;
            if (turning) to = c.FacingOverride.Value;
            else
            {
                var s = _sim.Companies[Math.Max(0, c.LastFlankShooter)].Position - pos;
                to = Math.Atan2(s.Y, s.X);
            }
            double sweep = Angles.Wrap(to - from);
            int n = Mathf.Clamp((int)(Math.Abs(sweep) / 0.12), 3, 40);
            var col = new Color32(255, 230, 90, 240);
            Vector2 prev = V(pos + Vec2.FromAngle(from) * r);
            for (int k = 1; k <= n; k++)
            {
                double a = from + sweep * k / n;
                var p = V(pos + Vec2.FromAngle(a) * r);
                if (!waiting || k % 2 == 0) _shapes.Line(prev, p, (turning ? 3f : 2f) * px, col);
                prev = p;
            }
            if (turning)
            {
                var tip = V(pos + Vec2.FromAngle(to) * r);
                var tangent = V(Vec2.FromAngle(to + Math.Sign(sweep) * Math.PI / 2));
                var normal = V(Vec2.FromAngle(to));
                _shapes.Triangle(tip + tangent * 8 * px, tip - tangent * 4 * px + normal * 6 * px, tip - tangent * 4 * px - normal * 6 * px, col);
            }
        }

        /// <summary>見える位置を探して動いている印（P1g）：山形の先に、目の形（横長の輪と黒目）。</summary>
        void DrawEye(Vec2 at, float px)
        {
            var c = V(at);
            var ink = new Color32(20, 20, 20, 235);
            var white = new Color32(255, 255, 255, 235);
            const int n = 16;
            var prev = c + new Vector2(10 * px, 0);
            for (int k = 1; k <= n; k++)
            {
                double a = 2 * Math.PI * k / n;
                var p = c + new Vector2((float)(Math.Cos(a) * 10 * px), (float)(Math.Sin(a) * 5.5 * px));
                _shapes.Triangle(c, prev, p, white);
                _shapes.Line(prev, p, 2 * px, ink);
                prev = p;
            }
            _shapes.Circle(c, 2.5f * px, 2.5f * px, ink, 10);
        }

        /// <summary>駒の脇の、<paramref name="from"/> から <paramref name="to"/> への矢じり付きの弧（旋回中の印）。</summary>
        void DrawArcArrow(Vec2 pos, double from, double to, float r, Color32 col, float px)
        {
            double sweep = Angles.Wrap(to - from);
            if (Math.Abs(sweep) < 1e-3) return;
            int n = Mathf.Clamp((int)(Math.Abs(sweep) / 0.12), 3, 40);
            Vector2 prev = V(pos + Vec2.FromAngle(from) * r);
            for (int k = 1; k <= n; k++)
            {
                var p = V(pos + Vec2.FromAngle(from + sweep * k / n) * r);
                _shapes.Line(prev, p, 2.5f * px, col);
                prev = p;
            }
            var tip = V(pos + Vec2.FromAngle(to) * r);
            var tangent = V(Vec2.FromAngle(to + Math.Sign(sweep) * Math.PI / 2));
            var normal = V(Vec2.FromAngle(to));
            _shapes.Triangle(tip + tangent * 7 * px, tip - tangent * 4 * px + normal * 5 * px, tip - tangent * 4 * px - normal * 5 * px, col);
        }

        /// <summary>隊形の乱れの印：駒の枠の外側に、ぎざぎざの線（30秒）。</summary>
        void DrawDisarray(Company c, Vec2 pos, double facing, float px)
        {
            if (c.DisarrayUntil < _sim.Time || !c.UnderCommand) return;
            double hd = _params.Movement.DepthM / 2 + 6 * px, hf = _params.Movement.FrontageM / 2 + 6 * px;
            Corners(pos, facing, hd, hf, _q);
            var col = new Color32(255, 140, 40, 230);
            float amp = 4 * px;
            for (int k = 0; k < 4; k++)
            {
                var a = _q[k];
                var b = _q[(k + 1) % 4];
                float len = Vector2.Distance(a, b);
                var dir = (b - a) / len;
                var nrm = new Vector2(-dir.y, dir.x);
                int teeth = Mathf.Max(2, (int)(len / (10 * px)));
                Vector2 prev = a;
                for (int t = 1; t <= teeth; t++)
                {
                    var p = a + dir * (len * t / teeth) + nrm * (t % 2 == 0 ? 0 : amp);
                    _shapes.Line(prev, p, 1.5f * px, col);
                    prev = p;
                }
            }
        }

        /// <summary>斜線の帯：<paramref name="along"/> の向きに長さ 2×半幅、そのうち <paramref name="frac"/> まで斜線を引く。</summary>
        void DrawHatch(Vec2 center, Vec2 along, double halfLen, double frac, float thick, float px, Color32 col)
        {
            var a0 = center - along * halfLen;
            var a1 = center - along * halfLen + along * (2 * halfLen * Math.Max(frac, 0.02));
            var perp = new Vec2(-along.Y, along.X) * (thick / 2);
            var q = new[] { V(a0 + perp), V(a1 + perp), V(a1 - perp), V(a0 - perp) };
            _shapes.Quad(q[0], q[1], q[2], q[3], Palette.WithAlpha(col, 90));
            double step = 7 * px;
            for (double s = 0; s < 2 * halfLen * frac; s += step)
            {
                var p = a0 + along * s;
                _shapes.Line(V(p - perp), V(p + along * (thick * 0.8) + perp), 1.2f * px, col);
            }
            // 全体の長さの目安（細い枠）
            var e1 = center + along * halfLen;
            _shapes.Line(V(a0 + perp), V(e1 + perp), 0.8f * px, Palette.WithAlpha(col, 120));
            _shapes.Line(V(a0 - perp), V(e1 - perp), 0.8f * px, Palette.WithAlpha(col, 120));
        }

        /// <summary>
        /// 駒のすぐ下に兵力と士気の細いバー。士気のバーは、減った分を薄い色でしばらく残してから縮める（直前に大きく削られたことが分かるように）。
        /// </summary>
        void DrawBars(Company c, Vec2 pos, float ey, float px)
        {
            float bw = 56 * px, bh = 4 * px;
            var left = V(pos) + new Vector2(-bw / 2, -ey - 10 * px);
            Bar(left, bw, bh, c.Strength / (float)c.InitialStrength, 1, new Color32(220, 220, 220, 255), default);
            var fx = _fx[c.Id];
            float m = (float)(c.Morale / 100.0), ghost = Mathf.Max(m, fx.GhostMorale / 100f);
            Bar(left + new Vector2(0, -bh - 2 * px), bw, bh, m, ghost, MoraleColor32(c), new Color32(255, 120, 120, 200));
        }

        void Bar(Vector2 left, float w, float h, float v, float ghost, Color32 col, Color32 ghostCol)
        {
            var bg = new Color32(0, 0, 0, 170);
            _shapes.Quad(left, left + new Vector2(w, 0), left + new Vector2(w, h), left + new Vector2(0, h), bg);
            if (ghost > v)
            {
                float g0 = w * Mathf.Clamp01(v), g1 = w * Mathf.Clamp01(ghost);
                _shapes.Quad(left + new Vector2(g0, 0), left + new Vector2(g1, 0), left + new Vector2(g1, h), left + new Vector2(g0, h), ghostCol);
            }
            float x = w * Mathf.Clamp01(v);
            if (x > 0) _shapes.Quad(left, left + new Vector2(x, 0), left + new Vector2(x, h), left + new Vector2(0, h), col);
        }

        Color32 MoraleColor32(Company c)
        {
            double rout = _params.Of(c.Training).RoutThreshold;
            if (c.Morale <= rout) return new Color32(220, 50, 50, 255);
            if (c.Morale <= _params.Morale.ShakenThreshold) return new Color32(240, 190, 50, 255);
            return new Color32(90, 205, 100, 255);
        }

        // ---------------------------------------------------------------- 浮かぶ文字

        /// <summary>損害の数字（小さめ、上へ流れて消える）と大きな出来事（大きめ）。駒の上に、補間した位置で置く。</summary>
        void DrawFloatTexts(float px, float now)
        {
            // 同じ駒に重なる文字は、少しずつ上へずらす
            var stack = new Dictionary<int, int>();
            foreach (var f in _floats)
            {
                Vector2 at;
                float rise;
                float age = (now - f.Start) / f.Duration;
                if (f.UnitId >= 0)
                {
                    var c = _sim.Companies[f.UnitId];
                    Pose(c, out var pos, out double facing);
                    Extent(facing, out _, out float ey);
                    stack.TryGetValue(f.UnitId, out int k);
                    stack[f.UnitId] = k + 1;
                    at = V(pos) + new Vector2(0, ey + (12 + 26 * k) * px);
                }
                else
                {
                    at = V(f.World) + new Vector2(0, 44 * px);
                }
                rise = (f.Big ? 10 : 22) * age * px;
                var col = f.Color;
                col.a = age < 0.7f ? 1f : Mathf.Clamp01((1 - age) / 0.3f);
                // 影（読みやすさのため、黒を少しずらして先に置く）
                var shadow = new Color(0, 0, 0, col.a * 0.8f);
                float size = f.Big ? 20 : 15;
                _texts.Draw(f.Text, at + new Vector2(1.5f * px, rise - 1.5f * px), size, px, shadow);
                _texts.Draw(f.Text, at + new Vector2(0, rise), size, px, col);
            }
        }

        // ---------------------------------------------------------------- 経路・目的地・強調・デバッグ

        /// <summary>
        /// 選んだ中隊の経路（残りの点の列）と目的地。攻撃・拘束は目標への破線、陣地構築の予定は目的地に斜線の帯。
        /// 出したばかりで受け付け待ちの命令があれば（一時停止中を含む）、その下見を経路として描く（P1e の5）。
        /// </summary>
        void DrawSelectedPath(float px)
        {
            var c = Selected;
            if (c == null || !c.OnBoard) return;
            Pose(c, out var pos, out _);
            // 未着・開始待ち・起案中の命令書の下見は、段ごとに DrawSheetPreviews で出す（P2b）
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
                Vec2 faceDir = o.FacingRad.HasValue ? Vec2.FromAngle(o.FacingRad.Value) : new Vec2(1, 0);
                if (o.FacingRad.HasValue)
                    _shapes.Arrow(g, g + V(faceDir) * 30 * px, 2 * px, Palette.Path);
                if (o.Type == OrderType.Entrench)
                {
                    // 陣地構築の予定：着いた後の正面の辺に沿う斜線の帯（陣地構築の印と同じ形）
                    var lv = new Vec2(-faceDir.Y, faceDir.X);
                    DrawHatch(c.MoveGoal + faceDir * (_params.Movement.DepthM / 2 + 9 * px), lv, _params.Movement.FrontageM / 2, 1.0, 7 * px, px,
                        new Color32(255, 255, 255, 220));
                }
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

        /// <summary>拘束・攻撃の目標を選ぶとき、カーソルの下の敵の駒（攻撃の方角をドラッグ中は、押した敵の駒）を強調する。</summary>
        void DrawHover(float px)
        {
            if (_mode != Mode.Attack && _mode != Mode.Pin) return;
            var tgt = _mode == Mode.Attack && _pressing ? _pressTarget : (MouseOverUi() ? null : UnitAt(MouseWorld(), Side.Red));
            if (tgt == null) return;
            Pose(tgt, out var pos, out double facing);
            double hd = _params.Movement.DepthM / 2, hf = _params.Movement.FrontageM / 2;
            Corners(pos, facing, hd + 4 * px, hf + 4 * px, _q);
            Outline(_q, 3 * px, Palette.Hover);
        }

        /// <summary>ドラッグ中の向きの矢印（前進・後退・保持・陣地構築）。</summary>
        void DrawDragPreview(float px)
        {
            if (!_pressing || (_mode != Mode.Advance && _mode != Mode.Retreat && _mode != Mode.HoldFacing && _mode != Mode.Entrench)) return;
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
