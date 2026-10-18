using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>伝令の状態（P2a。P2b で取り消しを足した）。</summary>
    public enum RunnerState { Preparing, Outbound, Returning, Done, Lost, Cancelled }

    /// <summary>命令を出した結果（P2a）。</summary>
    public enum SendResult
    {
        /// <summary>伝令を使わずに次の刻みで受け付ける（会戦開始前、または本部のない陣営）。</summary>
        Immediate,
        /// <summary>伝令が本部を出た（準備から）。</summary>
        Sent,
        /// <summary>判断枠がない（伝令が出払っている）。命令は出ていない。</summary>
        NoSlot,
        /// <summary>命令書に発令できない段がある（P2b）。命令は出ていない。</summary>
        Invalid,
    }

    /// <summary>伝令1人（1つの命令書を運ぶ）。表示のために読める。</summary>
    public sealed class Runner
    {
        public int Id;
        public Side Side;
        /// <summary>運んでいる命令書（P2b）。準備中なら直せる。</summary>
        public OrderSheet Sheet;
        /// <summary>命令書の第1段の命令（P2a からの読み口）。</summary>
        public Order Order => Sheet.First;
        public int CompanyId;
        public RunnerState State;
        public Vec2 Position;
        /// <summary>発令した時刻（秒）。</summary>
        public double SentTime;
        /// <summary>出した順（同じ中隊への命令の新旧を決める）。</summary>
        public long Seq;
        /// <summary>判断枠を数えるか（台本の伝令は数えない）。</summary>
        public bool CountsSlot;
        /// <summary>判断枠を空けたか（受領、受領報告なしの強制開放、取り消し）。</summary>
        public bool SlotReleased;
        /// <summary>受領報告なしとして判断枠を空ける時刻（発令＋往復予定の 1.5 倍。往復予定は経路の時間で見積もる）。</summary>
        public double OverdueAt;
        /// <summary>命令書を渡したか。</summary>
        public bool Delivered;
        /// <summary>今たどっている経路の点（最後の点の後は、向かう先へまっすぐ）と、次に向かう点の添字、次に引き直す時刻。</summary>
        public List<Vec2> Path;
        public int PathIndex;
        public double NextReplan;
    }

    // P2a：伝令（仕様「P2a：伝令（確定）」）。P2b：命令書を運び、経路探索で進む（仕様「P2b」の伝令の経路）。
    public sealed partial class Simulation
    {
        // 伝令の喪失の乱数は戦闘の乱数とは別の系列から引く（伝令の数で戦闘の乱数の並びがずれないように）
        const ulong RunnerStream = 0x5eedbeefUL;

        readonly Vec2?[] _hq = new Vec2?[2];
        readonly List<Runner> _runners = new List<Runner>();
        Pcg32 _runnerRng;
        long _orderSeq;

        SimParams.RunnersParams R => _p.Runners;

        /// <summary>本部の位置を決める（シナリオの定義から）。本部のない陣営の命令は伝令を使わない。</summary>
        public void SetHeadquarters(Side side, Vec2 position) => _hq[(int)side] = position;

        public Vec2? HeadquartersOf(Side side) => _hq[(int)side];

        /// <summary>伝令（倒れた・終わった・取り消した伝令も含む。表示用に読むだけ）。</summary>
        public IReadOnlyList<Runner> Runners => _runners;

        /// <summary>会戦開始前（時刻 0 のまま、まだ一度も刻みを進めていない）。この間の命令は伝令を使わずに届く。</summary>
        public bool BeforeStart => Tick == 0;

        /// <summary>判断枠のうち使っている数（受領も強制開放も取り消しもされていない、数える伝令）。</summary>
        public int RunnerSlotsUsed(Side side)
        {
            int n = 0;
            foreach (var r in _runners)
                if (r.Side == side && r.CountsSlot && !r.SlotReleased) n++;
            return n;
        }

        public int MaxRunners => R.MaxOut;

        /// <summary>命令書の段の最大数（P2b）。</summary>
        public int MaxStages => _p.Orders.MaxStages;

        /// <summary>1つの命令を出す（1段の命令書として）。</summary>
        public SendResult SendOrder(Order order) => SendSheet(OrderSheet.Single(order), true);

        /// <summary>台本の命令（判断枠を数えない）。</summary>
        public SendResult SendScriptOrder(Order order) => SendSheet(OrderSheet.Single(order), false);

        /// <summary>
        /// 命令書を出す（P2b）。会戦開始前と本部のない陣営は、伝令を使わずに次の刻みで受け付ける。
        /// それ以外は本部から伝令を出す（判断枠がなければ出さない）。発令できない段があれば出さない。
        /// </summary>
        public SendResult SendSheet(OrderSheet sheet) => SendSheet(sheet, true);

        SendResult SendSheet(OrderSheet sheet, bool countsSlot)
        {
            var c = _companies[sheet.CompanyId];
            if (!OrderSheet.IsValid(sheet.Stages, MaxStages, FlareUsedBy(c.Side))) return SendResult.Invalid;
            var hq = _hq[(int)c.Side];
            if (BeforeStart || !hq.HasValue)
            {
                IssueSheet(sheet);
                return SendResult.Immediate;
            }
            if (countsSlot && RunnerSlotsUsed(c.Side) >= R.MaxOut) return SendResult.NoSlot;

            double oneWay = WithoutCaching(() => RunnerPathTime(hq.Value, c.Position, RunnerPath(c.Side, hq.Value, c.Position)));
            var r = new Runner
            {
                Id = _runners.Count,
                Side = c.Side,
                Sheet = sheet,
                CompanyId = c.Id,
                State = RunnerState.Preparing,
                Position = hq.Value,
                SentTime = Time,
                Seq = ++_orderSeq,
                CountsSlot = countsSlot,
                OverdueAt = Time + R.OverdueMult * (R.PrepSec + 2 * oneWay),
            };
            _runners.Add(r);
            if (_log.Enabled)
                _log.Write(Ev(Time, "runner_sent", c)
                    .Add("runner", r.Id).Add("side", SideName(r.Side))
                    .Add("order", Order.Name(sheet.First.Type))
                    .Add("stages", sheet.Stages.Count)
                    .Add("eta", Math.Round(R.PrepSec + oneWay, 1))
                    .ToString());
            return SendResult.Sent;
        }

        /// <summary>準備中（本部を出る前）の伝令の命令書を差し替える（P2b）。準備の残り時間は数え直さない。宛先は変えられない。</summary>
        public bool ReviseSheet(int runnerId, OrderSheet sheet)
        {
            if (runnerId < 0 || runnerId >= _runners.Count) return false;
            var r = _runners[runnerId];
            if (r.State != RunnerState.Preparing || sheet.CompanyId != r.CompanyId) return false;
            if (!OrderSheet.IsValid(sheet.Stages, MaxStages, FlareUsedBy(r.Side))) return false;
            r.Sheet = sheet;
            if (_log.Enabled)
                _log.Write(Ev(Time, "runner_revised", _companies[r.CompanyId]).Add("runner", r.Id).Add("side", SideName(r.Side))
                    .Add("stages", sheet.Stages.Count).ToString());
            return true;
        }

        /// <summary>準備中（本部を出る前）の伝令を取り消す（P2b）。伝令は出ず、判断枠が空く。</summary>
        public bool CancelRunner(int runnerId)
        {
            if (runnerId < 0 || runnerId >= _runners.Count) return false;
            var r = _runners[runnerId];
            if (r.State != RunnerState.Preparing) return false;
            r.State = RunnerState.Cancelled;
            r.SlotReleased = true;
            if (_log.Enabled)
                _log.Write(Ev(Time, "runner_cancelled", _companies[r.CompanyId]).Add("runner", r.Id).Add("side", SideName(r.Side)).ToString());
            return true;
        }

        static string SideName(Side s) => s == Side.Blue ? "blue" : "red";

        /// <summary>
        /// 中隊へ向かっている（または本部が向かっていると思っている）最新の命令書と、到着予定（秒。準備の残り＋経路の時間）。
        /// 倒れた伝令も、受領報告なしで判断枠を空けるまでは「未着」として返す（本部は倒れたことを知らない）。なければ null。
        /// </summary>
        public Runner InFlightTo(int companyId, out double etaSec)
        {
            Runner best = null;
            etaSec = 0;
            foreach (var r in _runners)
            {
                if (r.CompanyId != companyId || r.Delivered) continue;
                bool live = r.State == RunnerState.Preparing || r.State == RunnerState.Outbound;
                bool presumed = r.State == RunnerState.Lost && !r.SlotReleased && Time < r.OverdueAt;
                if (!live && !presumed) continue;
                if (best == null || r.Seq > best.Seq) best = r;
            }
            if (best != null) etaSec = RunnerEta(best);
            return best;
        }

        /// <summary>伝令が中隊に着くまでの見積もり（秒）：準備の残り＋今たどっている経路の残り＋最後の点から中隊の今の位置まで。</summary>
        public double RunnerEta(Runner r)
        {
            var c = _companies[r.CompanyId];
            double prep = Math.Max(0, r.SentTime + R.PrepSec - Time);
            if (r.State == RunnerState.Preparing || r.Path == null)
            {
                var from = r.Position;
                return prep + WithoutCaching(() => RunnerPathTime(from, c.Position, RunnerPath(r.Side, from, c.Position)));
            }
            return prep + RunnerPathTime(r.Position, c.Position, r.Path, r.PathIndex);
        }

        /// <summary>今この中隊へ命令書を出したら、伝令が着くまでの見積もり（秒。準備＋本部からの経路の時間）。本部がなければ 0。</summary>
        public double EstimateRunnerEta(int companyId)
        {
            var c = _companies[companyId];
            var hq = _hq[(int)c.Side];
            if (!hq.HasValue) return 0;
            return R.PrepSec + WithoutCaching(() => RunnerPathTime(hq.Value, c.Position, RunnerPath(c.Side, hq.Value, c.Position)));
        }

        /// <summary>
        /// 刻みの外（命令を出すとき、表示の見積もり）で露出の地図などの使い回しを作らずに計算する（作る時機で会戦の結果が変わらないように）。
        /// </summary>
        T WithoutCaching<T>(Func<T> f)
        {
            bool was = _previewing;
            _previewing = true;
            try { return f(); }
            finally { _previewing = was; }
        }

        /// <summary>
        /// 伝令を動かす（刻みの始め、命令の適用の前）。準備→往路（経路をたどって中隊の今の位置を追いかける）→受け渡し→帰路→受領。
        /// 射界のマスにいる間は倒れうる。受領報告なしの強制開放と、開始待ちの段の開始もここで扱う。
        /// </summary>
        void UpdateRunners(double t, double tickStart)
        {
            if (_runnerRng == null) _runnerRng = new Pcg32(_seed, RunnerStream);
            double dt = _p.TickSec;
            foreach (var r in _runners)
            {
                if (r.State == RunnerState.Done || r.State == RunnerState.Lost || r.State == RunnerState.Cancelled) continue;
                double budget = dt;
                if (r.State == RunnerState.Preparing)
                {
                    double ready = r.SentTime + R.PrepSec;
                    if (t < ready - 1e-9) continue;
                    r.State = RunnerState.Outbound;
                    r.NextReplan = double.NegativeInfinity;
                    budget = Math.Min(dt, t - ready);
                }
                if (budget <= 0) continue;
                double moved = budget;
                if (r.State == RunnerState.Outbound)
                {
                    var c = _companies[r.CompanyId];
                    if (!c.OnBoard)
                    {
                        // 盤上にいない（降伏・離脱）：渡さずに本部へ戻る
                        r.State = RunnerState.Returning;
                        r.NextReplan = double.NegativeInfinity;
                        if (_log.Enabled) _log.Write(Ev(t, "runner_undeliverable", c).Add("runner", r.Id).Add("side", SideName(r.Side)).ToString());
                    }
                    else if (MoveRunner(r, c.Position, budget, tickStart))
                    {
                        Deliver(r, c, t);
                        r.State = RunnerState.Returning;
                        r.NextReplan = double.NegativeInfinity;
                    }
                }
                else if (r.State == RunnerState.Returning)
                {
                    var hq = _hq[(int)r.Side].Value;
                    if (MoveRunner(r, hq, budget, tickStart))
                    {
                        r.State = RunnerState.Done;
                        var c = _companies[r.CompanyId];
                        bool late = r.SlotReleased;
                        r.SlotReleased = true;
                        if (_log.Enabled) _log.Write(Ev(t, "runner_returned", c).Add("runner", r.Id).Add("side", SideName(r.Side)).Add("late", late).ToString());
                        continue;
                    }
                }
                // 射界を通る間、1秒あたり loss_per_sec で倒れる（本部の上・地図の外は数えない）
                if (r.State == RunnerState.Outbound || r.State == RunnerState.Returning)
                {
                    var hqPos = _hq[(int)r.Side].Value;
                    if (_map.Contains(r.Position) && Vec2.Distance(r.Position, hqPos) > 1e-9
                        && ExposureFor(r.Side)[_map.IndexOf(r.Position)])
                    {
                        double p = 1 - Math.Pow(1 - R.LossPerSec, moved);
                        if (_runnerRng.NextDouble() < p)
                        {
                            r.State = RunnerState.Lost;
                            if (_log.Enabled) _log.Write(Ev(t, "runner_lost", _companies[r.CompanyId]).Add("runner", r.Id).Add("side", SideName(r.Side)).Add("delivered", r.Delivered).ToString());
                        }
                    }
                }
            }
            // 受領報告なし：発令から往復予定の 1.5 倍が過ぎても受領が来なければ、判断枠を強制的に空ける
            foreach (var r in _runners)
            {
                if (!r.CountsSlot || r.SlotReleased || t < r.OverdueAt) continue;
                r.SlotReleased = true;
                if (_log.Enabled) _log.Write(Ev(t, "receipt_overdue", _companies[r.CompanyId]).Add("runner", r.Id).Add("side", SideName(r.Side)).ToString());
            }
            // 開始待ちの段：届いてから（次の段へ移ってから）練度の遅れが経ったら始める（この刻みの命令の適用で）
            foreach (var c in _companies)
            {
                if (c.StartWaitOrder == null || t < c.StartWaitAt - 1e-9) continue;
                _pending.Add(c.StartWaitOrder);
                c.StageStarted = true;
                c.StageCompleted = false;
                if (_log.Enabled)
                    _log.Write(Ev(t, "order_start", c).Add("order", Order.Name(c.StartWaitOrder.Type)).Add("stage", c.StageIndex + 1).ToString());
                c.StartWaitOrder = null;
            }
        }

        // ---------------------------------------------------------------- 伝令の経路（P2b）

        /// <summary>
        /// 伝令の経路：A*（今の <see cref="PathFinder"/>）。伝令の陣営から見た敵の射界に脅威の重み 3.0 を掛けて避け、
        /// 敵の駒の矩形に重なるマスは通らない（敵の中隊の中を通り抜けない）。川は移動倍率 0 なので橋と浅瀬だけを渡る。
        /// 届かなければ届くところまで。経路が引けなければ null（まっすぐ進む）。
        /// </summary>
        List<Vec2> RunnerPath(Side side, Vec2 from, Vec2 to)
        {
            if (!_map.Contains(from) || !_map.Contains(to)) return null;
            _map.CellOf(from, out int si, out int sj);
            _map.CellOf(to, out int gi, out int gj);
            var cells = PathFinder.Find(_map, new PathFinder.Cell(si, sj), new PathFinder.Cell(gi, gj), ExposureFor(side),
                R.PathThreatWeight, _p.MapRules.ExposureCostM, out _, EnemyRectCells(side), true);
            return cells == null ? null : PathFinder.ToWaypoints(_map, cells, to);
        }

        /// <summary><paramref name="side"/> から見て、敵の駒の矩形（盤上の敵）が掛かるマス。</summary>
        bool[] EnemyRectCells(Side side)
        {
            var cells = new bool[_map.Width * _map.Height];
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == side) continue;
                var r = RectOf(e);
                Visibility.ForCellsWithin(_map, e.Position, r.Radius + _map.CellM, (i, j, center) =>
                {
                    double x0 = _map.OriginX + i * _map.CellM, y0 = _map.OriginY + j * _map.CellM;
                    if (r.OverlapsBox(x0, y0, x0 + _map.CellM, y0 + _map.CellM)) cells[j * _map.Width + i] = true;
                });
            }
            return cells;
        }

        /// <summary>伝令のその点での速さ（秒速 3m×マスの移動倍率。通れないマスに押し出されたら浅瀬と同じ）。</summary>
        double RunnerSpeedAt(Vec2 p)
        {
            double mv = _map.Contains(p) ? _map.MoveMult(_map.TerrainAt(p)) : 1.0;
            if (mv <= 0) mv = _map.MoveMult(TerrainMap.Ford);
            return R.SpeedMps * mv;
        }

        /// <summary>経路の時間（秒）：<paramref name="from"/> から経路の点（添字 <paramref name="index"/> から）をたどり、<paramref name="to"/> まで。</summary>
        double RunnerPathTime(Vec2 from, Vec2 to, List<Vec2> path, int index = 0)
        {
            double time = 0;
            var a = from;
            if (path != null)
                for (int k = index; k < path.Count; k++)
                {
                    time += SegmentTime(a, path[k]);
                    a = path[k];
                }
            return time + SegmentTime(a, to);
        }

        double SegmentTime(Vec2 a, Vec2 b)
        {
            double len = Vec2.Distance(a, b);
            return len < 1e-9 ? 0 : len / RunnerSpeedAt((a + b) * 0.5);
        }

        /// <summary>
        /// 伝令を <paramref name="seconds"/> 秒ぶん経路に沿って <paramref name="to"/> へ動かす。10秒ごとに経路を引き直す。着いたら true。
        /// </summary>
        bool MoveRunner(Runner r, Vec2 to, double seconds, double tickStart)
        {
            if (tickStart >= r.NextReplan - 1e-9)
            {
                r.Path = RunnerPath(r.Side, r.Position, to);
                r.PathIndex = 0;
                r.NextReplan = tickStart + R.ReplanSec;
            }
            double left = seconds;
            while (left > 1e-12)
            {
                bool last = r.Path == null || r.PathIndex >= r.Path.Count;
                var next = last ? to : r.Path[r.PathIndex];
                var d = next - r.Position;
                double len = d.Length;
                double speed = RunnerSpeedAt(r.Position);
                if (len <= speed * left + 1e-9)
                {
                    r.Position = next;
                    left -= len / speed;
                    if (last) return true;
                    r.PathIndex++;
                }
                else
                {
                    r.Position = r.Position + d * (speed * left / len);
                    left = 0;
                }
            }
            return false;
        }

        /// <summary>
        /// 命令書を渡す。出した順で新しい命令書を優先し、すでにより新しい命令書を受けていれば捨てる。
        /// 受けた命令書は、古い命令書を段ごと全て置き換え、第1段を練度の遅れの後に始める（それまでは古い命令で動き続ける）。
        /// </summary>
        void Deliver(Runner r, Company c, double t)
        {
            r.Delivered = true;
            bool stale = r.Seq < c.ReceivedSeq;
            if (_log.Enabled)
                _log.Write(Ev(t, "runner_delivered", c).Add("runner", r.Id).Add("side", SideName(r.Side)).Add("stale", stale)
                    .Add("stages", r.Sheet.Stages.Count).ToString());
            if (stale) return;
            c.AwaitingWhenDelivered = AwaitingOrders(c);
            c.ReceivedSeq = r.Seq;
            c.ActiveSheet = r.Sheet;
            c.StageIndex = 0;
            c.StageStarted = false;
            c.StageCompleted = false;
            c.StartWaitOrder = r.Sheet.First;
            c.StartWaitAt = t + _p.Of(c.Training).OrderStartSec;
        }
    }
}
