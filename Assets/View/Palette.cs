using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    /// <summary>色。地形は docs/reference/map/scenario_a*_preview.png に合わせた。</summary>
    public static class Palette
    {
        public static Color32 Terrain(char c)
        {
            switch (c)
            {
                case TerrainMap.Forest: return new Color32(106, 148, 101, 255);
                case TerrainMap.Village: return new Color32(160, 145, 125, 255);
                case TerrainMap.River: return new Color32(112, 160, 200, 255);
                case TerrainMap.Bridge: return new Color32(90, 82, 74, 255);
                case TerrainMap.Ford: return new Color32(165, 195, 222, 255);
                case TerrainMap.Trench: return new Color32(140, 108, 70, 255);
                case TerrainMap.Crater: return new Color32(150, 140, 120, 255);
                default: return new Color32(220, 224, 200, 255);
            }
        }

        public static readonly Color32 Contour = new Color32(0, 0, 0, 70);
        public static readonly Color32 ContourMajor = new Color32(0, 0, 0, 120);

        public static readonly Color32 Blue = new Color32(40, 100, 190, 255);
        public static readonly Color32 BlueDark = new Color32(20, 50, 110, 255);
        public static readonly Color32 Red = new Color32(220, 110, 30, 255);
        public static readonly Color32 RedDark = new Color32(120, 55, 10, 255);
        public static readonly Color32 Gray = new Color32(130, 130, 130, 200);

        public static readonly Color32 Selected = new Color32(255, 220, 0, 255);
        public static readonly Color32 Path = new Color32(255, 255, 255, 230);
        public static readonly Color32 PathShadow = new Color32(0, 0, 0, 120);
        public static readonly Color32 Hover = new Color32(255, 240, 80, 255);

        public static readonly Color32 SightVisible = new Color32(60, 200, 80, 110);
        public static readonly Color32 SightBlocked = new Color32(220, 50, 50, 90);
        public static readonly Color32 Exposure = new Color32(230, 40, 40, 100);
        public static readonly Color32 Trigger = new Color32(200, 40, 200, 220);
        public static readonly Color32 TriggerFired = new Color32(120, 120, 120, 200);
        public static readonly Color32 EscapeRay = new Color32(80, 80, 80, 180);
        public static readonly Color32 EscapeBest = new Color32(0, 160, 60, 255);
        public static readonly Color32 EscapeNone = new Color32(220, 30, 30, 255);

        public static Color32 SideColor(Side s) => s == Side.Blue ? Blue : Red;
        public static Color32 SideDarkColor(Side s) => s == Side.Blue ? BlueDark : RedDark;

        public static Color32 WithAlpha(Color32 c, byte a) => new Color32(c.r, c.g, c.b, a);

        /// <summary>白と混ぜて薄くする（動揺の表示）。</summary>
        public static Color32 Lighten(Color32 c, float t) =>
            new Color32((byte)(c.r + (255 - c.r) * t), (byte)(c.g + (255 - c.g) * t), (byte)(c.b + (255 - c.b) * t), c.a);
    }
}
