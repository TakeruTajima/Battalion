using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    public static class TestUtil
    {
        public const string ParamsRelPath = "Assets/StreamingAssets/sim_params.json";

        /// <summary>sim_params.json の文字列を置き換えて読んだパラメータ（テストで一部の値だけ変えるため）。</summary>
        public static SimParams ParamsWith(params (string from, string to)[] replacements)
        {
            var s = File.ReadAllText(PathOf(ParamsRelPath));
            foreach (var (a, b) in replacements)
            {
                if (!s.Contains(a)) throw new ArgumentException("sim_params.json にない: " + a);
                s = s.Replace(a, b);
            }
            return SimParams.FromJson(s);
        }
        /// <summary>
        /// R1 の規則を止めたパラメータ：開けた所を動く被弾倍率と、動きながら撃つ倍率を 1.0、制圧のしきい値を無限大にする
        /// （R1 の前と同じ会戦になることの見張り。S1〜S6 を v3 と比べるのにも使う）。
        /// </summary>
        public static SimParams ParamsWithoutR1()
        {
            var p = SimParams.FromJson(File.ReadAllText(PathOf(ParamsRelPath)));
            p.Terrain.MovingOpenDamageMult = 1.0;
            p.Fire.MovingFireMult = 1.0;
            p.Suppression.ThresholdMovingPerMin = double.PositiveInfinity;
            p.Suppression.ThresholdStationaryPerMin = double.PositiveInfinity;
            return p;
        }

        static SimParams _noR1;

        /// <summary>
        /// R1 の規則を止めたパラメータ（<see cref="ParamsWithoutR1"/> を一度だけ読んだもの。書き換えないこと）。撃たれながら動く
        /// 場面で、制圧と関係のない動きを確かめるテストに使う。
        /// </summary>
        public static SimParams ParamsNoR1 => _noR1 ?? (_noR1 = ParamsWithoutR1());

        /// <summary>R1 の値を置き換えたパラメータ（掃き出し用。解けるまでの秒数と制圧下の撃つ力は、省けば sim_params.json の値）。</summary>
        public static SimParams ParamsWithR1(double movingOpenDamageMult, double thresholdMoving, double thresholdStationary, double movingFireMult,
            double? releaseSec = null, double? suppressedFireMult = null)
        {
            var p = SimParams.FromJson(File.ReadAllText(PathOf(ParamsRelPath)));
            p.Terrain.MovingOpenDamageMult = movingOpenDamageMult;
            p.Suppression.ThresholdMovingPerMin = thresholdMoving;
            p.Suppression.ThresholdStationaryPerMin = thresholdStationary;
            p.Fire.MovingFireMult = movingFireMult;
            if (releaseSec.HasValue) p.Suppression.ReleaseSec = releaseSec.Value;
            if (suppressedFireMult.HasValue) p.Suppression.FireMult = suppressedFireMult.Value;
            return p;
        }

        public const string InitialParamsRelPath = "docs/reference/sim_params.initial.json";
        public const string ReferenceResultsRelPath = "docs/reference/reference_results.json";

        static string _root;

        /// <summary>
        /// プロジェクトのルート（Assets と docs を持つディレクトリ）。Unity でも .NET 単体の実行でも見つかるように、
        /// カレントディレクトリとテストのディレクトリから親をたどる。
        /// </summary>
        public static string ProjectRoot
        {
            get
            {
                if (_root != null) return _root;
                foreach (var start in new[] { Directory.GetCurrentDirectory(), TestContext.CurrentContext.TestDirectory })
                {
                    var d = new DirectoryInfo(start);
                    while (d != null)
                    {
                        if (File.Exists(Path.Combine(d.FullName, ParamsRelPath)) && Directory.Exists(Path.Combine(d.FullName, "docs")))
                            return _root = d.FullName;
                        d = d.Parent;
                    }
                }
                throw new DirectoryNotFoundException("プロジェクトのルートが見つからない");
            }
        }

        public static string PathOf(string rel) => Path.Combine(ProjectRoot, rel);

        static SimParams _params;
        public static SimParams Params => _params ?? (_params = SimParams.LoadFile(PathOf(ParamsRelPath)));

        public static double Median(IList<double> xs) => Percentile(xs, 50);

        /// <summary>numpy.percentile の既定（linear 補間）と同じ。</summary>
        public static double Percentile(IList<double> xs, double q)
        {
            var s = xs.OrderBy(x => x).ToArray();
            double pos = (s.Length - 1) * q / 100.0;
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, s.Length - 1);
            return s[lo] + (s[hi] - s[lo]) * (pos - lo);
        }

        public sealed class BatchStats
        {
            public string Scenario;
            public int Runs;
            public double P05, Median, P95;
            public readonly Dictionary<string, double> LossMean = new Dictionary<string, double>();
            public readonly Dictionary<string, int> RoutedCount = new Dictionary<string, int>();
            public readonly Dictionary<string, int> SurrenderedCount = new Dictionary<string, int>();
            public int TimedOut;
        }

        /// <summary>シード first〜first+runs-1 で回して集計する。</summary>
        public static BatchStats RunBatch(string scenario, int runs = 300, int firstSeed = 1, double capMin = 60.0, SimParams p = null)
        {
            var st = new BatchStats { Scenario = scenario, Runs = runs };
            var times = new List<double>(runs);
            var loss = new Dictionary<string, List<double>>();
            for (int s = firstSeed; s < firstSeed + runs; s++)
            {
                var r = BattleRunner.RunToFirstRout(Scenarios.ByName(scenario), p ?? Params, (ulong)s, capMin);
                times.Add(r.EndTimeMin);
                if (!r.Routed) st.TimedOut++;
                foreach (var u in r.RoutedUnits)
                    st.RoutedCount[u] = (st.RoutedCount.TryGetValue(u, out var n) ? n : 0) + 1;
                foreach (var u in r.SurrenderedUnits)
                    st.SurrenderedCount[u] = (st.SurrenderedCount.TryGetValue(u, out var n) ? n : 0) + 1;
                foreach (var kv in r.LossRate)
                {
                    if (!loss.TryGetValue(kv.Key, out var l)) loss[kv.Key] = l = new List<double>();
                    l.Add(kv.Value);
                }
                RunProgress.Tick();
            }
            st.P05 = Percentile(times, 5);
            st.Median = Median(times);
            st.P95 = Percentile(times, 95);
            foreach (var kv in loss) st.LossMean[kv.Key] = kv.Value.Average();
            return st;
        }
    }
}
