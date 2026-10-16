using System;

namespace Battalion.Sim
{
    public enum SightResult { Clear, Range, Ridge, Cover }

    /// <summary>
    /// 視線（仕様「P1 詳細仕様」の視線。docs/reference/map/map_reference.py の los と同じ手順）。
    /// 値は sim_params.json の "sight"、視線を遮る重みは "terrain" の los_weight。
    /// </summary>
    public static class LineOfSight
    {
        public static bool Visible(TerrainMap m, SimParams.SightParams sp, Vec2 from, Vec2 to) =>
            Check(m, sp, from, to) == SightResult.Clear;

        /// <summary>
        /// 観測者 <paramref name="from"/> から目標 <paramref name="to"/> が見えるか。
        /// 線分を 10m 刻みで調べ（両端を除く）、補間した地面が視線より高ければ稜線で遮られる。
        /// 森林・村落のマス（観測者と目標のマスを除く）を通る距離を重み付きで足し、100m を超えたら遮られる。
        /// 視界距離は 800m×(1＋0.1×高低差÷10m)（高い側から見るときだけ伸びる）。
        /// </summary>
        public static SightResult Check(TerrainMap m, SimParams.SightParams sp, Vec2 from, Vec2 to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double eo = m.ElevationInterp(from), et = m.ElevationInterp(to);
            double vis = sp.BaseRangeM * (1 + sp.RangeGainPer10mHeight * Math.Max(0.0, eo - et) / 10.0);
            if (dist > vis) return SightResult.Range;

            int oc = m.IndexOf(from), tc = m.IndexOf(to);
            int n = (int)Math.Floor(dist / sp.SampleStepM);
            double obsc = 0.0;
            for (int k = 1; k < n; k++)
            {
                double s = k * sp.SampleStepM / dist;
                var p = new Vec2(from.X + dx * s, from.Y + dy * s);
                double z = (eo + sp.EyeHeightM) * (1 - s) + (et + sp.EyeHeightM) * s;
                if (m.ElevationInterp(p) > z) return SightResult.Ridge;
                m.CellOf(p, out int i, out int j);
                int c = j * m.Width + i;
                if (c != oc && c != tc)
                {
                    obsc += m.LosWeight(m.TerrainAt(i, j)) * sp.SampleStepM;
                    if (obsc > sp.CoverBlockM) return SightResult.Cover;
                }
            }
            return SightResult.Clear;
        }

        public static string Name(SightResult r)
        {
            switch (r)
            {
                case SightResult.Clear: return "clear";
                case SightResult.Range: return "range";
                case SightResult.Ridge: return "ridge";
                default: return "cover";
            }
        }
    }
}
