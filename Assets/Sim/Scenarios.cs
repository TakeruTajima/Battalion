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

    /// <summary>検算用の配置。全部隊はその場で保持する。</summary>
    public sealed class Scenario
    {
        public string Name;
        public readonly List<CompanySetup> Companies = new List<CompanySetup>();
        /// <summary>
        /// 地図の範囲。P0 には地図がないので、仕様の 3km×2km を配置の中心付近に置く。
        /// </summary>
        public MapBounds Bounds = new MapBounds(-1200, -850, 1800, 1150);

        public Scenario(string name, params CompanySetup[] companies)
        {
            Name = name;
            Companies.AddRange(companies);
        }

        public Simulation Build(SimParams p, ulong seed, IEventSink log = null)
        {
            var sim = new Simulation(p, seed, Bounds, log);
            foreach (var s in Companies)
                sim.AddCompany(s.Name, s.Side, s.Training, s.Personality, new Vec2(s.X, s.Y), s.FacingDeg);
            return sim;
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
