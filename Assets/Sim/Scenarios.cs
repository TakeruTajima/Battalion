using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    public sealed class CompanySetup
    {
        public string Name;
        public Side Side;
        public Training Training = Training.Regular;
        public Personality Personality = Personality.Calm;
        public double X, Y, FacingDeg;

        public CompanySetup(string name, Side side, Training training, double x, double y, double facingDeg)
        {
            Name = name;
            Side = side;
            Training = training;
            X = x;
            Y = y;
            FacingDeg = facingDeg;
        }
    }

    /// <summary>場面の中で、決まった時刻に出す前進の命令（R1 の検証の場面。伝令は使わない）。</summary>
    public sealed class SceneOrder
    {
        public double AtSec;
        public string Unit;
        public double X, Y, FacingDeg;
    }

    /// <summary>検算用の配置。命令がなければ、全部隊はその場で保持する。</summary>
    public sealed class Scenario
    {
        public string Name;
        public readonly List<CompanySetup> Companies = new List<CompanySetup>();
        /// <summary>
        /// 塹壕を置く中隊の名前（R1）。その中隊の中心のマスと、正面に沿って左右2マスずつ（5マス、250m）を塹壕にする。
        /// </summary>
        public readonly List<string> TrenchUnits = new List<string>();
        /// <summary>時刻順の前進の命令（R1）。時刻 0 の命令は会戦の初めに渡す。</summary>
        public readonly List<SceneOrder> Orders = new List<SceneOrder>();
        /// <summary>通れない水のマスにする場所（マスの中心で判定。null ならなし。R1 の S7・S8 の両脇）。</summary>
        public Func<Vec2, bool> Water;
        /// <summary>
        /// 全て平地の地図（60×40マス、3km×2km）。原点を (−1200, −850) にずらして配置の中心付近に置く。
        /// 壊走した中隊が会戦から離脱する範囲は P0 と同じ（x −1200〜1800、y −850〜1150）。
        /// </summary>
        public static TerrainMap FlatMap(SimParams p) => TerrainMap.Flat(p.Terrain, -1200, -850, 60, 40);

        public Scenario(string name, params CompanySetup[] companies)
        {
            Name = name;
            Companies.AddRange(companies);
        }

        public Simulation Build(SimParams p, ulong seed, IEventSink log = null)
        {
            var map = FlatMap(p);
            if (Water != null)
                for (int j = 0; j < map.Height; j++)
                    for (int i = 0; i < map.Width; i++)
                        if (Water(map.CellCenter(i, j))) map.SetOverlay(i, j, TerrainMap.River);
            foreach (var name in TrenchUnits)
            {
                var s = Companies.Find(x => x.Name == name);
                map.CellOf(new Vec2(s.X, s.Y), out int ci, out int cj);
                double a = (s.FacingDeg + 90) * Angles.DegToRad;
                int di = (int)Math.Round(Math.Cos(a)), dj = (int)Math.Round(Math.Sin(a));
                for (int k = -2; k <= 2; k++)
                    if (map.InBounds(ci + k * di, cj + k * dj)) map.SetOverlay(ci + k * di, cj + k * dj, TerrainMap.Trench);
            }
            var sim = new Simulation(p, seed, map, log);
            foreach (var s in Companies)
                sim.AddCompany(s.Name, s.Side, s.Training, s.Personality, new Vec2(s.X, s.Y), s.FacingDeg);
            return sim;
        }

        /// <summary>時刻が来た命令を渡す（<paramref name="next"/> は次に渡す命令の添字）。毎刻みの前に呼ぶ。</summary>
        public void IssueDue(Simulation sim, ref int next)
        {
            while (next < Orders.Count && Orders[next].AtSec <= sim.Time + 1e-9)
            {
                var o = Orders[next++];
                sim.Issue(Order.Advance(sim.Find(o.Unit).Id, new Vec2(o.X, o.Y), o.FacingDeg * Angles.DegToRad));
            }
        }
    }

    /// <summary>
    /// docs/reference/combat_reference.py の S1〜S6 と同じ配置（座標 m、向きは度、x 正が東）。
    /// </summary>
    public static class Scenarios
    {
        static CompanySetup Blue(string n, double x, double y, double f, Training t = Training.Regular) =>
            new CompanySetup(n, Side.Blue, t, x, y, f);

        static CompanySetup Red(string n, double x, double y, double f, Training t = Training.Regular) =>
            new CompanySetup(n, Side.Red, t, x, y, f);

        public static Scenario S1_1v1_front() =>
            new Scenario("S1_1v1_front", Blue("A", 0, 0, 0), Red("R", 300, 0, 180));

        public static Scenario S2_2v1_front_front() =>
            new Scenario("S2_2v1_front_front", Blue("A", 0, 0, 0), Blue("B", 0, 220, -36), Red("R", 300, 0, 180));

        public static Scenario S3_2v1_front_flank() =>
            new Scenario("S3_2v1_front_flank", Blue("A", 0, 0, 0), Blue("B", 300, 300, -90), Red("R", 300, 0, 180));

        public static Scenario S4_2v1_front_rear() =>
            new Scenario("S4_2v1_front_rear", Blue("A", 0, 0, 0), Blue("B", 600, 0, 180), Red("R", 300, 0, 180));

        public static Scenario S5_vet_vs_reg() =>
            new Scenario("S5_vet_vs_reg", Blue("V", 0, 0, 0, Training.Veteran), Red("R", 300, 0, 180));

        /// <summary>4対1：R が四方（西・北・南・東）を囲まれる。どの方位にも逃げ道がなく、壊走すれば降伏する。</summary>
        public static Scenario S6_4v1_surrounded() =>
            new Scenario("S6_4v1_surrounded", Blue("A", 0, 0, 0), Blue("B", 300, 300, -90), Blue("C", 300, -300, 90),
                Blue("D", 600, 0, 180), Red("R", 300, 0, 180));

        /// <summary>接近戦の検算：50m で対峙した同数（通常練度）の正面衝突。</summary>
        public static Scenario CC_1v1_front_50m() =>
            new Scenario("CC_1v1_front_50m", Blue("A", 0, 0, 0), Red("R", 50, 0, 180));

        // ---------------------------------------------------------------- R1 の検証の場面（強い陣地）

        static SceneOrder Advance(double at, string unit, double x, double y, double facingDeg) =>
            new SceneOrder { AtSec = at, Unit = unit, X = x, Y = y, FacingDeg = facingDeg };

        /// <summary>
        /// S7・S8 の脇を塞いだ陣地：攻める側が近づける幅は 400m（|y| ≤ 200）。守備の線の手前 150m から守備の後ろの辺まで
        /// （250 ≤ x < 450）は守備の正面幅（|y| ≤ 100）だけになり、守備の両脇は水。守備の正面の弧（±60°）の外へは、正面を
        /// 通らずに回れない（x = 250 で |y| = 200 の点は、守備から見て 53°）。守備の後ろ（x ≥ 450）は 400m の幅に戻る。
        /// </summary>
        static bool FlankedPositionWater(Vec2 p) => p.X >= 250 && p.X < 450 ? Math.Abs(p.Y) > 100 : Math.Abs(p.Y) > 200;

        /// <summary>S7・S8 の配置：塹壕の R（x=400、西向き）、攻める A・C が前（x=0、y=∓100）、B が後ろ（x=−200）。</summary>
        static Scenario FlankedPosition(string name)
        {
            var sc = new Scenario(name, Blue("A", 0, -100, 0), Blue("B", -200, 0, 0), Blue("C", 0, 100, 0), Red("R", 400, 0, 180));
            sc.TrenchUnits.Add("R");
            sc.Water = FlankedPositionWater;
            return sc;
        }

        /// <summary>
        /// S7 正面からの突撃：脇を塞いだ陣地の R に、前の A・C と後ろの B が、守備の線まで前進する（突撃。R1 の設計で、今の
        /// 攻撃の命令は 250m の発起点で止まるため前進で作る）。
        /// </summary>
        public static Scenario S7_assault_front()
        {
            var sc = FlankedPosition("S7_assault_front");
            foreach (var (n, y) in new[] { ("A", -100.0), ("C", 100.0), ("B", 0.0) }) sc.Orders.Add(Advance(0, n, 400, y, 0));
            return sc;
        }

        /// <summary>S8 抑えてから詰める：S7 と同じ配置。前の A・C は 300m（x=100）へ出て保持して撃ち、3分後に後ろの B が守備の線まで前進する。</summary>
        public static Scenario S8_suppress_then_assault()
        {
            var sc = FlankedPosition("S8_suppress_then_assault");
            sc.Orders.Add(Advance(0, "A", 100, -100, 0));
            sc.Orders.Add(Advance(0, "C", 100, 100, 0));
            sc.Orders.Add(Advance(180, "B", 400, 0, 0));
            return sc;
        }

        /// <summary>
        /// S9 射界を横切る：塹壕の R（原点、北向き）の前を、M が東向きに 600m 前進する。M の矩形の手前の辺が、R の矩形の
        /// 正面の辺から 100m（中心どうしは 225m。中隊の正面幅 200m・奥行 50m）。
        /// </summary>
        public static Scenario S9_cross_field_of_fire()
        {
            var sc = new Scenario("S9_cross_field_of_fire", Blue("M", -300, 225, 0), Red("R", 0, 0, 90));
            sc.TrenchUnits.Add("R");
            sc.Orders.Add(Advance(0, "M", 300, 225, 0));
            return sc;
        }

        public static Scenario ByName(string name)
        {
            switch (name)
            {
                case "S1_1v1_front": return S1_1v1_front();
                case "S2_2v1_front_front": return S2_2v1_front_front();
                case "S3_2v1_front_flank": return S3_2v1_front_flank();
                case "S4_2v1_front_rear": return S4_2v1_front_rear();
                case "S5_vet_vs_reg": return S5_vet_vs_reg();
                case "S6_4v1_surrounded": return S6_4v1_surrounded();
                case "CC_1v1_front_50m": return CC_1v1_front_50m();
                case "S7_assault_front": return S7_assault_front();
                case "S8_suppress_then_assault": return S8_suppress_then_assault();
                case "S9_cross_field_of_fire": return S9_cross_field_of_fire();
                default: throw new KeyNotFoundException(name);
            }
        }

        public static readonly string[] Reference =
        {
            "S1_1v1_front", "S2_2v1_front_front", "S3_2v1_front_flank", "S4_2v1_front_rear", "S5_vet_vs_reg",
            "S6_4v1_surrounded",
        };
    }

    /// <summary>1回の対戦の結果（最初の壊走または打ち切りまで）。</summary>
    public sealed class BattleResult
    {
        public double EndTimeMin;
        public bool Routed;
        public readonly List<string> RoutedUnits = new List<string>();
        /// <summary>壊走した中隊のうち、包囲されていて降伏したもの。</summary>
        public readonly List<string> SurrenderedUnits = new List<string>();
        public readonly Dictionary<string, double> LossRate = new Dictionary<string, double>();
    }

    public static class BattleRunner
    {
        /// <summary>最初の壊走（降伏を含む）か、<paramref name="capMin"/> 分で打ち切る。</summary>
        public static BattleResult RunToFirstRout(Scenario sc, SimParams p, ulong seed, double capMin = 60.0, IEventSink log = null)
        {
            var sim = sc.Build(p, seed, log);
            double capSec = capMin * 60.0;
            while (sim.Time < capSec)
            {
                sim.Step();
                if (sim.FirstRoutTime.HasValue) break;
            }
            var r = new BattleResult { EndTimeMin = sim.Time / 60.0, Routed = sim.FirstRoutTime.HasValue };
            foreach (var c in sim.Companies)
            {
                r.LossRate[c.Name] = c.LossRate;
                if (c.State == CompanyState.Routed || c.State == CompanyState.Surrendered) r.RoutedUnits.Add(c.Name);
                if (c.State == CompanyState.Surrendered) r.SurrenderedUnits.Add(c.Name);
            }
            return r;
        }
    }
}
