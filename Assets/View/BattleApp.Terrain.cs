using System;
using System.Collections.Generic;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 地形の高さを読む（P1f）：等高線の数字、撃たない理由（視線が切れている敵への点線と理由）、断面図。
    // 視線の判定は Sim の LineOfSight.TraceLine を使い、表示側で計算し直さない
    public sealed partial class BattleApp
    {
        /// <summary>撃たない理由を、選んでいる中隊だけでなく全ての中隊について出すか（F8）。</summary>
        bool _showBlockedAll;
        /// <summary>断面図の2点目（視線確認の状態で Shift＋クリック、または2点の間のドラッグ）。</summary>
        Vec2? _sightTo;
        string _sightToLabel;

        // 撃たない理由の線（刻みごとに作り直す）
        readonly List<BlockedSight> _blocked = new List<BlockedSight>();
        long _blockedTick = -1;
        bool _blockedAll;
        int _blockedSelected = -2;

        LineOfSight.Trace _profile;
        Vec2 _profileFrom, _profileTo;
        long _profileTick = -1;

        readonly struct BlockedSight
        {
            public readonly int From, To;
            public readonly string Reason;
            public readonly Vec2 BlockAt;

            public BlockedSight(int from, int to, string reason, Vec2 blockAt)
            {
                From = from;
                To = to;
                Reason = reason;
                BlockAt = blockAt;
            }
        }

        static string BlockReason(LineOfSight.Trace t)
        {
            if (t.Result == SightResult.Ridge) return "稜線";
            if (t.Result == SightResult.Cover) return t.CoverBy == TerrainMap.Village ? "村" : "森";
            return null;
        }

        static string SightResultText(LineOfSight.Trace t)
        {
            switch (t.Result)
            {
                case SightResult.Clear: return "見える";
                case SightResult.Range: return "視界距離の外";
                default: return BlockReason(t) + "で遮られる";
            }
        }

        // ---------------------------------------------------------------- 撃たない理由（エ）

        /// <summary>
        /// 射程（400m）以内に指揮下の敵がいるのに、視線が通らない組を集める。対象は選んでいる中隊（F8 で盤上の指揮下の全中隊）。
        /// </summary>
        void RebuildBlockedSights()
        {
            if (_blockedTick == _sim.Tick && _blockedAll == _showBlockedAll && _blockedSelected == _selected) return;
            _blockedTick = _sim.Tick;
            _blockedAll = _showBlockedAll;
            _blockedSelected = _selected;
            _blocked.Clear();
            foreach (var c in _sim.Companies)
            {
                if (!c.OnBoard || !c.UnderCommand) continue;
                if (!_showBlockedAll && c.Id != _selected) continue;
                foreach (var e in _sim.Companies)
                {
                    if (!e.OnBoard || !e.UnderCommand || e.Side == c.Side) continue;
                    if (Vec2.Distance(c.Position, e.Position) > _params.Fire.RangeMaxM) continue;
                    var t = LineOfSight.TraceLine(_sim.Map, _params.Sight, c.Position, e.Position);
                    string why = BlockReason(t);
                    if (why != null) _blocked.Add(new BlockedSight(c.Id, e.Id, why, t.BlockAt.Value));
                }
            }
        }

        /// <summary>撃たない理由：視線の切れている敵への点線、遮った位置の小さな×、線の中ほどに理由の文字。</summary>
        void DrawBlockedSights(float px)
        {
            RebuildBlockedSights();
            var col = new Color32(45, 40, 35, 210);
            var mark = new Color32(220, 40, 30, 230);
            foreach (var b in _blocked)
            {
                Pose(_sim.Companies[b.From], out var p, out _);
                Pose(_sim.Companies[b.To], out var q, out _);
                _shapes.DashedLine(V(p), V(q), 1.5f * px, 4 * px, col);
                var x = V(b.BlockAt);
                float s = 5 * px;
                _shapes.Line(x + new Vector2(-s, -s), x + new Vector2(s, s), 2 * px, mark);
                _shapes.Line(x + new Vector2(-s, s), x + new Vector2(s, -s), 2 * px, mark);
                var mid = V((p + q) * 0.5);
                _texts.Draw(b.Reason, mid + new Vector2(1.2f * px, 3 * px - 1.2f * px), 13, px, new Color(0, 0, 0, 0.85f));
                _texts.Draw(b.Reason, mid + new Vector2(0, 3 * px), 13, px, new Color(1f, 0.92f, 0.85f));
            }
        }

        // ---------------------------------------------------------------- 等高線の数字（イ）

        /// <summary>太い等高線の数字。画面の上で 140px より近いものは間引く（引いて見ても重ならない）。</summary>
        void DrawContourLabels(float px)
        {
            var drawn = new List<Vector2>();
            float minGap = 140 * px;
            var ink = new Color(0.22f, 0.15f, 0.05f, 0.95f);
            var halo = new Color(1f, 0.97f, 0.88f, 0.8f);
            foreach (var (pos, e) in _mapView.ContourLabels)
            {
                var w = V(pos);
                bool near = false;
                foreach (var d in drawn)
                    if ((d - w).sqrMagnitude < minGap * minGap)
                    {
                        near = true;
                        break;
                    }
                if (near) continue;
                drawn.Add(w);
                string label = e + "m";
                _texts.Draw(label, w + new Vector2(1 * px, -1 * px), 14, px, halo, TextAnchor.MiddleCenter);
                _texts.Draw(label, w, 14, px, ink, TextAnchor.MiddleCenter);
            }
        }

        // ---------------------------------------------------------------- カーソルの位置の標高（ウ）

        /// <summary>カーソルの地点の標高（地図の外・パネルの上なら null）。Sim と同じ補間。</summary>
        double? CursorElevation()
        {
            if (MouseOverUi()) return null;
            var m = MouseWorld();
            return _sim.Map.Contains(m) ? _sim.Map.ElevationInterp(m) : (double?)null;
        }

        // ---------------------------------------------------------------- 断面図（オ）

        /// <summary>視線確認の点：中隊の駒の上ならその中心、そうでなければその地点。</summary>
        Vec2 SightPoint(Vec2 at, out string label)
        {
            var unit = UnitAt(at, null);
            label = unit != null ? unit.Name : $"({at.X:0}, {at.Y:0})";
            return unit != null ? unit.Position : at;
        }

        void UpdateProfile()
        {
            if (!_sightFrom.HasValue || !_sightTo.HasValue)
            {
                _profile = null;
                return;
            }
            if (_profile != null && _profileTick == _sim.Tick && _profileFrom.Equals(_sightFrom.Value) && _profileTo.Equals(_sightTo.Value)) return;
            _profileFrom = _sightFrom.Value;
            _profileTo = _sightTo.Value;
            _profileTick = _sim.Tick;
            _profile = LineOfSight.TraceLine(_sim.Map, _params.Sight, _profileFrom, _profileTo);
        }

        /// <summary>画面の画素（左下が原点）を世界の点へ。</summary>
        Vector2 ScreenToWorld(float x, float y)
        {
            var w = _cam.ScreenToWorldPoint(new Vector3(x, y, 10));
            return new Vector2(w.x, w.y);
        }

        /// <summary>
        /// 断面図：地図の上の2点を結ぶ破線と遮った位置、画面の左下の小窓に、横は距離・縦は標高（誇張）の断面。
        /// 地面、目の高さ（+2m）どうしを結ぶ視線（地面が上に出る区間は赤）、森・村の帯（遮る重みで濃さを変える）、遮った位置。
        /// </summary>
        void DrawProfile(float px)
        {
            UpdateProfile();
            if (_profile == null) return;
            var tr = _profile;
            _shapes.DashedLine(V(_profileFrom), V(_profileTo), 2 * px, 8 * px, Palette.Selected);
            _shapes.Circle(V(_profileTo), 6 * px, 2 * px, Palette.Selected, 16);
            if (tr.BlockAt.HasValue)
            {
                var x = V(tr.BlockAt.Value);
                float s = 7 * px;
                _shapes.Line(x + new Vector2(-s, -s), x + new Vector2(s, s), 3 * px, new Color32(220, 40, 30, 255));
                _shapes.Line(x + new Vector2(-s, s), x + new Vector2(s, -s), 3 * px, new Color32(220, 40, 30, 255));
            }

            // 小窓（画面の画素。地図の領域の左下）
            bool ui = _cam.targetTexture == null;
            float W = 480, H = 220;
            float x0 = (ui ? LeftW : 0) + 12, y0 = (ui ? BottomH : 0) + 12;
            float pl = x0 + 44, pr = x0 + W - 14, pb = y0 + 34, pt = y0 + H - 30;
            Vector2 S(float sx, float sy) => ScreenToWorld(sx, sy);
            _shapes.Quad(S(x0, y0), S(x0 + W, y0), S(x0 + W, y0 + H), S(x0, y0 + H), new Color32(20, 22, 26, 225));

            string title = $"断面　{_sightFromLabel} から {_sightToLabel} まで　{tr.DistanceM:0}m　{SightResultText(tr)}";
            if (tr.Samples.Count < 2)
            {
                _texts.Draw(title, S(x0 + 10, y0 + H - 8), 13, px, Color.white, TextAnchor.UpperLeft);
                return;
            }

            double zmin = double.MaxValue, zmax = double.MinValue;
            foreach (var sm in tr.Samples)
            {
                zmin = Math.Min(zmin, Math.Min(sm.Ground, sm.Sight));
                zmax = Math.Max(zmax, Math.Max(sm.Ground, sm.Sight));
            }
            zmin = Math.Floor(zmin - 1);
            zmax = Math.Ceiling(zmax + 1);
            if (zmax - zmin < 10) zmax = zmin + 10;
            double hScale = (pr - pl) / Math.Max(1.0, tr.DistanceM); // 画素/m
            double vScale = (pt - pb) / (zmax - zmin);
            double ex = vScale / hScale;
            float X(double d) => (float)(pl + d * hScale);
            float Y(double z) => (float)(pb + (z - zmin) * vScale);

            // 森・村の帯（下端）：遮る重みが大きいほど濃い
            for (int k = 0; k + 1 < tr.Samples.Count; k++)
            {
                var a = tr.Samples[k];
                char t = a.Terrain;
                if (t != TerrainMap.Forest && t != TerrainMap.Village) continue;
                var b = tr.Samples[k + 1];
                double wgt = _sim.Map.LosWeight(t);
                byte alpha = (byte)Math.Min(255, 90 + 165 * wgt);
                var bc = Palette.WithAlpha(Palette.Terrain(t), alpha);
                _shapes.Quad(S(X(a.DistanceM), y0 + 18), S(X(b.DistanceM), y0 + 18), S(X(b.DistanceM), y0 + 28), S(X(a.DistanceM), y0 + 28), bc);
            }
            // 地面（下端から地面まで塗る）
            var ground = new Color32(150, 128, 90, 255);
            for (int k = 0; k + 1 < tr.Samples.Count; k++)
            {
                var a = tr.Samples[k];
                var b = tr.Samples[k + 1];
                _shapes.Quad(S(X(a.DistanceM), pb), S(X(b.DistanceM), pb), S(X(b.DistanceM), Y(b.Ground)), S(X(a.DistanceM), Y(a.Ground)), ground);
            }
            // 視線：地面が視線より上に出ている区間は赤
            var clear = new Color32(255, 235, 120, 255);
            var hidden = new Color32(235, 50, 40, 255);
            for (int k = 0; k + 1 < tr.Samples.Count; k++)
            {
                var a = tr.Samples[k];
                var b = tr.Samples[k + 1];
                bool blocked = a.AboveSight || b.AboveSight;
                _shapes.Line(S(X(a.DistanceM), Y(a.Sight)), S(X(b.DistanceM), Y(b.Sight)), (blocked ? 3f : 2f) * px, blocked ? hidden : clear);
            }
            // 目の位置（両端）
            _shapes.Circle(S(X(0), Y(tr.Samples[0].Sight)), 4 * px, 2 * px, Color.white, 12);
            var last = tr.Samples[tr.Samples.Count - 1];
            _shapes.Circle(S(X(last.DistanceM), Y(last.Sight)), 4 * px, 2 * px, Color.white, 12);
            // 遮った位置
            if (tr.BlockAt.HasValue)
            {
                float bx = X(tr.BlockDistanceM);
                _shapes.Line(S(bx, pb), S(bx, pt), 2 * px, hidden);
                _texts.Draw(BlockReason(tr), S(bx + 4, pt - 2), 12, px, new Color(1f, 0.45f, 0.4f), TextAnchor.UpperLeft);
            }
            // 軸：下の線と、100m ごとの目盛り
            var axis = new Color32(200, 200, 200, 200);
            _shapes.Line(S(pl, pb), S(pr, pb), 1 * px, axis);
            _shapes.Line(S(pl, pb), S(pl, pt), 1 * px, axis);
            for (double d = 0; d <= tr.DistanceM + 1e-6; d += 100)
                _shapes.Line(S(X(d), pb), S(X(d), pb - 4), 1 * px, axis);

            _texts.Draw(title, S(x0 + 10, y0 + H - 8), 13, px, Color.white, TextAnchor.UpperLeft);
            _texts.Draw($"{zmax:0}m", S(pl - 4, pt), 11, px, Color.white, TextAnchor.MiddleRight);
            _texts.Draw($"{zmin:0}m", S(pl - 4, pb), 11, px, Color.white, TextAnchor.MiddleRight);
            _texts.Draw("0", S(pl, pb - 6), 11, px, Color.white, TextAnchor.UpperCenter);
            _texts.Draw($"{tr.DistanceM:0}m", S(pr, pb - 6), 11, px, Color.white, TextAnchor.UpperCenter);
            _texts.Draw($"縦 ×{ex:0.#}　目の高さ +{_params.Sight.EyeHeightM:0}m　帯：森・村", S(pr, y0 + 4), 11, px,
                new Color(0.85f, 0.85f, 0.85f), TextAnchor.LowerRight);
        }
    }
}
