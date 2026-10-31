using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 教程1（側面を突く）の通し実行（仕様「ステージの作り直し：教程」の表）：6つの作戦を各20回（シード1〜20）、裁量は任せる、
    /// 攻撃の「崩れたら」は確保。勝率・青と赤の損害率・崩した時刻と確保した時刻の中央値・崩れた中隊を表にし、仕様の線を確かめる。
    /// </summary>
    [Category("Slow")]
    public class TutorialReportTests
    {
        public const int Runs = 20;

        /// <summary>作戦の命令：時刻（秒）と、中隊と命令書（1〜3段）の作り方。時刻 0 の命令は伝令を使わずに届く。それ以外は青の本部から伝令で出す。</summary>
        public readonly struct TutorialOrder
        {
            public readonly double AtSec;
            public readonly string Unit;
            public readonly Func<Simulation, Company, OrderSheet> Make;

            public TutorialOrder(double atSec, string unit, Func<Simulation, Company, OrderSheet> make)
            {
                AtSec = atSec;
                Unit = unit;
                Make = make;
            }
        }

        public const string TotalAssault = "総員突撃";
        public const string FrontFire = "正面火力戦";
        public const string FlankOnly = "側面攻撃（拘束なし）";
        public const string PinFlankFar = "拘束＋迂回（遠い拘束）";
        public const string PinFlankNear = "拘束＋迂回（近い拘束）";
        public const string PinBothFlanks = "拘束＋両脇";

        // R1 は (975, 800)、R2 は (975, 600) で、ともに西向き。北の脇は +90°、南の脇は -90°
        static Func<Simulation, Company, Order> Adv(double x, double y) => (s, c) => Order.Advance(c.Id, new Vec2(x, y), 0);
        static Func<Simulation, Company, Order> Pin(PinRange r) => (s, c) => Order.Pin(c.Id, s.Find("R1").Id, r);
        static Func<Simulation, Company, Order> AttackFrom(string target, double bearingDeg) =>
            (s, c) => Order.AttackFrom(c.Id, s.Find(target).Id, bearingDeg * Angles.DegToRad, AttackOnBreak.Secure);

        /// <summary>命令書：第1段に条件なし、第2段・第3段は「完了したら」。</summary>
        static TutorialOrder Sheet(double at, string u, params Func<Simulation, Company, Order>[] stages) =>
            new TutorialOrder(at, u, (s, c) => new OrderSheet(stages.Select((make, k) =>
                new Stage(make(s, c), k == 0 ? StageTrigger.None : StageTrigger.Complete))));

        /// <summary>
        /// 仕様の「青の作戦の命令」の6作戦。塹壕の確保は、R1 の位置 (975, 800) と R2 の位置 (975, 600) への前進で表す。
        /// - 総員突撃：B1 は R1 の位置、B2 は R2 の位置へ前進。B3 は B2 と同じ R2 の位置へ前進（後ろから出るので B2 の後ろを続く第二波）。
        ///   前進の目的地が塹壕なので、崩れたら（阻まれなくなったら）そのまま塹壕に入る。
        /// - 正面火力戦：B1・B2 が塹壕の 250m 前（x 725、y 800・600）で撃つ。B3 は初めの位置（その後ろ）で保持（命令なし）。
        /// - 側面攻撃（拘束なし）：B1 が北の脇から R1 を攻撃（森を通るかは経路が決める）。ほかは保持（命令なし）。
        /// - 拘束＋迂回：B2 が R1 を遠い（近い）拘束（初めの位置からの方角で、R1・R2 の間の正面に近い点）。
        ///   B1 は 第1段 R1 を北から攻撃 →（完了したら）第2段 R2 を北から攻撃 →（完了したら）第3段 R1 の位置へ前進。
        ///   B3 は B1 に続く：2分後に同じ段の命令書（第3段は R2 の位置へ前進）。
        /// - 拘束＋両脇：B2 が遠い拘束。B1 は R1 を北から攻撃 → R1 の位置へ前進、B3 は R2 を南から攻撃 → R2 の位置へ前進。
        /// 攻撃は全て「崩れたら確保」。
        /// </summary>
        public static List<(string name, TutorialOrder[] orders)> Plans() => new List<(string, TutorialOrder[])>
        {
            (TotalAssault, new[] { Sheet(0, "B1", Adv(975, 800)), Sheet(0, "B2", Adv(975, 600)), Sheet(0, "B3", Adv(975, 600)) }),
            (FrontFire, new[] { Sheet(0, "B1", Adv(725, 800)), Sheet(0, "B2", Adv(725, 600)) }),
            (FlankOnly, new[] { Sheet(0, "B1", AttackFrom("R1", 90)) }),
            (PinFlankFar, PinFlank(PinRange.Far)),
            (PinFlankNear, PinFlank(PinRange.Near)),
            (PinBothFlanks, new[]
            {
                Sheet(0, "B2", Pin(PinRange.Far)),
                Sheet(0, "B1", AttackFrom("R1", 90), Adv(975, 800)),
                Sheet(0, "B3", AttackFrom("R2", -90), Adv(975, 600)),
            }),
        };

        static TutorialOrder[] PinFlank(PinRange r) => new[]
        {
            Sheet(0, "B2", Pin(r)),
            Sheet(0, "B1", AttackFrom("R1", 90), AttackFrom("R2", 90), Adv(975, 800)),
            Sheet(120, "B3", AttackFrom("R1", 90), AttackFrom("R2", 90), Adv(975, 600)),
        };

        public sealed class RunResult
        {
            public bool Win;
            public double EndMin, BlueLoss, RedLoss;
            public double? BrokenMin, SecuredMin;
            public readonly HashSet<string> Routed = new HashSet<string>();
        }

        /// <summary>作戦を1回回す。<paramref name="timeLimitMin"/> が null なら地図ファイルの制限時間。</summary>
        public static RunResult RunOne(MapScenario sc, TutorialOrder[] plan, ulong seed, SimParams p = null, double? timeLimitMin = null)
        {
            var sim = sc.Build(p ?? TestUtil.Params, seed);
            var times = new TrenchClearTimes(sc);
            var result = new RunResult();
            var orders = plan.OrderBy(o => o.AtSec).ToList();
            int next = 0;
            double endSec = (timeLimitMin ?? sc.TimeLimitMin) * 60;
            while (sim.Time < endSec - 1e-9)
            {
                // 判断枠がなければ、空くまで毎刻み出し直す
                while (next < orders.Count && orders[next].AtSec <= sim.Time)
                {
                    var o = orders[next];
                    var c = sim.Find(o.Unit);
                    if (sim.SendSheet(o.Make(sim, c)) == SendResult.NoSlot) break;
                    next++;
                }
                sim.Step();
                times.Observe(sim);
                foreach (var c in sim.Companies)
                    if (c.State == CompanyState.Routed || c.State == CompanyState.Surrendered) result.Routed.Add(c.Name);
                bool redLeft = sim.Companies.Any(c => c.Side == Side.Red && c.OnBoard);
                bool blueLeft = sim.Companies.Any(c => c.Side == Side.Blue && c.OnBoard);
                if (!redLeft || !blueLeft) break;
            }
            RunProgress.Tick();
            double Loss(Side side)
            {
                var cs = sim.Companies.Where(c => c.Side == side).ToList();
                return cs.Sum(c => c.InitialStrength - c.Strength) / (double)cs.Sum(c => c.InitialStrength);
            }
            result.Win = sc.BlueWins(sim);
            result.EndMin = sim.Time / 60;
            result.BlueLoss = Loss(Side.Blue);
            result.RedLoss = Loss(Side.Red);
            result.BrokenMin = times.BrokenSec / 60;
            result.SecuredMin = times.SecuredSec / 60;
            return result;
        }

        public sealed class PlanSummary
        {
            public string Name;
            public double WinRate, BlueLoss, RedLoss;
            public int Broken, Secured;
            public double? BrokenMedianMin, SecuredMedianMin, WinSecuredMedianMin;
            public readonly Dictionary<string, int> RoutedCount = new Dictionary<string, int>();
        }

        public static PlanSummary Summarize(string name, List<RunResult> rs)
        {
            double? Med(IEnumerable<double> xs)
            {
                var l = xs.ToList();
                return l.Count == 0 ? (double?)null : TestUtil.Median(l);
            }
            var s = new PlanSummary
            {
                Name = name,
                WinRate = rs.Count(r => r.Win) / (double)rs.Count,
                BlueLoss = rs.Average(r => r.BlueLoss),
                RedLoss = rs.Average(r => r.RedLoss),
                Broken = rs.Count(r => r.BrokenMin.HasValue),
                Secured = rs.Count(r => r.SecuredMin.HasValue),
                BrokenMedianMin = Med(rs.Where(r => r.BrokenMin.HasValue).Select(r => r.BrokenMin.Value)),
                SecuredMedianMin = Med(rs.Where(r => r.SecuredMin.HasValue).Select(r => r.SecuredMin.Value)),
                WinSecuredMedianMin = Med(rs.Where(r => r.Win && r.SecuredMin.HasValue).Select(r => r.SecuredMin.Value)),
            };
            foreach (var r in rs)
                foreach (var u in r.Routed)
                    s.RoutedCount[u] = (s.RoutedCount.TryGetValue(u, out int n) ? n : 0) + 1;
            return s;
        }

        public static List<PlanSummary> RunAll(SimParams p = null, double? timeLimitMin = null)
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf(T1Tests.TutorialPath));
            var all = new List<PlanSummary>();
            foreach (var (name, orders) in Plans())
            {
                var rs = new List<RunResult>();
                for (ulong seed = 1; seed <= Runs; seed++) rs.Add(RunOne(sc, orders, seed, p, timeLimitMin));
                all.Add(Summarize(name, rs));
            }
            return all;
        }

        static string Min(double? m) => m.HasValue ? $"{m:0.0}分" : "—";

        public static string Table(List<PlanSummary> all)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 作戦 | 勝率 | 青の損害率 | 赤の損害率 | 崩した（回/20、時刻の中央値） | 確保した（回/20、時刻の中央値） | 勝った会戦の確保の時刻の中央値 | 崩れた中隊（回/20） |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var s in all)
            {
                string routed = s.RoutedCount.Count == 0 ? "—" : string.Join("、", s.RoutedCount.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));
                sb.AppendLine($"| {s.Name} | {s.WinRate:0.00} | {s.BlueLoss:0.000} | {s.RedLoss:0.000} | {s.Broken}（{Min(s.BrokenMedianMin)}） | " +
                              $"{s.Secured}（{Min(s.SecuredMedianMin)}） | {Min(s.WinSecuredMedianMin)} | {routed} |");
            }
            return sb.ToString();
        }

        /// <summary>仕様の線を満たさないものを返す（空なら全て満たす）。</summary>
        public static List<string> CheckLines(List<PlanSummary> all)
        {
            PlanSummary P(string n) => all.First(s => s.Name == n);
            var fails = new List<string>();
            if (P(TotalAssault).WinRate > 0.3 + 1e-9) fails.Add($"総員突撃 {P(TotalAssault).WinRate:0.00} > 0.3");
            if (P(FrontFire).WinRate > 0.3 + 1e-9) fails.Add($"正面火力戦 {P(FrontFire).WinRate:0.00} > 0.3");
            var best = P(PinFlankFar).WinRate >= P(PinFlankNear).WinRate ? P(PinFlankFar) : P(PinFlankNear);
            if (best.WinRate < 0.8 - 1e-9) fails.Add($"拘束＋迂回の良い方（{best.Name}）{best.WinRate:0.00} < 0.8");
            if (P(TotalAssault).BlueLoss <= best.BlueLoss) fails.Add($"総員突撃の青の損害 {P(TotalAssault).BlueLoss:0.000} が拘束＋迂回 {best.BlueLoss:0.000} より大きくない");
            var flank = P(FlankOnly);
            if (best.WinSecuredMedianMin.HasValue && flank.WinSecuredMedianMin.HasValue &&
                best.WinSecuredMedianMin.Value >= flank.WinSecuredMedianMin.Value - 1e-9)
                fails.Add($"拘束＋迂回の確保の時刻 {best.WinSecuredMedianMin:0.0}分 が側面攻撃（拘束なし）{flank.WinSecuredMedianMin:0.0}分 より早くない");
            if (!best.WinSecuredMedianMin.HasValue) fails.Add("拘束＋迂回の勝った会戦で確保した回がない");
            return fails;
        }

        [Test]
        public void SixPlansMeetTheTutorialLines()
        {
            using var progress = RunProgress.Begin("教程1の6作戦", 6 * Runs);
            var all = RunAll();
            TestContext.WriteLine(Table(all));
            var fails = CheckLines(all);
            TestContext.WriteLine(fails.Count == 0 ? "線：全て満たす" : "線を満たさない：" + string.Join("／", fails));
            Assert.That(fails, Is.Empty);
        }

        /// <summary>制限時間を 35〜45分で振る（線を満たさないとき用。ふだんは回さない）。</summary>
        [Test, Explicit("制限時間の掃き出し")]
        public void TimeLimitSweep()
        {
            foreach (double min in new[] { 35.0, 40.0, 45.0 })
            {
                var all = RunAll(null, min);
                TestContext.WriteLine($"## 制限時間 {min:0}分");
                TestContext.WriteLine(Table(all));
                var fails = CheckLines(all);
                TestContext.WriteLine(fails.Count == 0 ? "線：全て満たす" : "線を満たさない：" + string.Join("／", fails));
            }
        }
    }
}
