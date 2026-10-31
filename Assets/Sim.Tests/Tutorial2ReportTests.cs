using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using static Battalion.Sim.Tests.TutorialReportTests;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 教程2（抑えてから詰める）の通し実行（仕様「教程2：抑えてから詰める（T4）」の表）：5つの作戦を各20回（シード1〜20）、裁量は任せる。
    /// 教程1 と同じ表に加えて、B3 が地峡に入ってから着くまでの秒数と、その間に R1 が制圧されていた秒数を出す。
    /// </summary>
    [Category("Slow")]
    public class Tutorial2ReportTests
    {
        public const string TotalAssault = "総員突撃";
        public const string FrontFire = "正面火力戦";
        public const string SuppressThenClose = "抑えてから詰める";
        public const string CloseWithoutSuppress = "抑えずに詰める";
        public const string EarlyClose = "時機の早い抑え";

        /// <summary>塹壕の位置（R1 の中心）。</summary>
        static readonly Vec2 Trench = new Vec2(975, 725);

        /// <summary>撃つ位置：射程の端（塹壕から 390m、正面から 25°。仕様「教程2 の報告の後に決めたこと」の2）。</summary>
        static readonly Vec2 FireNorth = new Vec2(622, 890), FireSouth = new Vec2(622, 560);

        /// <summary>地峡（二つの湖の間、x 700〜1250・y 650〜800）。</summary>
        public static bool InIsthmus(Vec2 p) => p.X >= 700 && p.X <= 1250 && p.Y >= 650 && p.Y <= 800;

        static readonly ApproachWatch Watch = new ApproachWatch { Unit = "B3", Defender = "R1", InApproach = InIsthmus };

        static Func<Simulation, Company, Order> Hold => (s, c) => Order.Hold(c.Id);

        /// <summary>第1段の命令の後、緑の信号弾で塹壕の位置へ前進する命令書。</summary>
        static TutorialOrder ThenTakeOnGreen(string u, Func<Simulation, Company, Order> first) =>
            new TutorialOrder(0, u, (s, c) => new OrderSheet(new[]
            {
                new Stage(first(s, c), StageTrigger.None),
                new Stage(Adv(Trench.X, Trench.Y)(s, c), StageTrigger.OnFlare(FlareColor.Green)),
            }));

        /// <summary>
        /// 緑の信号弾を上げる時：抑えてから詰めるは R1 が初めて制圧された刻み（「伏せた」の合図）、ほかは R1 を初めて崩した刻み（「崩れた」の合図）。
        /// </summary>
        public static Func<Simulation, TrenchClearTimes, bool> GreenWhen(string plan) =>
            plan == SuppressThenClose ? (s, t) => s.Find("R1").Suppressed : (Func<Simulation, TrenchClearTimes, bool>)null;

        /// <summary>
        /// 仕様の表の5作戦。前進は全て東向き。塹壕の位置へ前進した中隊は、崩れた後に阻まれなくなればそのまま塹壕に入る（確保）。
        /// - 総員突撃：3個中隊が塹壕の位置 (975, 700) へ前進。
        /// - 正面火力戦：B1 (622, 890)・B2 (622, 560)・B3 (550, 750) へ前進して撃ち、崩れたら（緑の信号弾）3個とも塹壕の位置へ前進。
        /// - 抑えてから詰める：B1 (622, 890)・B2 (622, 560) へ前進して撃つ。B3 は保持して、R1 が伏せたら（緑の信号弾）塹壕の位置へ前進。
        /// - 抑えずに詰める：B3 だけ時刻 0 に塹壕の位置へ前進。B1・B2 は保持（命令なし）。
        /// - 時機の早い抑え：B1・B2 が撃つ位置へ、B3 が塹壕の位置へ、時刻 0 に同時に前進。
        /// </summary>
        public static List<(string name, TutorialOrder[] orders)> Plans() => new List<(string, TutorialOrder[])>
        {
            (TotalAssault, new[] { Sheet(0, "B1", Adv(Trench.X, Trench.Y)), Sheet(0, "B2", Adv(Trench.X, Trench.Y)), Sheet(0, "B3", Adv(Trench.X, Trench.Y)) }),
            (FrontFire, new[]
            {
                ThenTakeOnGreen("B1", Adv(FireNorth.X, FireNorth.Y)), ThenTakeOnGreen("B2", Adv(FireSouth.X, FireSouth.Y)), ThenTakeOnGreen("B3", Adv(550, 750)),
            }),
            (SuppressThenClose, new[]
            {
                Sheet(0, "B1", Adv(FireNorth.X, FireNorth.Y)),
                Sheet(0, "B2", Adv(FireSouth.X, FireSouth.Y)),
                ThenTakeOnGreen("B3", Hold),
            }),
            (CloseWithoutSuppress, new[] { Sheet(0, "B3", Adv(Trench.X, Trench.Y)) }),
            (EarlyClose, new[] { Sheet(0, "B1", Adv(FireNorth.X, FireNorth.Y)), Sheet(0, "B2", Adv(FireSouth.X, FireSouth.Y)), Sheet(0, "B3", Adv(Trench.X, Trench.Y)) }),
        };

        public static List<PlanSummary> RunAll(double? timeLimitMin = null) =>
            TutorialReportTests.RunAll(T2Tests.TutorialPath, Plans(), null, timeLimitMin, Watch, GreenWhen);

        /// <summary>B3 が地峡に入ってから着くまでの秒数と、その間に R1 が制圧されていた秒数（中央値、20回）。</summary>
        public static string ApproachTable(List<PlanSummary> all)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 作戦 | B3 が地峡にいた秒数（中央値） | その間に R1 が制圧されていた秒数（中央値） | 制圧されていた割合（全会戦の合計） |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var s in all)
            {
                var a = s.Runs.Select(r => r.ApproachSec ?? 0).ToList();
                var q = s.Runs.Select(r => r.SuppressedSec ?? 0).ToList();
                string ratio = a.Sum() > 0 ? $"{q.Sum() / a.Sum():0.00}" : "—";
                sb.AppendLine($"| {s.Name} | {TestUtil.Median(a):0} | {TestUtil.Median(q):0} | {ratio} |");
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
            var aim = P(SuppressThenClose);
            if (aim.WinRate < 0.8 - 1e-9) fails.Add($"抑えてから詰める {aim.WinRate:0.00} < 0.8");
            if (P(TotalAssault).BlueLoss <= aim.BlueLoss)
                fails.Add($"総員突撃の青の損害 {P(TotalAssault).BlueLoss:0.000} が抑えてから詰める {aim.BlueLoss:0.000} より大きくない");
            return fails;
        }

        static void Print(List<PlanSummary> all)
        {
            TestContext.WriteLine(Table(all));
            TestContext.WriteLine(ApproachTable(all));
            var fails = CheckLines(all);
            TestContext.WriteLine(fails.Count == 0 ? "線：全て満たす" : "線を満たさない：" + string.Join("／", fails));
        }

        [Test]
        public void FivePlansMeetTheTutorialLines()
        {
            using var progress = RunProgress.Begin("教程2の5作戦", 5 * Runs);
            var all = RunAll();
            Print(all);
            Assert.That(CheckLines(all), Is.Empty);
        }

        /// <summary>制限時間を 25〜40分で振る（線を満たさないとき用。ふだんは回さない）。</summary>
        [Test, Explicit("制限時間の掃き出し")]
        public void TimeLimitSweep()
        {
            foreach (double min in new[] { 25.0, 30.0, 35.0, 40.0 })
            {
                TestContext.WriteLine($"## 制限時間 {min:0}分");
                Print(RunAll(min));
            }
        }
    }
}
