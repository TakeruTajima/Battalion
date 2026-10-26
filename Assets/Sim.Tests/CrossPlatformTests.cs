using System;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// Unity（Mono）と .NET で結果が変わらないことを見張る（V1 タスク0）。どれも Unity と .NET の両方で同じ値になるはず。
    /// Mono の <c>double.Parse</c> は正しく丸めないことがあり、<c>ToString("R")</c> は往復できる値でも 17桁で書くことがあるため、
    /// JSON の数の読み書きは <see cref="JsonNumber"/> で行う。
    /// </summary>
    public class CrossPlatformTests
    {
        static string Bits(double v) => BitConverter.DoubleToInt64Bits(v).ToString("X16");

        static string Format(double v)
        {
            var sb = new StringBuilder();
            JsonNumber.Append(sb, v);
            return sb.ToString();
        }

        /// <summary>ログの数の書き方：.NET の ToString("R") と同じ文字（Mono の "R" が 17桁で書いた値を含む）。</summary>
        [TestCase(1712.764048901362, "1712.764049")]
        [TestCase(1284.0641658391014, "1284.064166")]
        [TestCase(1712.764049, "1712.764049")]
        [TestCase(-179.298543, "-179.298543")]
        [TestCase(100.0, "100")]
        [TestCase(0.5, "0.5")]
        [TestCase(-0.0, "0")]
        [TestCase(0.0000004, "0")]
        [TestCase(0.0001, "0.0001")]
        [TestCase(0.000012, "1.2E-05")]
        [TestCase(0.000005, "5E-06")]
        [TestCase(0.00005, "5E-05")]
        [TestCase(-0.000099, "-9.9E-05")]
        [TestCase(0.0000125, "1.3E-05")]
        public void NumbersAreWrittenTheSameEverywhere(double v, string expected)
        {
            Assert.That(Format(v), Is.EqualTo(expected));
        }

        /// <summary>.NET では、新しい書き方が今までの書き方（Math.Round(v, 6) と "R"）と同じ文字になる（ログのハッシュが変わらない）。</summary>
        [Test]
        public void NewFormattingMatchesTheOldDotNetFormatting()
        {
            if (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith("Mono", StringComparison.Ordinal))
                Assert.Ignore("Mono の \"R\" は .NET と違うので、.NET だけで比べる");
            var rng = new Pcg32(7);
            for (int k = 0; k < 200000; k++)
            {
                double v = (rng.NextDouble() - 0.3) * Math.Pow(10, rng.NextDouble() * 12 - 4);
                double r = Math.Round(v, 6, MidpointRounding.AwayFromZero);
                if (Math.Abs(r) >= 1e9) continue;
                if (r == 0) r = 0;
                Assert.That(Format(v), Is.EqualTo(r.ToString("R", CultureInfo.InvariantCulture)), $"v={v:R}");
            }
        }

        /// <summary>JSON の数の読み方：正しく丸める（Mono の double.Parse は 1284.064166 を 1ビット大きく読む）。</summary>
        [TestCase("1284.064166", "40941041B4BB5E0F")]
        [TestCase("1712.764049", "409AC30E62DC6E2B")]
        [TestCase("0.1", "3FB999999999999A")]
        [TestCase("0.003", "3F689374BC6A7EFA")]
        [TestCase("-2.5e3", "C0A3880000000000")]
        [TestCase("1500", "4097700000000000")]
        [TestCase("0.000", "0000000000000000")]
        public void NumbersAreReadTheSameEverywhere(string tok, string bits)
        {
            Assert.That(JsonNumber.TryParseExact(tok, out double d), Is.True);
            Assert.That(Bits(d), Is.EqualTo(bits));
        }

        /// <summary>設定・地図・参照の結果の数は、全て正しく丸める読み方で読める（実行環境の double.Parse に頼らない）。</summary>
        [TestCase(TestUtil.ParamsRelPath)]
        [TestCase(TestUtil.InitialParamsRelPath)]
        [TestCase("maps/scenario_a1.json")]
        [TestCase("maps/scenario_a2.json")]
        [TestCase(TestUtil.ReferenceResultsRelPath)]
        public void ProjectJsonNumbersNeverFallBackToDoubleParse(string rel)
        {
            int before = JsonNumber.FallbackCount;
            JsonValue.Parse(File.ReadAllText(TestUtil.PathOf(rel)));
            Assert.That(JsonNumber.FallbackCount - before, Is.EqualTo(0));
        }

        /// <summary>
        /// シナリオA の作戦（ステージ2 の段階つき、シード1、90分。中隊の伝令の行を含むログ全体）のハッシュが、.NET で取った値と同じ。
        /// Unity でも通れば、会戦の計算とログの書き方が両方で同じ。E1（赤の守り方、占領の乱れ）を入れた値で取り直した
        /// （R1 の前は 3929行・67e779d0…、R1 では 3924行・6637bf00…、R2 では 3965行・986fa11c…、R2.1 では 3939行・ac0c6f8a…）。
        /// </summary>
        [Test]
        public void ScenarioALogIsTheSameOnUnityAndDotNet()
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a2.json"));
            string log = ScenarioLogHashTests.RunAndLog(sc, new ScenarioAReportTests.PlannedOrder[0], 1, out int n,
                s => ScenarioAReportTests.StagedPinAndFlankStart(s, 2), ScenarioAReportTests.StagedPinAndFlankTick);
            Assert.That(n, Is.EqualTo(3966));
            Assert.That(ScenarioLogHashTests.Hash(log), Is.EqualTo("d0fc0389636a635a5ff12a62a95cbe483bbf90960fdeb2111df9084d53784239"));
        }
    }
}
