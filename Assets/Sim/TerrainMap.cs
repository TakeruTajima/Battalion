using System;

namespace Battalion.Sim
{
    /// <summary>
    /// 地図。50m 四方の格子に地形と標高を持つ。座標は x が東、y が北（メートル）。
    /// 内部の添字は <c>[j * Width + i]</c>、j = 0 が南の行（ファイルは北の行が先なので読み込み時に反転する）。
    /// 地形は「基本の地形」（会戦中は変えない）と「変わる層」（構築した塹壕、砲撃痕）の2枚で持つ。
    /// </summary>
    public sealed class TerrainMap
    {
        public const char Plain = '.';
        public const char Forest = 'F';
        public const char Village = 'V';
        public const char River = 'W';
        public const char Bridge = 'B';
        public const char Ford = 'S';
        public const char Trench = 'T';
        public const char Crater = 'C';
        const char NoOverlay = '\0';

        public int Width { get; }
        public int Height { get; }
        public double CellM { get; }
        /// <summary>南西の角の座標。シナリオAの地図は (0, 0)。</summary>
        public double OriginX { get; }
        public double OriginY { get; }
        /// <summary>地形の表（移動倍率・被弾倍率・視線を遮る重み）。</summary>
        public SimParams.TerrainParams Terrain { get; }

        readonly char[] _base;
        readonly char[] _overlay;
        readonly double[] _elev;
        /// <summary>陣地（塹壕のマス）の向き（ラジアン。NaN は向きがない）。R2。</summary>
        readonly double[] _fortFacing;

        TerrainMap(SimParams.TerrainParams terrain, int width, int height, double cellM, double originX, double originY,
            char[] baseTerrain, double[] elevation)
        {
            Terrain = terrain;
            Width = width;
            Height = height;
            CellM = cellM;
            OriginX = originX;
            OriginY = originY;
            _base = baseTerrain;
            _elev = elevation;
            _overlay = new char[width * height];
            _fortFacing = new double[width * height];
            for (int k = 0; k < _fortFacing.Length; k++) _fortFacing[k] = double.NaN;
        }

        public double MinX => OriginX;
        public double MinY => OriginY;
        public double MaxX => OriginX + Width * CellM;
        public double MaxY => OriginY + Height * CellM;

        /// <summary>全て平地・標高0の地図。</summary>
        public static TerrainMap Flat(SimParams.TerrainParams terrain, double originX, double originY, int width, int height,
            double cellM = 50.0)
        {
            var t = new char[width * height];
            for (int k = 0; k < t.Length; k++) t[k] = Plain;
            return new TerrainMap(terrain, width, height, cellM, originX, originY, t, new double[width * height]);
        }

        /// <summary>範囲 [minX, maxX]×[minY, maxY] を覆う平地の地図（マスの数は切り上げ）。</summary>
        public static TerrainMap FlatCovering(SimParams.TerrainParams terrain, double minX, double minY, double maxX, double maxY,
            double cellM = 50.0) =>
            Flat(terrain, minX, minY, (int)Math.Ceiling((maxX - minX) / cellM), (int)Math.Ceiling((maxY - minY) / cellM), cellM);

        /// <summary>maps/scenario_*.json の形式から読む。原点は南西の角 (0, 0)。地形の文字は表にあるものだけを受け付ける。</summary>
        public static TerrainMap FromJson(JsonValue d, SimParams.TerrainParams terrain)
        {
            int w = (int)d.D("width"), h = (int)d.D("height");
            double cell = d.D("cell_m");
            if (d.TryGet("rows_order", out var ro) && ro.String != "north_first")
                throw new FormatException("rows_order は north_first だけを扱う");
            var rows = d["terrain"].Array;
            var erows = d["elevation"].Array;
            if (rows.Count != h || erows.Count != h) throw new FormatException("行の数が height と合わない");
            var t = new char[w * h];
            var e = new double[w * h];
            for (int r = 0; r < h; r++)
            {
                int j = h - 1 - r; // ファイルは北の行が先
                string row = rows[r].String;
                var erow = erows[r].Array;
                if (row.Length != w || erow.Count != w) throw new FormatException($"{r} 行目の長さが width と合わない");
                for (int i = 0; i < w; i++)
                {
                    char c = row[i];
                    if (!terrain.Knows(c)) throw new FormatException($"不明な地形の文字 '{c}'");
                    t[j * w + i] = c;
                    e[j * w + i] = erow[i].Number;
                }
            }
            return new TerrainMap(terrain, w, h, cell, 0, 0, t, e);
        }

        // ---------------------------------------------------------------- 地形の表（sim_params.json の "terrain"）

