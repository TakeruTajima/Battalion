using System;
using System.Security.Cryptography;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>同じシードを2回回して、ログの SHA256 が一致することを確かめる。</summary>
    public class DeterminismTests
    {
        /// <summary>壊走後の逃走・再集結も含めて流すため、最初の壊走で止めずに60分回す。</summary>
        static string RunAndHash(string scenario, ulong seed, out int lines)
        {
            var sink = new StringEventSink();
            var sim = Scenarios.ByName(scenario).Build(TestUtil.Params, seed, sink);
            while (sim.Time < 60 * 60) sim.Step();
            lines = sink.Count;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(sink.ToUtf8())).Replace("-", "").ToLowerInvariant();
        }

        [TestCase("S1_1v1_front", 1UL)]
        [TestCase("S3_2v1_front_flank", 7UL)]
        [TestCase("S4_2v1_front_rear", 42UL)]
        [TestCase("CC_1v1_front_50m", 300UL)]
        [TestCase("S6_4v1_surrounded", 5UL)]
        public void SameSeedSameLogHash(string scenario, ulong seed)
        {
            string h1 = RunAndHash(scenario, seed, out int n1);
            string h2 = RunAndHash(scenario, seed, out int n2);
            TestContext.WriteLine($"{scenario} seed={seed}: {n1} 行, SHA256={h1}");
            Assert.That(n1, Is.GreaterThan(0));
            Assert.That(n2, Is.EqualTo(n1));
            Assert.That(h2, Is.EqualTo(h1));
        }

        /// <summary>
        /// S1〜S6 を平地の地図に載せても、P0.2（地図なし）とログが1バイトも変わらないこと。
        /// ハッシュは P0.2（proto-p0.2-sim）のテスト出力。60分まで流すので、壊走後の逃走・再集結も含む。
        /// </summary>
        [TestCase("S1_1v1_front", 1UL, 729, "033ca5315b4486887dbc3cc6ce79bf7e11cc4c73af037ad43d32131a68f0f864")]
        [TestCase("S3_2v1_front_flank", 7UL, 1089, "d43ba4adddca308c5f0b7bd9a6b9264cde73d8435943d6c226d895485379eba0")]
        [TestCase("S4_2v1_front_rear", 42UL, 1089, "79e8ab8f03f191e58bb28f7413b57327aa9beb3a4000e4a5dc999eb7be8179bc")]
        [TestCase("CC_1v1_front_50m", 300UL, 730, "28c69ead1ef4f2c04effc8af599dcee60ce82fc7bc53b74e7460e6fe7913f9f4")]
        [TestCase("S6_4v1_surrounded", 5UL, 1456, "524946e32798113c9e00a6d2183f12cacd2a73ff25403a7e84e59bfa0c840367")]
        public void FlatMapLogMatchesP02(string scenario, ulong seed, int expectedLines, string expectedHash)
        {
            string h = RunAndHash(scenario, seed, out int n);
            Assert.That(n, Is.EqualTo(expectedLines));
            Assert.That(h, Is.EqualTo(expectedHash));
        }

        [Test]
        public void DifferentSeedsDiffer()
        {
            Assert.That(RunAndHash("S1_1v1_front", 1, out _), Is.Not.EqualTo(RunAndHash("S1_1v1_front", 2, out _)));
        }
    }
}
