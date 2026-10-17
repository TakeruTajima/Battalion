using System;
using System.Collections.Generic;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    /// <summary>
    /// 地図の描画。地形の色分け・高さの明暗・陰影・等高線をテクスチャ1枚（1マス 10px）に描き、その上に半透明の重ねる表示
    /// （視線確認・露出の地図。1マス 1px）を置く。世界座標は Sim と同じメートル。
    /// 25m おきの太い等高線の数字の位置も、ここで選んでおく（描くのは BattleApp の文字。P1f のイ）。
    /// </summary>
    public sealed class MapRenderer
    {
        public const int PixelsPerCell = 10;
        public const double ContourStepM = 5.0;
        /// <summary>太い等高線（と数字）の間隔。</summary>
        public const double MajorContourM = 25.0;

        // 陰影（P1f のア）：北西（方位 315°）・高度 45° からの光。起伏が小さい（標高 2〜29m）ので縦を誇張して勾配を出し、
        // 平らな所の明るさを 1 として、明暗の幅を抑える（色分けを消さない）
        const double LightAzimuthDeg = 315, LightAltitudeDeg = 45;
        const double ShadeZFactor = 4.0;
        const double ShadeStrength = 0.9, ShadeMin = 0.74, ShadeMax = 1.08;
        // 勾配は 1マス（10px）の幅の差分で取る（1px の差分だと、マスの境目で補間の傾きが変わる所に縦横の縞が出る）
        const int ShadeStencilPx = PixelsPerCell / 2;
        // 高さの明暗（P1f のイ）：平地を、地図の最低で ×0.84、最高で ×1.02（明るい側は陰影と重なって白く飛ぶので控えめ）
        const double TintLow = 0.84, TintHigh = 1.02;
        // 等高線の数字の間隔（同じ高さの数字どうし、どの数字どうし）
        const double LabelSpacingSameM = 700, LabelSpacingAnyM = 250;

        /// <summary>太い等高線の数字の位置と標高（m）。</summary>
        public readonly List<(Vec2 pos, int elevation)> ContourLabels = new List<(Vec2, int)>();

        readonly TerrainMap _map;
        readonly Texture2D _terrainTex;
        readonly Texture2D _overlayTex;
        readonly SpriteRenderer _overlay;
        readonly Color32[] _overlayPixels;

        public GameObject Root { get; }

        public MapRenderer(TerrainMap map, Transform parent)
        {
            _map = map;
            Root = new GameObject("Map");
            Root.transform.SetParent(parent, false);
            Root.transform.position = new Vector3((float)map.OriginX, (float)map.OriginY, 0);

            int w = map.Width * PixelsPerCell, h = map.Height * PixelsPerCell;
            _terrainTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            PaintTerrain();
            AddSprite("Terrain", _terrainTex, (float)(PixelsPerCell / map.CellM), 0);

            _overlayTex = new Texture2D(map.Width, map.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _overlayPixels = new Color32[map.Width * map.Height];
            _overlay = AddSprite("Overlay", _overlayTex, (float)(1.0 / map.CellM), 1);
            ClearOverlay();
        }

        SpriteRenderer AddSprite(string name, Texture2D tex, float pixelsPerUnit, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            // FullRect：透明な画素から形を作らない（重ねる表示は作った時点で全て透明なので、Tight だと何も描かれない）
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.zero, pixelsPerUnit, 0, SpriteMeshType.FullRect);
            sr.sortingOrder = order;
            return sr;
        }

        /// <summary>
        /// 地形を塗り、等高線を描き込む。各画素の中心の標高を Sim と同じ補間（<see cref="TerrainMap.ElevationInterp"/>）で出し、
        /// 平地は標高で明暗を変え（低いほど濃く）、全体に北西からの陰影を掛け、
        /// 右か上の画素と 5m の区切りをまたぐ画素を暗くする（25m ごとは濃く）。陣地が完成したときにも呼ぶ。
        /// </summary>
        public void PaintTerrain()
        {
            int w = _terrainTex.width, h = _terrainTex.height;
            double px = _map.CellM / PixelsPerCell;
            var elev = new double[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    elev[y * w + x] = _map.ElevationInterp(new Vec2(_map.OriginX + (x + 0.5) * px, _map.OriginY + (y + 0.5) * px));

            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double e in elev)
            {
                lo = Math.Min(lo, e);
                hi = Math.Max(hi, e);
            }
            double az = LightAzimuthDeg * Math.PI / 180, alt = LightAltitudeDeg * Math.PI / 180;
            double lx = Math.Cos(alt) * Math.Sin(az), ly = Math.Cos(alt) * Math.Cos(az), lz = Math.Sin(alt);

            var pixels = new Color32[w * h];
            var majors = new List<(Vec2, int)>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    char t = _map.TerrainAt(x / PixelsPerCell, y / PixelsPerCell);
                    var c = Palette.Terrain(t);
                    double e0 = elev[y * w + x];
                    if (t == TerrainMap.Plain && hi > lo)
                        c = Scale(c, TintLow + (TintHigh - TintLow) * (e0 - lo) / (hi - lo));
                    // 陰影：中心差分の勾配（地図の端では片側に寄せる）
                    int xa = Math.Max(0, x - ShadeStencilPx), xb = Math.Min(w - 1, x + ShadeStencilPx);
                    int ya = Math.Max(0, y - ShadeStencilPx), yb = Math.Min(h - 1, y + ShadeStencilPx);
                    double gx = (elev[y * w + xb] - elev[y * w + xa]) / ((xb - xa) * px) * ShadeZFactor;
                    double gy = (elev[yb * w + x] - elev[ya * w + x]) / ((yb - ya) * px) * ShadeZFactor;
                    double shade = (-gx * lx - gy * ly + lz) / Math.Sqrt(gx * gx + gy * gy + 1);
                    c = Scale(c, Math.Max(ShadeMin, Math.Min(ShadeMax, 1 + ShadeStrength * (shade - lz))));
                    int band = Band(e0);
                    int right = x + 1 < w ? Band(elev[y * w + x + 1]) : band;
                    int up = y + 1 < h ? Band(elev[(y + 1) * w + x]) : band;
                    if (band != right || band != up)
                    {
                        int top = Math.Max(band, Math.Max(right, up));
                        bool major = top % (int)(MajorContourM / ContourStepM) == 0;
                        c = Blend(c, major ? Palette.ContourMajor : Palette.Contour);
                        if (major && top > 0) majors.Add((new Vec2(_map.OriginX + (x + 0.5) * px, _map.OriginY + (y + 0.5) * px), (int)(top * ContourStepM)));
                    }
                    pixels[y * w + x] = c;
                }
            }
            _terrainTex.SetPixels32(pixels);
            _terrainTex.Apply();
            PickContourLabels(majors);
        }

        /// <summary>太い等高線の上の点から、数字を置く点を間引いて選ぶ（同じ高さは 700m、どれとも 250m 離す）。</summary>
        void PickContourLabels(List<(Vec2 pos, int elevation)> candidates)
        {
            ContourLabels.Clear();
            foreach (var (pos, e) in candidates)
            {
                bool ok = true;
                foreach (var (q, qe) in ContourLabels)
                {
                    double d = Vec2.Distance(pos, q);
                    if (d < LabelSpacingAnyM || (qe == e && d < LabelSpacingSameM))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) ContourLabels.Add((pos, e));
            }
        }

        static Color32 Scale(Color32 c, double k) =>
            new Color32((byte)Math.Min(255, c.r * k), (byte)Math.Min(255, c.g * k), (byte)Math.Min(255, c.b * k), c.a);

        /// <summary>
        /// 標高の区切り（5m ごと）。標高がちょうど 5m の倍数の平らな所で、補間の丸め誤差（4.9999… と 5.0）のために
        /// 線が点々と出ないよう、わずかに上へずらしてから切り捨てる。
        /// </summary>
        static int Band(double e) => (int)Math.Floor(e / ContourStepM + 1e-6);

        static Color32 Blend(Color32 under, Color32 over)
        {
            float a = over.a / 255f;
            return new Color32((byte)(under.r * (1 - a) + over.r * a), (byte)(under.g * (1 - a) + over.g * a),
                (byte)(under.b * (1 - a) + over.b * a), 255);
        }

        // ---------------------------------------------------------------- 重ねる表示

        public void ClearOverlay()
        {
            for (int k = 0; k < _overlayPixels.Length; k++) _overlayPixels[k] = new Color32(0, 0, 0, 0);
            _overlayTex.SetPixels32(_overlayPixels);
            _overlayTex.Apply();
            _overlay.enabled = false;
        }

        /// <summary>マスごとの色（添字 <c>j * Width + i</c>、j = 0 が南）を重ねる。</summary>
        public void ShowOverlay(Color32[] cells)
        {
            Array.Copy(cells, _overlayPixels, _overlayPixels.Length);
            _overlayTex.SetPixels32(_overlayPixels);
            _overlayTex.Apply();
            _overlay.enabled = true;
        }

        /// <summary>
        /// 視線確認：<paramref name="observer"/> から各マスの中心へ Sim の <see cref="LineOfSight.Check"/> で視線を引き、
        /// 見えるマスを緑、視界距離の内側で見えないマス（稜線・遮蔽）を赤で塗る。視界距離の外は塗らない。
        /// </summary>
        public static Color32[] SightCells(TerrainMap map, SimParams.SightParams sp, Vec2 observer, double maxRangeM)
        {
            var cells = new Color32[map.Width * map.Height];
            Visibility.ForCellsWithin(map, observer, maxRangeM, (i, j, center) =>
            {
                var r = LineOfSight.Check(map, sp, observer, center);
                if (r == SightResult.Clear) cells[j * map.Width + i] = Palette.SightVisible;
                else if (r != SightResult.Range) cells[j * map.Width + i] = Palette.SightBlocked;
            });
            return cells;
        }

        /// <summary>地図の最大の高低差から、視界距離の最大（800m×(1＋0.1×高低差÷10m)）を出す。</summary>
        public static double MaxSightRange(TerrainMap map, SimParams.SightParams sp)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int j = 0; j < map.Height; j++)
                for (int i = 0; i < map.Width; i++)
                {
                    lo = Math.Min(lo, map.ElevationAt(i, j));
                    hi = Math.Max(hi, map.ElevationAt(i, j));
                }
            return sp.BaseRangeM * (1 + sp.RangeGainPer10mHeight * (hi - lo) / 10.0);
        }

        public static Color32[] ExposureCells(TerrainMap map, bool[] exposure)
        {
            var cells = new Color32[map.Width * map.Height];
            for (int k = 0; k < cells.Length; k++)
                if (exposure[k]) cells[k] = Palette.Exposure;
            return cells;
        }
    }
}
