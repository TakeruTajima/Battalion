using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>敵の規模の見積もり（P2d）：多い（7割以上）／半ば（4〜7割）／少ない（4割未満）。</summary>
    public enum EnemySize { Many, Half, Few }

    /// <summary>報告された敵の様子（P2d）。壊走中・降伏は正確に報告する。</summary>
    public enum ReportedState { Normal, Routed, Surrendered }

    /// <summary>報告の駒の見え方（P2d）：新しい／薄い（5分）／「?」（10分）／地図から消えた（30分。戦闘詳報には残る）。</summary>
    public enum PieceLook { Fresh, Faded, Unknown, Forgotten }

    /// <summary>敵の報告1つ（P2d）。中隊の伝令が本部へ運ぶ。</summary>
    public sealed class EnemyReport
    {
        /// <summary>見た中隊と、見た時刻（秒）。</summary>
        public int ObserverId;
        public double SeenTime;
        /// <summary>報告の位置（見た時の実際の位置に、見た中隊の練度の誤差の中で散らした点）と、その誤差の半径。</summary>
        public Vec2 Position;
        public double ErrorM;
        public EnemySize Size;
        /// <summary>動いているか、動いている向き（8方位に丸めたラジアン。止まっていれば null）。</summary>
        public bool Moving;
        public double? HeadingRad;
        public ReportedState State;
        /// <summary>実際の敵（検証と戦闘詳報の重ね表示のためだけ。本部の推定には使わない）。</summary>
        public int TrueEnemyId;
    }

    /// <summary>報告の駒（本部の推定。P2d）。届いた報告を位置と時刻で紐付けて作る。実際の敵とずれることがある。</summary>
    public sealed class IntelPiece
    {
        public int Id;
        public Vec2 Position;
        public double ErrorM;
        public double SeenTime;
        public EnemySize Size;
        public bool Moving;
        public double? HeadingRad;
        public ReportedState State;
        /// <summary>会戦の始めの敵情（地図ファイルの initial_intel）から作った駒か。その時の敵の向き（なければ null）。</summary>
        public bool Initial;
        public double? FacingRad;
        /// <summary>紐付けた報告（時刻順）。</summary>
        public readonly List<EnemyReport> Reports = new List<EnemyReport>();
    }

    // P2d：霧と報告（仕様「P2d：霧と報告（確定）」）。会戦の結果を変えない：報告の誤差・規模の間違いは戦闘と別の乱数の系列から引き、
    // 本部の推定（報告の駒）は戦闘の判断に使わない。報告の駒への命令（プレイヤーの命令）だけが、受けた中隊の目標の決め方に効く。
    public sealed partial class Simulation
    {
        // 報告の誤差の乱数（戦闘・本部の伝令・中隊の伝令とも別の系列）
        const ulong FogStream = 0xf06f06UL;

        SimParams.FogParams FG => _p.Fog;

        /// <summary>見た敵の覚え（中隊ごと、敵ごと）。</summary>
        sealed class SightMemo
        {
            public bool Reported;
            public Vec2 ReportedPosition;
            public bool ReportedMoving;
            public ReportedState ReportedState;
            public double LastReportTime;
            public bool AwareLastTick;
        }

        readonly List<IntelPiece> _intel = new List<IntelPiece>();
        Pcg32 _fogRng;

        /// <summary>青の本部の報告の駒（作った順。地図から消えた駒も含む。表示用に読むだけ）。</summary>
        public IReadOnlyList<IntelPiece> Intel => _intel;

        /// <summary>会戦の始めの敵情（時刻 0 の報告）を足す。誤差は 0、向きは止まっている。</summary>
        public IntelPiece AddInitialIntel(Vec2 position, EnemySize size, double? facingRad = null, int trueEnemyId = -1)
        {
            var r = new EnemyReport
            {
                ObserverId = -1, SeenTime = 0, Position = position, ErrorM = 0, Size = size, State = ReportedState.Normal, TrueEnemyId = trueEnemyId,
            };
            var piece = NewPiece(r);
            piece.Initial = true;
            piece.FacingRad = facingRad;
            return piece;
        }

        /// <summary>駒の見え方（見た時刻からの経過で決まる）。</summary>
        public PieceLook LookOf(IntelPiece p, double t)
        {
            double age = t - p.SeenTime;
            if (age >= FG.ForgetSec - 1e-9) return PieceLook.Forgotten;
            if (age >= FG.UnknownSec - 1e-9) return PieceLook.Unknown;
            if (age >= FG.FadeSec - 1e-9) return PieceLook.Faded;
            return PieceLook.Fresh;
        }

        /// <summary>兵力の割合から規模を見積もる（新兵の間違いを除く）。</summary>
        public EnemySize SizeOf(Company e)
        {
            double r = (double)e.Strength / e.InitialStrength;
            return r >= FG.SizeManyRatio - 1e-9 ? EnemySize.Many : r >= FG.SizeHalfRatio - 1e-9 ? EnemySize.Half : EnemySize.Few;
        }

        // ---------------------------------------------------------------- 報告を送る時（刻みの最後。戦闘の状態は変えない）

        /// <summary>
        /// 見ている敵の報告（P2c.2 の「敵発見」を広げる）：発見（気付いている指揮下の敵を初めて見た）、続報（接敵している間 2分ごと）、
        /// 変化（動き出した・止まった・200m 以上動いた・壊走した・降伏した）。
        /// </summary>
        void WatchEnemies(Company c, DispatchBook b, double t)
        {
            foreach (var e in _companies)
            {
                if (e.Side == c.Side) continue;
                if (!b.Sights.TryGetValue(e.Id, out var m))
                {
                    m = new SightMemo();
                    b.Sights[e.Id] = m;
                }
                bool aware = e.OnBoard && Aware(c, e);
                if (!m.Reported)
                {
                    if (aware && e.UnderCommand)
                    {
                        b.Discovered.Add(e.Id);
                        NoteEnemyReport(c, b, m, e, DispatchKind.EnemySighted, t);
                    }
                }
                else if (e.State == CompanyState.Surrendered)
                {
                    // 見ていた敵が降伏した
                    if (m.AwareLastTick && m.ReportedState != ReportedState.Surrendered) NoteEnemyReport(c, b, m, e, DispatchKind.EnemyChanged, t);
                }
                else if (aware)
                {
                    bool moving = EnemyMoving(e);
                    bool routed = e.State == CompanyState.Routed;
                    if (moving != m.ReportedMoving || (routed && m.ReportedState != ReportedState.Routed)
                        || Vec2.Distance(e.Position, m.ReportedPosition) >= FG.ChangeMoveM - 1e-9)
                        NoteEnemyReport(c, b, m, e, DispatchKind.EnemyChanged, t);
                    else if (Vec2.Distance(c.Position, e.Position) <= B.ContactRadiusM && t - m.LastReportTime >= FG.UpdateSec - 1e-9)
                        NoteEnemyReport(c, b, m, e, DispatchKind.EnemyUpdate, t);
                }
                m.AwareLastTick = aware;
            }
        }

        /// <summary>報告の「動いている」：壊走中、または移動の命令の途中（伏せて止まっている間を除く）。</summary>
        bool EnemyMoving(Company e) => e.State == CompanyState.Routed || (e.UnderCommand && MovingPosture(e) && !PinnedDown(e));

        /// <summary>報告を作って列に足す。同じ敵の報告がまだ列にあれば、新しい方に置き換える。</summary>
        void NoteEnemyReport(Company c, DispatchBook b, SightMemo m, Company e, DispatchKind kind, double t)
        {
            if (!SendsDispatches(c) || _previewing) return;
            if (_fogRng == null) _fogRng = new Pcg32(_seed, FogStream);
            var r = MakeReport(c, e, t);
            m.Reported = true;
            m.ReportedPosition = e.Position;
            m.ReportedMoving = r.Moving;
            m.ReportedState = r.State;
            m.LastReportTime = t;
            // 初めの発見は「発見」のまま残す（続報・変化で置き換えない）
            int k = b.Pending.FindIndex(d => d.Report != null && d.OtherId == e.Id);
            if (k >= 0 && b.Pending[k].Kind != DispatchKind.EnemySighted) b.Pending.RemoveAt(k);
            else if (k >= 0)
            {
                b.Pending[k].Report = r;
                return;
            }
            b.Pending.Add(new Dispatch { Kind = kind, CompanyId = c.Id, EventTime = t, OtherId = e.Id, Report = r });
        }

        /// <summary>報告の中身：位置の誤差（練度）、規模（新兵は 20% で1段階間違える）、向きと動き（8方位）、様子、時刻。</summary>
        EnemyReport MakeReport(Company c, Company e, double t)
        {
            double err = _p.Of(c.Training).ReportErrorM;
            double ang = _fogRng.NextDouble() * 2 * Math.PI;
            double rad = err * Math.Sqrt(_fogRng.NextDouble());
            var size = SizeOf(e);
            if (c.Training == Training.Green && _fogRng.NextDouble() < FG.GreenSizeErrorRate)
            {
                int s = (int)size;
                int d = s == 0 ? 1 : s == 2 ? -1 : (_fogRng.NextDouble() < 0.5 ? -1 : 1);
                size = (EnemySize)(s + d);
            }
            bool moving = e.OnBoard && EnemyMoving(e);
            double? heading = null;
            if (moving)
            {
                double h = e.Facing;
                if (e.State != CompanyState.Routed && e.Path != null && e.PathIndex < e.Path.Count)
                {
                    var d = e.Path[e.PathIndex] - e.Position;
                    if (d.Length > 1e-9) h = Math.Atan2(d.Y, d.X);
                }
                heading = Angles.Wrap(Math.Round(h / (Math.PI / 4)) * (Math.PI / 4));
            }
            var state = e.State == CompanyState.Surrendered ? ReportedState.Surrendered
                : e.State == CompanyState.Routed ? ReportedState.Routed : ReportedState.Normal;
            return new EnemyReport
            {
                ObserverId = c.Id, SeenTime = t, Position = e.Position + Vec2.FromAngle(ang) * rad, ErrorM = err, Size = size,
                Moving = moving, HeadingRad = heading, State = state, TrueEnemyId = e.Id,
            };
        }

        // ---------------------------------------------------------------- 本部の推定（報告の駒）

        /// <summary>
        /// 届いた報告を、位置が 300m 以内で見た時刻の差が 10分以内の駒（近い方）に紐付ける。なければ新しい駒にする。
        /// 地図から消えた駒にも、時刻の条件を満たせば紐付く。
        /// </summary>
        public void ReceiveReport(EnemyReport r)
        {
            IntelPiece best = null;
            double bestD = double.MaxValue;
            foreach (var p in _intel)
            {
                double d = Vec2.Distance(p.Position, r.Position);
                if (d > FG.LinkRadiusM + 1e-9 || Math.Abs(r.SeenTime - p.SeenTime) > FG.LinkWindowSec + 1e-9) continue;
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }
            if (best == null)
            {
                NewPiece(r);
                return;
            }
            best.Reports.Add(r);
            // 古い報告が後から届いたときは、駒の位置と様子を戻さない
            if (r.SeenTime < best.SeenTime) return;
            Apply(best, r);
        }

        IntelPiece NewPiece(EnemyReport r)
        {
            var p = new IntelPiece { Id = _intel.Count };
            p.Reports.Add(r);
            Apply(p, r);
            _intel.Add(p);
            return p;
        }

        static void Apply(IntelPiece p, EnemyReport r)
        {
            p.Position = r.Position;
            p.ErrorM = r.ErrorM;
            p.SeenTime = r.SeenTime;
            p.Size = r.Size;
            p.Moving = r.Moving;
            p.HeadingRad = r.HeadingRad;
            p.State = r.State;
        }

        // ---------------------------------------------------------------- 報告の駒への命令

        /// <summary>報告の駒への命令の目標：気付いている指揮下の敵のうち、駒の位置に最も近い敵（駒の位置から 300m 以内）。いなければ null。</summary>
        Company PieceTargetFor(Company c, Vec2 piecePos)
        {
            Company best = null;
            double bestD = FG.PieceTargetRadiusM + 1e-9;
            foreach (var e in _companies)
            {
                if (!e.OnBoard || !e.UnderCommand || e.Side == c.Side) continue;
                double d = Vec2.Distance(e.Position, piecePos);
                if (d > bestD || !Aware(c, e)) continue;
                best = e;
                bestD = d;
            }
            return best;
        }

        /// <summary>
        /// 命令の始めに、報告の駒への命令の目標を決める。決まれば今の攻撃・拘束と同じ命令にする。決まらなければ、駒の位置へ前進し
        /// （<see cref="Company.PieceOrder"/> に元の命令を残す）、毎刻み探す。
        /// </summary>
        Order ResolvePieceOrder(Company c, Order order)
        {
            if (!order.ToPiece)
            {
                c.PieceOrder = null;
                return order;
            }
            var e = PieceTargetFor(c, order.PiecePosition.Value);
            if (e != null)
            {
                c.PieceOrder = null;
                return order.WithTarget(e.Id);
            }
            c.PieceOrder = order;
            return Order.Advance(c.Id, order.PiecePosition.Value);
        }

        /// <summary>
        /// 報告の駒へ向かっている中隊（刻みの終わり）：駒の近くに気付いている敵が見えたら、その敵への攻撃・拘束を始める。
        /// 着いても見えなければ、その場で保持し「敵を見ず」を具申する（要請：命令を請う）。
        /// </summary>
        void UpdatePieceOrders(double t)
        {
            foreach (var c in _companies)
            {
                var o = c.PieceOrder;
                if (o == null) continue;
                if (!c.UnderCommand || c.CurrentOrder.Type != OrderType.Advance)
                {
                    c.PieceOrder = null;
                    continue;
                }
                var e = PieceTargetFor(c, o.PiecePosition.Value);
                if (e != null)
                {
                    StartOrder(c, o.WithTarget(e.Id), t, t);
                    continue;
                }
                if (c.Arrived)
                {
                    c.PieceOrder = null;
                    HoldInPlace(c);
                    NoteDispatch(c, DispatchKind.NoEnemySeen, t);
                    if (_log.Enabled) _log.Write(Ev(t, "no_enemy_seen", c).Add("side", SideName(c.Side)).ToString());
                }
            }
        }
    }
}
