using System;
using System.Diagnostics;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 遅いテストの進み具合（何回目か・％・経過・残りの見込み）を、テストの途中で出力に出す（TestContext.Progress）。
    /// テストの初めに <see cref="Begin"/> で全体の回数を決め、1回回すごとに <see cref="Tick"/> を呼ぶ。
    /// 出すのは、15秒ごとと、最後の1回。残りの見込みは、ここまでの1回あたりの平均の時間から計算する。
    /// </summary>
    public static class RunProgress
    {
        const double PrintEverySec = 15;

        static string _name;
        static int _total, _done;
        static Stopwatch _sw;
        static double _lastPrint;

        /// <summary>進み具合を数え始める。using で囲むと、終わりに数えるのをやめる。</summary>
        public static IDisposable Begin(string name, int total)
        {
            _name = name;
            _total = total;
            _done = 0;
            _sw = Stopwatch.StartNew();
            _lastPrint = 0;
            Write($"[{_name}] 開始（{_total}回）");
            return new End();
        }

        /// <summary>1回回し終えた。数えていなければ何もしない。</summary>
        public static void Tick()
        {
            if (_sw == null) return;
            _done++;
            double sec = _sw.Elapsed.TotalSeconds;
            if (_done < _total && sec - _lastPrint < PrintEverySec) return;
            _lastPrint = sec;
            double pct = _total > 0 ? 100.0 * _done / _total : 0;
            string left = _done >= _total ? "終わり" : "残り約 " + Duration(sec / _done * (_total - _done));
            Write($"[{_name}] {_done}/{_total}（{pct:0.0}%）　経過 {Duration(sec)}　{left}");
        }

        static string Duration(double sec)
        {
            int s = (int)Math.Round(sec);
            if (s >= 3600) return $"{s / 3600}時間{s % 3600 / 60:00}分";
            if (s >= 60) return $"{s / 60}分{s % 60:00}秒";
            return $"{s}秒";
        }

        static void Write(string line) => TestContext.Progress.WriteLine(line);

        sealed class End : IDisposable
        {
            public void Dispose() => _sw = null;
        }
    }
}
