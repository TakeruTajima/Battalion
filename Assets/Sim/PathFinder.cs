using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>
    /// 経路探索（仕様「P1 詳細仕様」の経路探索。docs/reference/map/map_reference.py の astar と同じ手順）。
    /// 8近傍の A*。1歩のコスト＝距離÷移動倍率＋脅威の重み×露出×露出のコスト（50m）。斜め移動で通れないマスの角を切らない。
    /// 同点の扱いもリファレンスに合わせる：キューは (f, g, i, j) の辞書順で取り出し、近傍は di, dj を −1, 0, 1 の順に調べる。
    /// </summary>
    public static class PathFinder
    {
        public readonly struct Cell : IEquatable<Cell>
        {
            public readonly int I, J;

            public Cell(int i, int j)
            {
                I = i;
                J = j;
            }

            public bool Equals(Cell o) => I == o.I && J == o.J;
            public override bool Equals(object obj) => obj is Cell o && Equals(o);
            public override int GetHashCode() => I * 397 ^ J;
            public override string ToString() => $"({I}, {J})";
        }

        struct Node
        {
            public double F, G;
            public int I, J;

            public bool Less(in Node o)
            {
                if (F != o.F) return F < o.F;
                if (G != o.G) return G < o.G;
                if (I != o.I) return I < o.I;
                return J < o.J;
            }
        }

        /// <summary>
        /// <paramref name="start"/> のマスから <paramref name="goal"/> のマスへの経路（両端のマスを含む）。
        /// 届かなければ null。<paramref name="exposure"/> は添字 <c>j * Width + i</c> の露出（null なら全て 0）。
        /// <paramref name="blocked"/> が true のマスには入らない（目的地のマスは除く。敵の周りを避けるのに使う）。
        /// <paramref name="distanceMult"/> はマスごとの距離のコストの倍率（1 以上。null なら全て 1。止まっている味方の圏に使う）。
        /// </summary>
        public static List<Cell> Find(TerrainMap m, Cell start, Cell goal, bool[] exposure, double threatWeight, double exposureCostM,
            out double cost, bool[] blocked = null, bool allowPartial = false, double[] distanceMult = null)
        {
            int w = m.Width;
            var g = new Dictionary<int, double>();
            var came = new Dictionary<int, int>();
            var heap = new List<Node>();
            int sk = start.J * w + start.I, gk = goal.J * w + goal.I;
            g[sk] = 0.0;
            Push(heap, new Node { F = 0.0, G = 0.0, I = start.I, J = start.J });
            bool found = false;
            while (heap.Count > 0)
            {
                var cur = Pop(heap);
                int ck = cur.J * w + cur.I;
                if (ck == gk) { found = true; break; }
                if (cur.G > (g.TryGetValue(ck, out var gc) ? gc : 1e18)) continue;
                for (int di = -1; di <= 1; di++)
                {
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int ni = cur.I + di, nj = cur.J + dj;
                        if (!m.InBounds(ni, nj)) continue;
                        double mv = m.MoveMult(m.TerrainAt(ni, nj));
                        if (mv <= 0) continue;
                        if (blocked != null && blocked[nj * w + ni] && (ni != goal.I || nj != goal.J)) continue;
                        if (di != 0 && dj != 0)
                        {
                            if (m.MoveMult(m.TerrainAt(ni, cur.J)) <= 0 || m.MoveMult(m.TerrainAt(cur.I, nj)) <= 0)
                                continue;
                        }
                        double step = m.CellM * (di != 0 && dj != 0 ? Math.Sqrt(2) : 1.0);
                        int nk = nj * w + ni;
                        double exp = exposure != null && exposure[nk] ? 1.0 : 0.0;
                        double dm = distanceMult != null ? distanceMult[nk] : 1.0;
                        double ng = cur.G + (step / mv * dm + threatWeight * exp * exposureCostM);
                        if (ng < (g.TryGetValue(nk, out var old) ? old : 1e18))
                        {
                            g[nk] = ng;
                            came[nk] = ck;
                            double h = Hypot(goal.I - ni, goal.J - nj) * m.CellM; // 移動倍率1.0が上限なので許容的
                            Push(heap, new Node { F = ng + h, G = ng, I = ni, J = nj });
                        }
                    }
                }
            }
            if (!found && sk != gk)
            {
                if (!allowPartial)
                {
                    cost = double.PositiveInfinity;
                    return null;
                }
                // 届かない：探索で訪れたマスのうち、目的地に最も近いマス（直線距離。同じなら経路コストの小さい方）までの経路
                int bestK = sk;
                double bestH = double.MaxValue, bestG = double.MaxValue;
                foreach (var kv in g)
                {
                    int ci = kv.Key % w, cj = kv.Key / w;
                    double h = Hypot(goal.I - ci, goal.J - cj);
                    if (h < bestH - 1e-12 || (Math.Abs(h - bestH) <= 1e-12 && kv.Value < bestG))
                    {
                        bestH = h;
                        bestG = kv.Value;
                        bestK = kv.Key;
                    }
                }
                gk = bestK;
                goal = new Cell(bestK % w, bestK / w);
            }
            var path = new List<Cell> { goal };
            int k = gk;
            while (k != sk)
            {
                k = came[k];
                path.Add(new Cell(k % w, k / w));
            }
            path.Reverse();
            cost = g[gk];
            return path;
        }

        /// <summary>Python の math.hypot に合わせて、整数の差から距離を出す。</summary>
        static double Hypot(int a, int b) => Math.Sqrt((double)a * a + (double)b * b);

        /// <summary>経路の長さと、露出したマスへ入った区間の長さ（map_reference.py の path_stats と同じ）。</summary>
        public static void Stats(TerrainMap m, List<Cell> path, bool[] exposure, out double lengthM, out double exposedM)
        {
            lengthM = 0;
            exposedM = 0;
            for (int k = 1; k < path.Count; k++)
            {
                var a = path[k - 1];
                var b = path[k];
                double step = m.CellM * (a.I != b.I && a.J != b.J ? Math.Sqrt(2) : 1.0);
                lengthM += step;
                if (exposure != null && exposure[b.J * m.Width + b.I]) exposedM += step;
            }
        }

        /// <summary>
        /// マスの経路を、移動に使う点の列にする。出発のマスは除き、一直線に並ぶ点は間引いて曲がり角のマスの中心だけを残す。
        /// 最後の点は目的地そのもの。
        /// </summary>
        public static List<Vec2> ToWaypoints(TerrainMap m, List<Cell> path, Vec2 destination)
        {
            var pts = new List<Vec2>();
            for (int k = 1; k < path.Count - 1; k++)
            {
                var a = path[k - 1];
                var b = path[k];
                var c = path[k + 1];
                if (b.I - a.I != c.I - b.I || b.J - a.J != c.J - b.J)
                    pts.Add(m.CellCenter(b.I, b.J));
            }
            pts.Add(destination);
            return pts;
        }

        // 2分ヒープ（.NET Standard 2.1 には PriorityQueue がない）
        static void Push(List<Node> h, Node n)
        {
            h.Add(n);
            int c = h.Count - 1;
            while (c > 0)
            {
                int p = (c - 1) / 2;
                if (!h[c].Less(h[p])) break;
                (h[c], h[p]) = (h[p], h[c]);
                c = p;
            }
        }

        static Node Pop(List<Node> h)
        {
            var top = h[0];
            int last = h.Count - 1;
            h[0] = h[last];
            h.RemoveAt(last);
            int c = 0;
            while (true)
            {
                int l = 2 * c + 1, r = l + 1, s = c;
                if (l < h.Count && h[l].Less(h[s])) s = l;
                if (r < h.Count && h[r].Less(h[s])) s = r;
                if (s == c) break;
                (h[c], h[s]) = (h[s], h[c]);
                c = s;
            }
            return top;
        }
    }
}
