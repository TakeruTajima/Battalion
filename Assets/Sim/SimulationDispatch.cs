using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>具申の大きな分け方（P2c.2）：報告（知らせるだけ）／要請（返事を待つ）／宣言（独断を始めた）。</summary>
    public enum DispatchCategory { Report, Request, Declaration }

    /// <summary>具申の種類（P2c.2）。</summary>
    public enum DispatchKind
    {
        // 報告
        OrdersDone, TargetBroken, EnemySighted, HeavyLoss, AttackStalled, NeighborRouted, EntrenchDone,
        // 要請
        AskOrders, AskPursuit, AskRetreat,
        // 宣言
        Declaration,
    }

    /// <summary>伝令が出た時点の中隊の様子（届いた時には古いことがある）。</summary>
    public readonly struct DispatchState
    {
        public readonly double Time;
        public readonly double StrengthRatio;
        public readonly double Morale;
        public readonly CompanyState State;
        public readonly Vec2 Position;
        /// <summary>今の段（0 から。命令書がなければ −1）と段の数、今の命令。</summary>
        public readonly int StageIndex;
        public readonly int StageCount;
        public readonly OrderType OrderType;
        public readonly int OrderTargetId;

        public DispatchState(double time, Company c)
        {
            Time = time;
            StrengthRatio = (double)c.Strength / c.InitialStrength;
            Morale = c.Morale;
            State = c.State;
            Position = c.Position;
            StageIndex = c.ActiveSheet != null ? c.StageIndex : -1;
            StageCount = c.ActiveSheet != null ? c.ActiveSheet.Stages.Count : 0;
            OrderType = c.CurrentOrder.Type;
            OrderTargetId = c.CurrentOrder.TargetId;
        }
    }

    /// <summary>具申1つ（P2c.2）。中隊の伝令が本部へ運ぶ。</summary>
    public sealed class Dispatch
    {
        public DispatchKind Kind;
        public int CompanyId;
        /// <summary>具申の元になったことが起きた時刻（秒）。</summary>
        public double EventTime;
        /// <summary>相手の中隊（目標・見つけた敵・崩れた味方。なければ −1）。</summary>
        public int OtherId = -1;
        /// <summary>宣言の独断の種類（pursue／attack／flank／cover／no_pursuit）。宣言でなければ null。</summary>
        public string DeclarationKind;
        /// <summary>伝令が出た時点の中隊の様子（出るまでは既定値）。</summary>
        public DispatchState SentState;
        /// <summary>運んだ伝令（出るまでは −1）と、本部に届いた時刻（届くまでは null）。</summary>
        public int CourierId = -1;
        public double? ArrivedTime;

        public DispatchCategory Category =>
            Kind == DispatchKind.Declaration ? DispatchCategory.Declaration
            : Kind == DispatchKind.AskOrders || Kind == DispatchKind.AskPursuit || Kind == DispatchKind.AskRetreat ? DispatchCategory.Request
            : DispatchCategory.Report;

        public static string Name(DispatchKind k)
        {
            switch (k)
            {
                case DispatchKind.OrdersDone: return "orders_done";
                case DispatchKind.TargetBroken: return "target_broken";
                case DispatchKind.EnemySighted: return "enemy_sighted";
                case DispatchKind.HeavyLoss: return "heavy_loss";
                case DispatchKind.AttackStalled: return "attack_stalled";
                case DispatchKind.NeighborRouted: return "neighbor_routed";
                case DispatchKind.EntrenchDone: return "entrench_done";
                case DispatchKind.AskOrders: return "ask_orders";
                case DispatchKind.AskPursuit: return "ask_pursuit";
                case DispatchKind.AskRetreat: return "ask_retreat";
                default: return "declaration";
            }
        }
    }

    /// <summary>中隊の伝令の状態（P2c.2）。片道：準備中→本部へ→届いた／倒れた。</summary>
    public enum CourierState { Preparing, Outbound, Arrived, Lost }

    /// <summary>中隊の伝令1人（P2c.2）：中隊から本部へ具申を運ぶ。表示のために読める。</summary>
    public sealed class Courier : Messenger
    {
        public int CompanyId;
        public CourierState State;
        /// <summary>中隊を出た（準備を始めた）時刻（秒）。</summary>
        public double SentTime;
        public readonly List<Dispatch> Items = new List<Dispatch>();
    }

    // P2c.2：意見具申（仕様「P2c.2：意見具申（確定）」）。中隊が本部へ伝令で報告・要請・宣言を送る。
    // 情報だけで、会戦の結果を変えない：戦闘の状態を変えず、喪失の乱数は別の系列から引き、露出の地図などの使い回しを作らない。
    public sealed partial class Simulation
    {
        // 中隊の伝令の喪失の乱数（戦闘とも本部の伝令とも別の系列）
        const ulong CourierStream = 0xc0a1e5ce5UL;

        SimParams.CouriersParams CP => _p.Couriers;

        /// <summary>中隊ごとの具申の覚え。</summary>
        sealed class DispatchBook
        {
            public readonly List<Dispatch> Pending = new List<Dispatch>();
            public double LastBatchSent = double.NegativeInfinity;
            public bool PrevAwaiting;
            public double? AwaitingSince;
            public double NextAskOrders;
            public readonly HashSet<int> Discovered = new HashSet<int>();
            public int LossBaseStrength;
            public bool PrevShakenHalt;
            public bool RetreatAsked;
            public double? LastArrival;
        }

        readonly List<Courier> _couriers = new List<Courier>();
        readonly List<Dispatch> _inbox = new List<Dispatch>();
        DispatchBook[] _books;
        Pcg32 _courierRng;

        /// <summary>中隊の伝令（倒れた・届いたものも含む。表示用に読むだけ）。</summary>
        public IReadOnlyList<Courier> Couriers => _couriers;

        /// <summary>本部に届いた具申（届いた順。表示用に読むだけ）。</summary>
        public IReadOnlyList<Dispatch> Inbox => _inbox;

        /// <summary>その中隊の伝令が最後に本部に届いた時刻（まだなければ null）。</summary>
        public double? LastCourierArrival(int companyId) => _books != null && companyId < _books.Length ? _books[companyId].LastArrival : null;

        bool SendsDispatches(Company c) => c.Side == Side.Blue && _hq[(int)c.Side].HasValue;

        /// <summary>具申を列に足す（戦闘の状態は変えない）。</summary>
        void NoteDispatch(Company c, DispatchKind kind, double t, Company other = null, string declaration = null)
        {
            if (!SendsDispatches(c) || _previewing) return;
            EnsureBooks();
            _books[c.Id].Pending.Add(new Dispatch
            {
                Kind = kind, CompanyId = c.Id, EventTime = t, OtherId = other?.Id ?? -1, DeclarationKind = declaration,
            });
        }

        void EnsureBooks()
        {
            if (_books != null && _books.Length == _companies.Count) return;
            var old = _books;
            _books = new DispatchBook[_companies.Count];
            for (int k = 0; k < _books.Length; k++) _books[k] = old != null && k < old.Length ? old[k] : NewBook(_companies[k]);
        }

        DispatchBook NewBook(Company c)
        {
            var b = new DispatchBook { LossBaseStrength = c.InitialStrength };
            // 会戦の初めにすでに 600m 以内で気付いている敵は、既知の敵として報告しない
            foreach (var e in _companies)
                if (e.OnBoard && e.Side != c.Side && e.UnderCommand
                    && Vec2.Distance(c.Position, e.Position) <= CP.DiscoverRadiusM && Aware(c, e))
                    b.Discovered.Add(e.Id);
            // 会戦の初めの命令待ちは「命令を終えた」にしない。命令を請うの 5分は会戦の初めから数える
            b.PrevAwaiting = AwaitingOrders(c);
            if (b.PrevAwaiting)
            {
                b.AwaitingSince = 0;
                b.NextAskOrders = CP.AskOrdersAfterSec;
            }
            return b;
        }

        // ---------------------------------------------------------------- 刻みの最後

        /// <summary>中隊の伝令を動かし、具申の判定をし、出すべき伝令を出す（刻みの最後。戦闘の状態は変えない）。</summary>
        void UpdateDispatches(double t)
        {
            if (!_hq[(int)Side.Blue].HasValue) return;
            if (_courierRng == null) _courierRng = new Pcg32(_seed, CourierStream);
            EnsureBooks();
            WithoutCaching(() =>
            {
                MoveCouriers(t);
                return 0;
            });
            foreach (var c in _companies)
            {
                if (!SendsDispatches(c) || !c.OnBoard) continue;
                var b = _books[c.Id];
                if (c.UnderCommand) Watch(c, b, t);
                SendPending(c, b, t);
            }
        }

        /// <summary>前の刻みからの変わり目を見て、具申を列に足す。</summary>
        void Watch(Company c, DispatchBook b, double t)
        {
            // 命令を終えた・命令を請う
            bool awaiting = AwaitingOrders(c);
            if (awaiting && !b.PrevAwaiting)
            {
                NoteDispatch(c, DispatchKind.OrdersDone, t);
                b.AwaitingSince = t;
                b.NextAskOrders = t + CP.AskOrdersAfterSec;
            }
            if (!awaiting) b.AwaitingSince = null;
            else if (b.AwaitingSince.HasValue && t >= b.NextAskOrders - 1e-9)
            {
                NoteDispatch(c, DispatchKind.AskOrders, t);
                b.NextAskOrders = t + CP.AskOrdersRepeatSec;
            }
            b.PrevAwaiting = awaiting;

            // 敵発見：気付いている指揮下の敵が 600m 以内に初めて入った
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || !e.UnderCommand || b.Discovered.Contains(e.Id)) continue;
                if (Vec2.Distance(c.Position, e.Position) > CP.DiscoverRadiusM || !Aware(c, e)) continue;
                b.Discovered.Add(e.Id);
                NoteDispatch(c, DispatchKind.EnemySighted, t, e);
            }

            // 損害大：前の「損害大」から、初めの兵力の 20%（慎重は 10%）を新たに失った
            double step = CP.HeavyLossStep[c.Personality] * c.InitialStrength;
            if (b.LossBaseStrength - c.Strength >= step - 1e-9)
            {
                NoteDispatch(c, DispatchKind.HeavyLoss, t);
                b.LossBaseStrength = c.Strength;
            }

            // 攻撃頓挫：前進・攻撃の段が動揺で停止中になった
            if (c.ShakenHalt && !b.PrevShakenHalt) NoteDispatch(c, DispatchKind.AttackStalled, t);
            b.PrevShakenHalt = c.ShakenHalt;

            // 後退の許可：動揺していて（慎重は士気 60 未満）、この刻みに撃たれた。状況が変わるまで繰り返さない
            double v = CP.RetreatAskMorale[c.Personality];
            bool low = c.Personality == Personality.Cautious ? c.Morale < v : c.Morale <= v;
            bool shot = c.LastTargetedTime == t;
            if (low && shot && !b.RetreatAsked)
            {
                NoteDispatch(c, DispatchKind.AskRetreat, t);
                b.RetreatAsked = true;
            }
            if (!low || t - c.LastTargetedTime >= CP.RetreatAskQuietSec - 1e-9) b.RetreatAsked = false;
        }

        /// <summary>
        /// たまった具申を出す：宣言があればすぐ（たまっている報告・要請も一緒に）、なければ前の伝令から 60秒あいていれば出す。
        /// 同時に出ている伝令が上限なら、出さずに捨てる。指揮下でない間は出さずにためる。
        /// </summary>
        void SendPending(Company c, DispatchBook b, double t)
        {
            if (b.Pending.Count == 0 || !c.UnderCommand) return;
            bool declaration = b.Pending.Exists(d => d.Kind == DispatchKind.Declaration);
            if (!declaration && t - b.LastBatchSent < CP.BatchSec - 1e-9) return;
            int out_ = 0;
            foreach (var r in _couriers)
                if (r.CompanyId == c.Id && (r.State == CourierState.Preparing || r.State == CourierState.Outbound)) out_++;
            if (out_ >= CP.MaxOutPerCompany)
            {
                if (_log.Enabled)
                    _log.Write(Ev(t, "courier_dropped", c).Add("side", SideName(c.Side)).Add("items", ItemsText(b.Pending)).ToString());
                b.Pending.Clear();
                b.LastBatchSent = t;
                return;
            }
            var courier = new Courier
            {
                Id = _couriers.Count, Side = c.Side, CompanyId = c.Id, State = CourierState.Preparing,
                Position = c.Position, SentTime = t,
            };
            var state = new DispatchState(t, c);
            foreach (var d in b.Pending)
            {
                d.SentState = state;
                d.CourierId = courier.Id;
                courier.Items.Add(d);
            }
            b.Pending.Clear();
            b.LastBatchSent = t;
            _couriers.Add(courier);
            if (_log.Enabled)
                _log.Write(Ev(t, "courier_sent", c).Add("courier", courier.Id).Add("side", SideName(c.Side)).Add("items", ItemsText(courier.Items)).ToString());
        }

        string ItemsText(List<Dispatch> items)
        {
            var parts = new List<string>();
            foreach (var d in items)
            {
                string s = d.Kind == DispatchKind.Declaration ? "declaration_" + d.DeclarationKind : Dispatch.Name(d.Kind);
                if (d.OtherId >= 0) s += ":" + _companies[d.OtherId].Name;
                parts.Add(s);
            }
            return string.Join(",", parts);
        }

        /// <summary>
        /// 中隊の伝令を動かす：準備 10秒の後、本部へ（経路・速さ・川・射界での喪失は本部の伝令と同じ）。着いたら届く。
        /// 露出の地図などの使い回しを作らない（呼び出し側で <see cref="WithoutCaching"/>）。
        /// </summary>
        void MoveCouriers(double t)
        {
            double dt = _p.TickSec;
            double tickStart = t - dt;
            foreach (var r in _couriers)
            {
                if (r.State == CourierState.Arrived || r.State == CourierState.Lost) continue;
                double budget = dt;
                if (r.State == CourierState.Preparing)
                {
                    double ready = r.SentTime + CP.PrepSec;
                    if (t < ready - 1e-9) continue;
                    r.State = CourierState.Outbound;
                    r.NextReplan = double.NegativeInfinity;
                    budget = Math.Min(dt, t - ready);
                }
                if (budget <= 0) continue;
                var hq = _hq[(int)r.Side].Value;
                if (MoveRunner(r, hq, budget, tickStart))
                {
                    r.State = CourierState.Arrived;
                    var b = _books[r.CompanyId];
                    b.LastArrival = t;
                    foreach (var d in r.Items)
                    {
                        d.ArrivedTime = t;
                        _inbox.Add(d);
                    }
                    if (_log.Enabled)
                        _log.Write(Ev(t, "courier_delivered", _companies[r.CompanyId]).Add("courier", r.Id).Add("side", SideName(r.Side))
                            .Add("sent", r.SentTime).Add("items", ItemsText(r.Items)).ToString());
                    continue;
                }
                // 射界を通る間、1秒あたり runners.loss_per_sec で倒れる（本部の上・地図の外は数えない）
                if (_map.Contains(r.Position) && Vec2.Distance(r.Position, hq) > 1e-9 && ExposureFor(r.Side)[_map.IndexOf(r.Position)])
                {
                    double p = 1 - Math.Pow(1 - R.LossPerSec, budget);
                    if (_courierRng.NextDouble() < p)
                    {
                        r.State = CourierState.Lost;
                        if (_log.Enabled)
                            _log.Write(Ev(t, "courier_lost", _companies[r.CompanyId]).Add("courier", r.Id).Add("side", SideName(r.Side)).ToString());
                    }
                }
            }
        }

        // ---------------------------------------------------------------- 要請への返事の起案（表示から使う。純 C#）

        /// <summary>
        /// 要請の［許可］で作る起案（P2c.2）：追撃の可否はその目標への攻撃（範囲の指定なし・崩れたら追撃）の第1段、
        /// 後退の許可・命令を請うは白紙（後退の行き先はプレイヤーが地図で決める）。報告・宣言なら null。
        /// </summary>
        public static OrderSheetDraft DraftForRequest(Dispatch d, int maxStages)
        {
            if (d.Category != DispatchCategory.Request) return null;
            var draft = new OrderSheetDraft(d.CompanyId);
            if (d.Kind == DispatchKind.AskPursuit && d.OtherId >= 0)
                draft.Write(Order.Attack(d.CompanyId, d.OtherId, AttackAspect.None, AttackOnBreak.Pursue), maxStages);
            return draft;
        }
    }
}
