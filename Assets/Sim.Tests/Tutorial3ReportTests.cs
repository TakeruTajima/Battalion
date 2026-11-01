using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using static Battalion.Sim.Tests.TutorialReportTests;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 教程3（砲撃に合わせて詰める）の通し実行（仕様「教程3：砲撃に合わせて詰める（T17、P3 の後）」の表）：6つの作戦を各20回（シード1〜20）、
    /// 裁量は任せる。教程1・2 と同じ表に加えて、砲撃が止んだ時の B2 と塹壕の距離、B2 が接近戦の距離に入った時刻（砲撃が止んでからの秒）、
    /// 詰めるまでの B2 の損害を出す。
    /// </summary>
    [Category("Slow")]
    public class Tutorial3ReportTests
    {
        public const string TotalAssault = "総員突撃";
        public const string SuppressThenClose = "抑えてから詰める";
        public const string ShellThenClose = "砲撃してから詰める";
        public const string Barrage = "砲撃と前進の連動（二波）";
        public const string BarrageCover = "連動・撃って支える";
        public const string ShellOnly = "砲撃だけ";

        /// <summary>塹壕の位置（R1 の中心）。計画射撃点もここ。</summary>
        public static readonly Vec2 Trench = new Vec2(975, 725);

        /// <summary>B1 の撃つ位置：教程2 の北の位置（塹壕から 390m、射程の端）。</summary>
        static readonly Vec2 FireNorth = new Vec2(622, 890);

        /// <summary>砲撃してから詰める：B2 が待つ位置（塹壕の 500m 手前、地峡の入口の西）。</summary>
        static readonly Vec2 Wait500 = new Vec2(475, 725);

        static readonly BarrageWatch Watch = new BarrageWatch { Unit = "B2", Defender = "R1", Trench = Trench };

        static Func<Simulation, Company, Order> Hold => (s, c) => Order.Hold(c.Id);

        static TutorialOrder ThenOnGreen(string u, Func<Simulation, Company, Order> first, Func<Simulation, Company, Order> second) =>
            new TutorialOrder(0, u, (s, c) => new OrderSheet(new[]
            {
                new Stage(first(s, c), StageTrigger.None),
                new Stage(second(s, c), StageTrigger.OnFlare(FlareColor.Green)),
            }));

        /// <summary>
        /// 仕様の表の6作戦。前進は全て東向き。塹壕の位置へ前進した中隊は、崩れた後に阻まれなくなればそのまま塹壕に入る（確保）。
        /// - 総員突撃：B1・B2 が塹壕の位置へ前進（砲撃なし）。
        /// - 抑えてから詰める：B1 が北の撃つ位置へ前進して撃つ。B2 は保持して、R1 が伏せたら（緑の信号弾）塹壕の位置へ前進（砲撃なし）。
        /// - 砲撃してから詰める：塹壕へ 5分（初弾 1分）。B1 は北の撃つ位置。B2 は塹壕の 500m 手前で待ち、砲撃が止んだら（緑）塹壕の位置へ前進。
        /// - 砲撃と前進の連動（二波。狙った解、教程3 の報告の後に決めたこと）：塹壕へ 5分、B2 が 150m に着く時刻に止む。
        ///   B2 は始めから、B1 は 2分後に塹壕の位置へ前進（同じ道）。
        /// - 連動・撃って支える（線なし）：同じ砲撃。B2 は始めから塹壕の位置へ前進。B1 は北の撃つ位置で撃ち、
        ///   B2 が接近戦の距離に入ったら（緑）塹壕の位置へ前進。
        /// - 砲撃だけ：塹壕へ 5分（初弾 1分）。青は動かない。
        /// </summary>
        public static List<(string name, TutorialOrder[] orders)> Plans() => new List<(string, TutorialOrder[])>
        {
            (TotalAssault, new[] { Sheet(0, "B1", Adv(Trench.X, Trench.Y)), Sheet(0, "B2", Adv(Trench.X, Trench.Y)) }),
            (SuppressThenClose, new[] { Sheet(0, "B1", Adv(FireNorth.X, FireNorth.Y)), ThenOnGreen("B2", Hold, Adv(Trench.X, Trench.Y)) }),
            (ShellThenClose, new[] { Sheet(0, "B1", Adv(FireNorth.X, FireNorth.Y)), ThenOnGreen("B2", Adv(Wait500.X, Wait500.Y), Adv(Trench.X, Trench.Y)) }),
            (Barrage, new[] { Sheet(0, "B2", Adv(Trench.X, Trench.Y)), Sheet(120, "B1", Adv(Trench.X, Trench.Y)) }),
            (BarrageCover, new[] { ThenOnGreen("B1", Adv(FireNorth.X, FireNorth.Y), Adv(Trench.X, Trench.Y)), Sheet(0, "B2", Adv(Trench.X, Trench.Y)) }),
            (ShellOnly, new TutorialOrder[0]),
        };

        static bool FireEnded(Simulation s) => s.FireMissions.Any(m => m.Side == Side.Blue && m.State == FireMissionState.Done);

        /// <summary>緑の信号弾を上げる時：抑えてから詰めるは R1 が伏せた、砲撃してから詰めるは砲撃が止んだ、連動・撃って支えるは B2 が接近戦の距離に入った。</summary>
        static Func<Simulation, TrenchClearTimes, bool> GreenWhen(string plan)
        {
            switch (plan)
            {
                case SuppressThenClose: return (s, t) => s.Find("R1").Suppressed;
                case ShellThenClose: return (s, t) => FireEnded(s);
                case BarrageCover: return (s, t) => s.InCloseCombat(s.Find("B2"), s.Find("R1"));
                default: return (s, t) => false;
            }
        }

        /// <summary>塹壕を計画射撃点にして、<paramref name="startAt"/> から 5分。</summary>
        static Action<Simulation> Fire(double startAt, int rounds) => s =>
        {
            if (rounds != 40) { s.ClearArtillery(Side.Blue); s.SetArtillery(Side.Blue, rounds); }
            s.AddPlannedPoint(Side.Blue, Trench);
            s.RequestFire(Side.Blue, Trench, 5, startAt);
        };

        /// <summary>
        /// 1回回す。連動の2作戦は、砲撃が止む時に B2 が 150m に着くよう時刻を合わせる（S28 と同じ）：砲撃なしで一度回して B2 が射程の 20m 手前
        /// （420m）に着いた時刻を測り、残りの 270m をふつうの速さで歩く時間を足した時刻に止むよう撃つ。その会戦で B2 が 150m に着いた時刻が
        /// 30秒を越えてずれれば、着いた時刻に止むよう撃ち直す。
        /// </summary>
        public static RunResult RunPlan(MapScenario sc, string name, TutorialOrder[] orders, ulong seed, double? timeLimitMin = null, int rounds = 40)
        {
            var p = TestUtil.Params;
            var green = GreenWhen(name);
            if (name != Barrage && name != BarrageCover)
            {
                var start = name == ShellThenClose || name == ShellOnly ? Fire(60, rounds) : null;
                return RunOne(sc, orders, seed, null, timeLimitMin, null, green, start, Watch);
            }
            var dry = RunOne(sc, orders, seed, null, timeLimitMin, null, green, null, Watch);
            double end = dry.At420Sec.HasValue ? dry.At420Sec.Value + (p.Fire.RangeMaxM + 20 - 150) / p.Movement.SpeedMps : 360;
            var r = RunOne(sc, orders, seed, null, timeLimitMin, null, green, Fire(Math.Max(60, end - 300), rounds), Watch);
            if (r.At150Sec.HasValue && Math.Abs(r.At150Sec.Value - end) > 30)
                r = RunOne(sc, orders, seed, null, timeLimitMin, null, green, Fire(Math.Max(60, r.At150Sec.Value - 300), rounds), Watch);
            return r;
        }

        public static List<PlanSummary> RunAll(double? timeLimitMin = null, int rounds = 40)
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf(T3Tests.TutorialPath));
            var all = new List<PlanSummary>();
            foreach (var (name, orders) in Plans())
            {
                var rs = new List<RunResult>();
                for (ulong seed = 1; seed <= Runs; seed++) rs.Add(RunPlan(sc, name, orders, seed, timeLimitMin, rounds));
                all.Add(Summarize(name, rs));
            }
            return all;
        }

        /// <summary>詰めるまでの B2 の損害（接近戦の距離に入るまで。入らなければ終わりまで）の平均。</summary>
        public static double LossToClose(PlanSummary s) => s.Runs.Average(r => r.LossToClose ?? r.UnitLossEnd);

        public static string BarrageTable(List<PlanSummary> all)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 作戦 | 砲撃が止んだ時刻の中央値 | 止んだ時の B2 と塹壕の距離（平均） | B2 が接近戦の距離に入った（回/20） | 入った時刻（砲撃が止んでからの秒、中央値） | 詰めるまでの B2 の損害（平均、兵） |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var s in all)
            {
                var ends = s.Runs.Where(r => r.FireEndSec.HasValue).ToList();
                var closed = s.Runs.Where(r => r.CloseSec.HasValue).ToList();
                var after = closed.Where(r => r.FireEndSec.HasValue).Select(r => r.CloseSec.Value - r.FireEndSec.Value).ToList();
                sb.AppendLine($"| {s.Name} | {(ends.Count == 0 ? "—" : $"{TestUtil.Median(ends.Select(r => r.FireEndSec.Value / 60).ToList()):0.0}分")} | " +
                              $"{(ends.Count == 0 ? "—" : $"{ends.Average(r => r.DistAtFireEnd.Value):0}m")} | {closed.Count} | " +
                              $"{(after.Count == 0 ? "—" : $"{TestUtil.Median(after):0}秒")} | {LossToClose(s):0.0} |");
            }
            return sb.ToString();
        }

        /// <summary>仕様の線を満たさないものを返す（空なら全て満たす）。</summary>
        public static List<string> CheckLines(List<PlanSummary> all)
        {
            PlanSummary P(string n) => all.First(s => s.Name == n);
            var fails = new List<string>();
            foreach (var n in new[] { TotalAssault, SuppressThenClose, ShellThenClose })
                if (P(n).WinRate > 0.3 + 1e-9) fails.Add($"{n} {P(n).WinRate:0.00} > 0.3");
            if (P(Barrage).WinRate < 0.8 - 1e-9) fails.Add($"{Barrage} {P(Barrage).WinRate:0.00} < 0.8");
            double a = LossToClose(P(Barrage)), b = LossToClose(P(ShellThenClose));
            if (a > b / 2 + 1e-9) fails.Add($"詰めるまでの B2 の損害 {Barrage} {a:0.0} が砲撃してから詰める {b:0.0} の半分を越える");
            return fails;
        }

        static void Print(List<PlanSummary> all)
        {
            TestContext.WriteLine(Table(all));
            TestContext.WriteLine(BarrageTable(all));
            var fails = CheckLines(all);
            TestContext.WriteLine(fails.Count == 0 ? "線：全て満たす" : "線を満たさない：" + string.Join("／", fails));
        }

        [Test]
        public void SixPlansMeetTheTutorialLines()
        {
            using var progress = RunProgress.Begin("教程3の6作戦", 8 * Runs);
            var all = RunAll();
            Print(all);
            Assert.That(CheckLines(all), Is.Empty);
        }

        /// <summary>制限時間（25〜40分）と弾数（32〜56発）を振る（線を満たさないとき用。ふだんは回さない）。</summary>
        public static (double min, int rounds)[] Combos =
            new[] { 25.0, 30.0, 35.0, 40.0 }.SelectMany(m => new[] { 32, 40, 48, 56 }.Select(r => (m, r))).ToArray();

        [Test, Explicit("制限時間と弾数の掃き出し")]
        public void Sweep()
        {
            foreach (var (min, rounds) in Combos)
                {
                    TestContext.WriteLine($"## 制限時間 {min:0}分・弾 {rounds}発");
                    Print(RunAll(min, rounds));
                }
        }

    }
}
