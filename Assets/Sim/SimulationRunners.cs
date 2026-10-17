using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>伝令の状態（P2a）。</summary>
    public enum RunnerState { Preparing, Outbound, Returning, Done, Lost }

    /// <summary>命令を出した結果（P2a）。</summary>
    public enum SendResult
    {
        /// <summary>伝令を使わずに次の刻みで受け付ける（会戦開始前、または本部のない陣営）。</summary>
        Immediate,
        /// <summary>伝令が本部を出た（準備から）。</summary>
        Sent,
        /// <summary>判断枠がない（伝令が出払っている）。命令は出ていない。</summary>
        NoSlot,
    }

    /// <summary>伝令1人（1つの命令を運ぶ）。表示のために読める。</summary>
    public sealed class Runner
    {
        public int Id;
        public Side Side;
        public Order Order;
        public int CompanyId;
        public RunnerState State;
        public Vec2 Position;
        /// <summary>発令した時刻（秒）。</summary>
        public double SentTime;
        /// <summary>出した順（同じ中隊への命令の新旧を決める）。</summary>
        public long Seq;
        /// <summary>判断枠を数えるか（台本の伝令は数えない）。</summary>
        public bool CountsSlot;
        /// <summary>判断枠を空けたか（受領、または受領報告なしの強制開放）。</summary>
        public bool SlotReleased;
        /// <summary>受領報告なしとして判断枠を空ける時刻（発令＋往復予定の 1.5 倍）。</summary>
        public double OverdueAt;
        /// <summary>命令を渡したか。</summary>
        public bool Delivered;
    }

    // P2a：伝令（仕様「P2a：伝令（確定）」）。命令は本部から伝令が運び、届いてから練度の遅れの後に始まる。
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

        /// <summary>伝令（倒れた・終わった伝令も含む。表示用に読むだけ）。</summary>
        public IReadOnlyList<Runner> Runners => _runners;

        /// <summary>会戦開始前（時刻 0 のまま、まだ一度も刻みを進めていない）。この間の命令は伝令を使わずに届く。</summary>
        public bool BeforeStart => Tick == 0;

        /// <summary>判断枠のうち使っている数（受領も強制開放もされていない、数える伝令）。</summary>
        public int RunnerSlotsUsed(Side side)
        {
            int n = 0;
            foreach (var r in _runners)
                if (r.Side == side && r.CountsSlot && !r.SlotReleased) n++;
            return n;
        }

        public int MaxRunners => R.MaxOut;

        /// <summary>
        /// 命令を出す（P2a）。会戦開始前と本部のない陣営は、伝令を使わずに次の刻みで受け付ける（<see cref="Issue"/>）。
        /// それ以外は本部から伝令を出す。判断枠がなければ出さない。
        /// </summary>
        public SendResult SendOrder(Order order) => SendOrder(order, true);

        /// <summary>台本の命令（判断枠を数えない）。</summary>
        public SendResult SendScriptOrder(Order order) => SendOrder(order, false);

        SendResult SendOrder(Order order, bool countsSlot)
        {
            var c = _companies[order.CompanyId];
            var hq = _hq[(int)c.Side];
            if (BeforeStart || !hq.HasValue)
            {
                Issue(order);
                return SendResult.Immediate;
            }
            if (countsSlot && RunnerSlotsUsed(c.Side) >= R.MaxOut) return SendResult.NoSlot;

            double d0 = Vec2.Distance(hq.Value, c.Position);
            var r = new Runner
            {
                Id = _runners.Count,
                Side = c.Side,
                Order = order,
                CompanyId = c.Id,
                State = RunnerState.Preparing,
                Position = hq.Value,
                SentTime = Time,
                Seq = ++_orderSeq,
                CountsSlot = countsSlot,
                OverdueAt = Time + R.OverdueMult * (R.PrepSec + 2 * d0 / R.SpeedMps),
            };
            _runners.Add(r);
            if (_log.Enabled)
                _log.Write(Ev(Time, "runner_sent", c)
                    .Add("runner", r.Id).Add("side", r.Side == Side.Blue ? "blue" : "red")
                    .Add("order", Order.Name(order.Type))
                    .Add("eta", Math.Round(R.PrepSec + d0 / R.SpeedMps, 1))
                    .ToString());
            return SendResult.Sent;
        }

        /// <summary>
        /// 中隊へ向かっている（または本部が向かっていると思っている）最新の命令と、到着予定（秒。準備の残り＋今の距離÷速さ）。
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
            if (best != null)
            {
                double prep = Math.Max(0, best.SentTime + R.PrepSec - Time);
                etaSec = prep + Vec2.Distance(best.Position, _companies[companyId].Position) / R.SpeedMps;
            }
            return best;
        }

        /// <summary>
        /// 伝令を動かす（刻みの始め、命令の適用の前）。準備→往路（中隊の今の位置を追いかける）→受け渡し→帰路→受領。
        /// 射界のマスにいる間は倒れうる。受領報告なしの強制開放と、開始待ちの命令の開始もここで扱う。
        /// </summary>
        void UpdateRunners(double t, double tickStart)
        {
            if (_runnerRng == null) _runnerRng = new Pcg32(_seed, RunnerStream);
            double dt = _p.TickSec;
            foreach (var r in _runners)
            {
                if (r.State == RunnerState.Done || r.State == RunnerState.Lost) continue;
                double budget = dt;
                if (r.State == RunnerState.Preparing)
                {
                    double ready = r.SentTime + R.PrepSec;
                    if (t < ready - 1e-9) continue;
                    r.State = RunnerState.Outbound;
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
                        if (_log.Enabled) _log.Write(Ev(t, "runner_undeliverable", c).Add("runner", r.Id).Add("side", r.Side == Side.Blue ? "blue" : "red").ToString());
                    }
                    else if (MoveRunner(r, c.Position, budget))
                    {
                        Deliver(r, c, t);
                        r.State = RunnerState.Returning;
                    }
                }
                else if (r.State == RunnerState.Returning)
                {
                    var hq = _hq[(int)r.Side].Value;
                    if (MoveRunner(r, hq, budget))
                    {
                        r.State = RunnerState.Done;
                        var c = _companies[r.CompanyId];
                        bool late = r.SlotReleased;
                        r.SlotReleased = true;
                        if (_log.Enabled) _log.Write(Ev(t, "runner_returned", c).Add("runner", r.Id).Add("side", r.Side == Side.Blue ? "blue" : "red").Add("late", late).ToString());
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
                            if (_log.Enabled) _log.Write(Ev(t, "runner_lost", _companies[r.CompanyId]).Add("runner", r.Id).Add("side", r.Side == Side.Blue ? "blue" : "red").Add("delivered", r.Delivered).ToString());
                        }
                    }
                }
            }
            // 受領報告なし：発令から往復予定の 1.5 倍が過ぎても受領が来なければ、判断枠を強制的に空ける
            foreach (var r in _runners)
            {
                if (!r.CountsSlot || r.SlotReleased || t < r.OverdueAt) continue;
                r.SlotReleased = true;
                if (_log.Enabled) _log.Write(Ev(t, "receipt_overdue", _companies[r.CompanyId]).Add("runner", r.Id).Add("side", r.Side == Side.Blue ? "blue" : "red").ToString());
            }
            // 開始待ちの命令：届いてから練度の遅れが経ったら始める（この刻みの命令の適用で）
            foreach (var c in _companies)
            {
                if (c.StartWaitOrder == null || t < c.StartWaitAt - 1e-9) continue;
                _pending.Add(c.StartWaitOrder);
                if (_log.Enabled) _log.Write(Ev(t, "order_start", c).Add("order", Order.Name(c.StartWaitOrder.Type)).ToString());
                c.StartWaitOrder = null;
            }
        }

        /// <summary>伝令を <paramref name="to"/> へ <paramref name="seconds"/> 秒ぶん動かす。着いたら true（位置は着いた点）。</summary>
        bool MoveRunner(Runner r, Vec2 to, double seconds)
        {
            var d = to - r.Position;
            double len = d.Length, step = R.SpeedMps * seconds;
            if (len <= step + 1e-9)
            {
                r.Position = to;
                return true;
            }
            r.Position = r.Position + d * (step / len);
            return false;
        }

        /// <summary>
        /// 命令を渡す。出した順で新しい命令を優先し、すでにより新しい命令を受けていれば捨てる。
        /// 受けた命令は、練度の遅れの後に始まる（それまでは古い命令で動き続ける。開始待ちの命令は置き換える）。
        /// </summary>
        void Deliver(Runner r, Company c, double t)
        {
            r.Delivered = true;
            bool stale = r.Seq < c.ReceivedSeq;
            if (_log.Enabled) _log.Write(Ev(t, "runner_delivered", c).Add("runner", r.Id).Add("side", r.Side == Side.Blue ? "blue" : "red").Add("stale", stale).ToString());
            if (stale) return;
            c.ReceivedSeq = r.Seq;
            c.StartWaitOrder = r.Order;
            c.StartWaitAt = t + _p.Of(c.Training).OrderStartSec;
        }
    }
}
