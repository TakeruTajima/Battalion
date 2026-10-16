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
        public void SameSeedSameLogHash(string scenario, ulong seed)
        {
            string h1 = RunAndHash(scenario, seed, out int n1);
            string h2 = RunAndHash(scenario, seed, out int n2);
            TestContext.WriteLine($"{scenario} seed={seed}: {n1} 行, SHA256={h1}");
            Assert.That(n1, Is.GreaterThan(0));
            Assert.That(n2, Is.EqualTo(n1));
            Assert.That(h2, Is.EqualTo(h1));
        }

        [Test]
        public void DifferentSeedsDiffer()
        {
            Assert.That(RunAndHash("S1_1v1_front", 1, out _), Is.Not.EqualTo(RunAndHash("S1_1v1_front", 2, out _)));
        }
    }
}
