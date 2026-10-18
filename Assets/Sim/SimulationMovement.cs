using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    // P1e：行動と命令の改善（仕様「P1e：行動と命令の改善（確定）」の1・2・5）。
    // 接敵の判定、隊列の超越の判定、命令の下見。
    public sealed partial class Simulation
    {
        // ---------------------------------------------------------------- 接敵と隊列の超越

        /// <summary>
        /// 移動の前に、全中隊の接敵（400m 以内に視線の通る指揮下の敵）と、隊列の超越（押し離しをしない止まっている味方、
        /// 矩形が重なっているか）をまとめて判定する。
        /// 超越中：止まっている味方と中心どうしが 200m 未満で、矩形が重なっているか、残りの経路がその味方へ今より近づく。
        /// いったん超越を始めたら、その味方から 200m 離れるまで続く（中心を過ぎて離れ始めたところで押し出されないように）。
        /// </summary>
        void UpdateContactAndPassing()
        {
            foreach (var c in _companies)
            {
                c.PassOverlap = false;
                // 接敵の判定は動き方にだけ使うので、経路に沿って動く中隊だけ判定する（視線の計算を減らす）
                c.InContact = c.OnBoard && c.UnderCommand && c.CurrentOrder.Moves && !c.Arrived && c.Path != null && EnemyInContact(c);
            }
            foreach (var c in _companies)
            {
                if (!c.OnBoard || !c.UnderCommand || !c.CurrentOrder.Moves || c.Arrived || c.Path == null)
                {
                    c.Passing.Clear();
                    continue;
                }
                _passingWas.Clear();
                _passingWas.UnionWith(c.Passing);
                c.Passing.Clear();
                var me = RectOf(c);
                foreach (var f in _companies)
                {
                    if (!IsStationaryFriend(f, c)) continue;
                    double d = Vec2.Distance(c.Position, f.Position);
                    if (d >= _p.Movement.FriendlyMinSpacingM) continue;
                    bool overlap = UnitRect.Overlap(me, RectOf(f));
                    if (overlap) c.PassOverlap = true;
                    if (overlap || _passingWas.Contains(f.Id) || RemainingPathMinDistance(c, f.Position) < d - 1e-6) c.Passing.Add(f.Id);
                }
            }
        }

        readonly HashSet<int> _passingWas = new HashSet<int>();

        /// <summary>400m 以内に、視線の通る指揮下の敵がいるか（壊走中・再集結中の敵は数えない）。</summary>
        bool EnemyInContact(Company c)
        {
            foreach (var e in _companies)
            {
                if (!e.OnBoard || e.Side == c.Side || !e.UnderCommand) continue;
                if (Vec2.Distance(c.Position, e.Position) > B.ContactRadiusM) continue;
                if (LineOfSight.Visible(_map, _p.Sight, c.Position, e.Position)) return true;
            }
            return false;
        }

        /// <summary>今の位置から残りの経路をたどる折れ線と、点 <paramref name="p"/> の最小距離。</summary>
        static double RemainingPathMinDistance(Company c, Vec2 p)
        {
            double best = Vec2.Distance(c.Position, p);
            var a = c.Position;
            for (int k = c.PathIndex; k < c.Path.Count; k++)
            {
                var b = c.Path[k];
                best = Math.Min(best, PointSegmentDistance(p, a, b));
                a = b;
            }
            return best;
        }

        static double PointSegmentDistance(Vec2 p, Vec2 a, Vec2 b)
        {
            var ab = b - a;
            double len2 = ab.Dot(ab);
            if (len2 < 1e-12) return Vec2.Distance(p, a);
            double u = Math.Max(0, Math.Min(1, (p - a).Dot(ab) / len2));
            return Vec2.Distance(p, a + ab * u);
        }

        // ---------------------------------------------------------------- 命令の下見（P1e の5）

        /// <summary>命令の下見：目的地（攻撃では発起点）と経路。攻撃なら選んだ方角と、結果の向き。</summary>
        public sealed class OrderPreview
        {
            public Vec2 Goal;
            /// <summary>中隊の今の位置から目的地までの点の列（届かなければ届くところまで）。動かない命令では空。</summary>
            public List<Vec2> Path = new List<Vec2>();
            public double? BearingRad;
            public AttackAspect Aspect;
            /// <summary>動かない（すでに着いている、届かない、動揺中に拒まれる、目標がいない等）。</summary>
            public bool Stays;
        }

        bool _previewing;

        /// <summary>
        /// 命令を出したらどう動くかの下見。命令の開始と同じ関数で目的地と経路を計算するが、シミュレーションの状態は変えない
        /// （ログを出さず、乱数を使わず、露出の地図などの使い回しも作らない）。
        /// </summary>
        public OrderPreview Preview(Order order)
        {
            var c = _companies[order.CompanyId];
            var pv = new OrderPreview { Goal = c.Position, Stays = true };
            if (!c.UnderCommand) return pv;
            var saved = new CompanySnapshot(c);
            _previewing = true;
            try
            {
                var o = order;
                if (RefusesAdvance(c) && (o.Type == OrderType.Advance || o.Type == OrderType.Attack
                                          || (o.Type == OrderType.Entrench && o.Destination.HasValue)))
                    o = Order.Hold(c.Id, o.FacingRad);
                StartOrder(c, o, Time, Time);
                if (o.Type == OrderType.Attack && c.CurrentOrder.Type == OrderType.Attack)
                {
                    pv.BearingRad = c.AttackBearing;
                    pv.Aspect = c.AttackChosenAspect;
                }
                if (c.CurrentOrder.Moves && !c.Arrived && c.Path != null)
                {
                    pv.Stays = false;
                    pv.Goal = c.MoveGoal;
                    pv.Path.Add(c.Position);
                    for (int k = c.PathIndex; k < c.Path.Count; k++) pv.Path.Add(c.Path[k]);
                }
            }
            finally
            {
                _previewing = false;
                saved.Restore(c);
            }
            return pv;
        }

        /// <summary>受け付け待ちの命令（次の刻みの始めに受け付ける）。なければ null。同じ中隊に複数あれば最後のもの。</summary>
        public Order PendingOrderOf(int companyId)
        {
            for (int k = _pending.Count - 1; k >= 0; k--)
                if (_pending[k].CompanyId == companyId) return _pending[k];
            return null;
        }

        /// <summary>下見で <see cref="StartOrder"/> が書き換える中隊の状態の写し。</summary>
        readonly struct CompanySnapshot
        {
            readonly Order _order;
            readonly bool _arrived;
            readonly List<Vec2> _path;
            readonly int _pathIndex;
            readonly Vec2 _moveGoal;
            readonly double _nextReplan;
            readonly AttackAspect _aspect;
            readonly double _bearing;
            readonly bool _fixed;
            readonly bool _pursuing;
            readonly int _pursuitTarget;
            readonly double? _facingOverride;
            readonly double? _turnWait;
            readonly double _pinBearing;
            readonly bool _seeking;
            readonly bool _stageCompleted;
            readonly double? _pinStandoff;

            public CompanySnapshot(Company c)
            {
                _order = c.CurrentOrder;
                _arrived = c.Arrived;
                _path = c.Path;
                _pathIndex = c.PathIndex;
                _moveGoal = c.MoveGoal;
                _nextReplan = c.NextReplanTime;
                _aspect = c.AttackChosenAspect;
                _bearing = c.AttackBearing;
                _fixed = c.AttackFixed;
                _pursuing = c.Pursuing;
                _pursuitTarget = c.PursuitTargetId;
                _facingOverride = c.FacingOverride;
                _turnWait = c.TurnWaitStart;
                _pinBearing = c.PinBearing;
                _seeking = c.SeekingSight;
                _stageCompleted = c.StageCompleted;
                _pinStandoff = c.PinStandoffM;
            }

            public void Restore(Company c)
            {
                c.CurrentOrder = _order;
                c.Arrived = _arrived;
                c.Path = _path;
                c.PathIndex = _pathIndex;
                c.MoveGoal = _moveGoal;
                c.NextReplanTime = _nextReplan;
                c.AttackChosenAspect = _aspect;
                c.AttackBearing = _bearing;
                c.AttackFixed = _fixed;
                c.Pursuing = _pursuing;
                c.PursuitTargetId = _pursuitTarget;
                c.FacingOverride = _facingOverride;
                c.TurnWaitStart = _turnWait;
                c.PinBearing = _pinBearing;
                c.SeekingSight = _seeking;
                c.StageCompleted = _stageCompleted;
                c.PinStandoffM = _pinStandoff;
            }
        }
    }
}
