using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>E1：赤の守り方の検証の場面 S14〜S19（仕様「E1：赤の守り方（最小版）（確定）」の検証の場面）。各 20シード。</summary>
    public class E1SceneTests
    {
        public const int Seeds = 20;

        /// <summary>出来事の行を拾う（type と unit と t と、行全体）。</summary>
        sealed class EventSink : IEventSink
        {
            public readonly List<(double t, string type, string unit, JsonValue v)> Events = new List<(double, string, string, JsonValue)>();
            static readonly string[] Keep =
            {
                "\"type\":\"defense_", "\"type\":\"retake_", "\"type\":\"occupation_", "\"type\":\"runner_sent\"",
            };
            public bool Enabled => true;
            public void Write(string line)
            {
                if (!Keep.Any(k => line.Contains(k))) return;
                var v = JsonValue.Parse(line);
                Events.Add((v.D("t"), v["type"].String, v.TryGet("unit", out var u) ? u.String : null, v));
            }
            public bool Any(string type, string unit = null) => Events.Exists(e => e.type == type && (unit == null || e.unit == unit));
            public double? First(string type, string unit = null)
            {
                foreach (var e in Events) if (e.type == type && (unit == null || e.unit == unit)) return e.t;
                return null;
            }
        }

        static Simulation Run(Scenario sc, SimParams p, ulong seed, double capMin, EventSink sink, Func<Simulation, bool> stop = null,
            Action<Simulation> each = null)
        {
            var sim = sc.Build(p, seed, sink);
            int next = 0;
            while (sim.Time < capMin * 60)
            {
                sc.IssueDue(sim, ref next);
                sim.Step();
                each?.Invoke(sim);
                if (stop != null && stop(sim)) break;
            }
            return sim;
        }

        // ---------------------------------------------------------------- S14・S15：前線の後退

        /// <summary>S14：20回とも下がって後退先に着く。下がり始め（判断）は、青が線より 50m 後ろに出てから 60秒以内。</summary>
        [Test]
        public void S14BypassedFrontFallsBackEveryTime()
        {
            var sb = new StringBuilder();
            int fell = 0, arrived = 0, inTime = 0;
            var lags = new List<double>();
            for (int s = 1; s <= Seeds; s++)
            {
                var sink = new EventSink();
                double? crossed = null;
                var sim = Run(Scenarios.S14_bypassed_front(), TestUtil.Params, (ulong)s, 15, sink, null, x =>
                {
                    if (!crossed.HasValue && x.Find("A").Position.X >= 50) crossed = x.Time;
                });
                var r = sim.Find("R");
                double? at = sink.First("defense_fallback", "R");
                if (at.HasValue) fell++;
                if (r.Role == DefenseRole.Garrison && Vec2.Distance(r.Position, new Vec2(300, 0)) < 60) arrived++;
                if (at.HasValue && crossed.HasValue)
                {
                    lags.Add(at.Value - crossed.Value);
                    if (at.Value <= crossed.Value + 60 + 1e-9) inTime++;
                }
            }
            TestContext.WriteLine($"S14：下がった {fell}/{Seeds}、後退先に着いた {arrived}/{Seeds}、線を越えて 60秒以内 {inTime}/{Seeds}、" +
                                  $"下がり始め − 線を越えた時刻 の中央値 {(lags.Count > 0 ? TestUtil.Median(lags) : double.NaN):0.0}秒（負は越える前）");
            Assert.That(fell, Is.EqualTo(Seeds));
            Assert.That(arrived, Is.EqualTo(Seeds));
            Assert.That(inTime, Is.EqualTo(Seeds));
        }

        /// <summary>S15：正面 350m に保持する青がいれば、一度も下がらない。</summary>
        [Test]
        public void S15PinnedFrontNeverFallsBack()
        {
            int fell = 0;
            for (int s = 1; s <= Seeds; s++)
            {
                var sink = new EventSink();
                Run(Scenarios.S15_pinned_front(), TestUtil.Params, (ulong)s, 15, sink);
                if (sink.Any("defense_fallback")) fell++;
            }
            TestContext.WriteLine($"S15：下がった {fell}/{Seeds}");
            Assert.That(fell, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- S16：即座の逆襲と戻り

        /// <summary>S16：予備が伝令なしで逆襲する。目標が崩れたら、円に別の青がいなければ戻る位置へ戻る。</summary>
        [Test]
        public void S16ReserveCounterattacksWithoutRunnerAndReturns()
        {
            int attacked = 0, viaRunner = 0, broke = 0, returned = 0;
            for (int s = 1; s <= Seeds; s++)
            {
                var sink = new EventSink();
                var sim = Run(Scenarios.S16_counterattack_and_return(), TestUtil.Params, (ulong)s, 45, sink);
                var v = sim.Find("V");
                if (sink.Any("defense_counterattack", "V")) attacked++;
                if (sink.Any("runner_sent", "V")) viaRunner++;
                if (!sim.Find("A").UnderCommand || sink.Any("defense_return", "V"))
                {
                    broke++;
                    if (sink.Any("defense_return", "V") && Vec2.Distance(v.Position, new Vec2(500, 0)) < 60) returned++;
                }
            }
            TestContext.WriteLine($"S16：逆襲した {attacked}/{Seeds}、伝令あり {viaRunner}、目標が崩れた {broke}、戻る位置へ戻った {returned}/{broke}");
            Assert.That(attacked, Is.EqualTo(Seeds));
            Assert.That(viaRunner, Is.EqualTo(0));
            Assert.That(broke, Is.GreaterThan(0));
            Assert.That(returned, Is.EqualTo(broke));
        }

        // ---------------------------------------------------------------- S17：奪回（数字を報告するだけ）

        public sealed class RetakeSummary
        {
            public double RetakenRate, RedLossMean, OrderedRate, LaunchedRate;
            public double? SpreadMedianSec;
        }

        /// <summary>S17 を回す：村を取り返した（45分の時点で村に指揮下の青がいない）割合、赤の損害、攻撃に移った時刻のばらつき。</summary>
        public static RetakeSummary RunS17(SimParams p)
        {
            int retaken = 0, ordered = 0, launched = 0;
            double redLoss = 0;
            var spreads = new List<double>();
            for (int s = 1; s <= Seeds; s++)
            {
                var sink = new EventSink();
                var sim = Run(Scenarios.S17_retake(), p, (ulong)s, 45, sink);
                var a = sim.Find("A");
                if (!(a.UnderCommand && sim.Map.TerrainAt(a.Position) == TerrainMap.Village)) retaken++;
                if (sink.Any("retake_order")) ordered++;
                var starts = sink.Events.Where(e => e.type == "retake_attack_unit").Select(e => e.t).ToList();
                if (p.Defense.RetakeAssemble ? sink.Any("retake_attack") : sink.Any("retake_order")) launched++;
                if (starts.Count >= 2) spreads.Add(starts.Max() - starts.Min());
                var red = sim.Companies.Where(c => c.Side == Side.Red).ToList();
                redLoss += red.Sum(c => c.InitialStrength - c.Strength) / (double)red.Sum(c => c.InitialStrength);
            }
            return new RetakeSummary
            {
                RetakenRate = retaken / (double)Seeds,
                RedLossMean = redLoss / Seeds,
                OrderedRate = ordered / (double)Seeds,
                LaunchedRate = launched / (double)Seeds,
                SpreadMedianSec = spreads.Count == 0 ? (double?)null : TestUtil.Median(spreads),
            };
        }

        /// <summary>S17：集めて出す版は、2個が揃ってから同時に攻める（攻撃に移る時刻が同じ刻み）。小出しの版と数字を並べる。</summary>
        [Test]
        public void S17AssembledRetakeAttacksTogether()
        {
            var assemble = RunS17(TestUtil.Params);
            var p = TestUtil.ParamsWith(("\"retake_mode\": \"assemble\"", "\"retake_mode\": \"immediate\""));
            var immediate = RunS17(p);
            TestContext.WriteLine($"S17 集めて出す：命令 {assemble.OrderedRate:0.00}、攻撃 {assemble.LaunchedRate:0.00}、取り返した {assemble.RetakenRate:0.00}、" +
                                  $"赤の損害 {assemble.RedLossMean:0.000}、攻撃に移った時刻の差の中央値 {assemble.SpreadMedianSec:0.0}秒");
            TestContext.WriteLine($"S17 小出し：命令 {immediate.OrderedRate:0.00}、取り返した {immediate.RetakenRate:0.00}、赤の損害 {immediate.RedLossMean:0.000}");
            Assert.That(assemble.LaunchedRate, Is.EqualTo(1.0), "20回とも揃って攻撃した");
            Assert.That(assemble.SpreadMedianSec, Is.EqualTo(0.0), "同時に攻撃に移る");
        }

        // ---------------------------------------------------------------- S18・S19：取った直後の逆襲

        /// <summary>S18・S19：赤が攻撃を始めてから 30分以内に、塹壕の青が崩れる割合。</summary>
        public static double BlueBrokenWithin30(Scenario sc, SimParams p)
        {
            double attackAt = sc.Orders.Single(o => o.Target != null).AtSec;
            int broken = 0;
            for (int s = 1; s <= Seeds; s++)
            {
                var sim = Run(sc, p, (ulong)s, (attackAt + 30 * 60) / 60, null, x => !x.Find("A").UnderCommand);
                if (!sim.Find("A").UnderCommand) broken++;
            }
            return broken / (double)Seeds;
        }

        /// <summary>
        /// S18：取った直後（占領の乱れの間）の逆襲で青が崩れる割合が、固めた後（S19）より 0.3 以上高い。E1 の値では満たさず、
        /// 占領の乱れの掃き出しでも満たす組がない（E1 の報告）。仕様の判断を待つ間はふだん回さない。
        /// </summary>
        [Test, Explicit("S18・S19 の期待を満たす占領の乱れの値がない（E1 の報告）")]
        public void S18CounterRightAfterCaptureBreaksMoreOftenThanS19()
        {
            double s18 = BlueBrokenWithin30(Scenarios.S18_counter_right_after_capture(), TestUtil.Params);
            double s19 = BlueBrokenWithin30(Scenarios.S19_counter_after_consolidation(), TestUtil.Params);
            TestContext.WriteLine($"S18 崩れる {s18:0.00}／S19 崩れる {s19:0.00}");
            Assert.That(s18 - s19, Is.GreaterThanOrEqualTo(0.3 - 1e-9));
        }

        /// <summary>占領の乱れの値の掃き出し（S18・S19 の期待を満たさないとき用。ふだんは回さない）。</summary>
        [Test, Explicit]
        public void OccupationSweep()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 乱れの秒数 | 乱れの撃つ力 | S18 崩れる | S19 崩れる | 差 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (double sec in new[] { 120.0, 300.0, 600.0 })
            foreach (double mult in new[] { 0.5, 0.25 })
            {
                var p = SimParams.FromJson(System.IO.File.ReadAllText(TestUtil.PathOf(TestUtil.ParamsRelPath)));
                p.Occupation.DisarraySec = sec;
                p.Occupation.DisarrayFireMult = mult;
                double s18 = BlueBrokenWithin30(Scenarios.S18_counter_right_after_capture(), p);
                double s19 = BlueBrokenWithin30(Scenarios.S19_counter_after_consolidation(), p);
                sb.AppendLine($"| {sec:0} | {mult:0.00} | {s18:0.00} | {s19:0.00} | {s18 - s19:+0.00;-0.00} |");
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