        /// <summary>移動倍率。0 は通れない（川）。</summary>
        public double MoveMult(char c) => Terrain.MoveMult(c);
        /// <summary>被弾倍率。塹壕の値は止まっている中隊にだけ効く（呼び出し側で判定する）。</summary>
        public double DamageMult(char c) => Terrain.DamageMult(c);
        /// <summary>視線を遮る重み（そのマスを通る距離に掛ける）。</summary>
        public double LosWeight(char c) => Terrain.LosWeightOf(c);

        // ---------------------------------------------------------------- 参照

        public bool InBounds(int i, int j) => i >= 0 && i < Width && j >= 0 && j < Height;

        /// <summary>地図の範囲内か（端を含む）。壊走した中隊がこの外に出ると会戦から離脱する。</summary>
        public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

        /// <summary>点のあるマス。地図の外は端のマスに丸める（map_reference.py の cell_of と同じ）。</summary>
        public void CellOf(Vec2 p, out int i, out int j)
        {
            i = Math.Min(Width - 1, Math.Max(0, (int)Math.Floor((p.X - OriginX) / CellM)));
            j = Math.Min(Height - 1, Math.Max(0, (int)Math.Floor((p.Y - OriginY) / CellM)));
        }

        public int IndexOf(Vec2 p)
        {
            CellOf(p, out int i, out int j);
            return j * Width + i;
        }

        public Vec2 CellCenter(int i, int j) => new Vec2(OriginX + (i + 0.5) * CellM, OriginY + (j + 0.5) * CellM);

        /// <summary>今の地形（変わる層に値があればそれ、なければ基本の地形）。</summary>
        public char TerrainAt(int i, int j)
        {
            int k = j * Width + i;
            return _overlay[k] != NoOverlay ? _overlay[k] : _base[k];
        }

        public char TerrainAt(Vec2 p)
        {
            CellOf(p, out int i, out int j);
            return TerrainAt(i, j);
        }

        public char BaseTerrainAt(int i, int j) => _base[j * Width + i];

        public bool Passable(int i, int j) => MoveMult(TerrainAt(i, j)) > 0;

        public bool Passable(Vec2 p)
        {
            CellOf(p, out int i, out int j);
            return Passable(i, j);
        }

        /// <summary>
        /// 変わる層に地形を置く（構築した塹壕、砲撃痕）。川・橋・浅瀬のマスには置かない。
        /// 置いたら true。
        /// </summary>
        public bool SetOverlay(int i, int j, char c)
        {
            char b = _base[j * Width + i];
            if (b == River || b == Bridge || b == Ford) return false;
            _overlay[j * Width + i] = c;
            return true;
        }

        // ---------------------------------------------------------------- 陣地の向き（R2）

        /// <summary>
        /// 塹壕のマスの向き（ラジアン。null なら向きがない）。地図の塹壕はシナリオの定義で、構築した塹壕は完成したときの中隊の
        /// 向きで決まり、後から変わらない。
        /// </summary>
        public double? FortificationFacing(int i, int j)
        {
            double f = _fortFacing[j * Width + i];
            return double.IsNaN(f) ? (double?)null : f;
        }

        public double? FortificationFacingAt(Vec2 p)
        {
            CellOf(p, out int i, out int j);
            return InBounds(i, j) ? FortificationFacing(i, j) : null;
        }

        /// <summary>塹壕のマスの向きを決める（まだ向きがないマスだけ。決まった向きは変えない）。決めたら true。</summary>
        public bool SetFortificationFacing(int i, int j, double facingRad)
        {
            int k = j * Width + i;
            if (!double.IsNaN(_fortFacing[k])) return false;
            _fortFacing[k] = Angles.Wrap(facingRad);
            return true;
        }

        public double ElevationAt(int i, int j) => _elev[j * Width + i];

        /// <summary>マス中心の標高を双線形補間した値（視線用。map_reference.py の elev_interp と同じ）。</summary>
        public double ElevationInterp(Vec2 p)
        {
            double fx = (p.X - OriginX) / CellM - 0.5;
            double fy = (p.Y - OriginY) / CellM - 0.5;
            int i0 = Math.Min(Width - 1, Math.Max(0, (int)Math.Floor(fx)));
            int j0 = Math.Min(Height - 1, Math.Max(0, (int)Math.Floor(fy)));
            int i1 = Math.Min(Width - 1, i0 + 1), j1 = Math.Min(Height - 1, j0 + 1);
            double tx = Math.Min(1.0, Math.Max(0.0, fx - i0));
            double ty = Math.Min(1.0, Math.Max(0.0, fy - j0));
            return _elev[j0 * Width + i0] * (1 - tx) * (1 - ty) + _elev[j0 * Width + i1] * tx * (1 - ty)
                   + _elev[j1 * Width + i0] * (1 - tx) * ty + _elev[j1 * Width + i1] * tx * ty;
        }
    }
}
