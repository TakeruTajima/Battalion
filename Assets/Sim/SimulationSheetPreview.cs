using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>見積もりの時刻の種類（P2b）。</summary>
    public enum EtaKind
    {
        /// <summary>ゲーム内の時刻（秒）。</summary>
        Absolute,
        /// <summary>信号弾（<see cref="SheetStagePreview.Color"/>）が上がってからの秒。</summary>
        AfterFlare,
        /// <summary>決まらない（攻撃・陣地構築の完了しだい等）。</summary>
        Unknown,
    }

    /// <summary>命令書の1段の下見（P2b）：経路と行き先、始まる時刻と着く時刻の見積もり。</summary>
    public sealed class SheetStagePreview
    {
        public Simulation.OrderPreview Preview;
        public EtaKind Kind;
        public FlareColor Color;
        /// <summary>始まる時刻・行き先に着く時刻の見積もり（<see cref="Kind"/> が Unknown なら意味なし）。</summary>
        public double Start, Arrive;
    }

    // P2b の到着予定：命令書の各段を順に下見し、経路の長さ÷（秒速 1.2m×マスの移動倍率）で着く時刻を見積もる（概算）
    public sealed partial class Simulation
    {
        /// <summary>
        /// 命令書の段ごとの下見と到着予定。第1段は中隊の今の位置から、第2段以降は前の段の行き先から下見する（中隊の状態は変えない）。
        /// <paramref name="firstStart"/> は第1段が始まる時刻の見積もり（呼ぶ側が決める：会戦開始前は今、そうでなければ伝令の到着予定＋練度の遅れ）。
        /// null なら第1段から決まらない。
        /// </summary>
        public List<SheetStagePreview> PreviewSheet(IReadOnlyList<Stage> stages, double? firstStart)
        {
            var result = new List<SheetStagePreview>();
            if (stages.Count == 0 || stages[0].Order == null) return result;
            var c = _companies[stages[0].Order.CompanyId];
            double delay = _p.Of(c.Training).OrderStartSec;
            var savedPos = c.Position;
            var savedFacing = c.Facing;
            var pos = c.Position;
            EtaKind kind = firstStart.HasValue ? EtaKind.Absolute : EtaKind.Unknown;
            FlareColor color = default;
            double start = firstStart ?? 0, prevStart = start, prevArrive = start;
            OrderType prevType = OrderType.Hold;
            try
            {
                for (int k = 0; k < stages.Count; k++)
                {
                    var st = stages[k];
                    if (st.Order == null) break;
                    if (k > 0)
                    {
                        var tr = st.Trigger;
                        switch (tr.Kind)
                        {
                            case TriggerKind.Complete:
                                // 前進・後退は着いたら完了。攻撃・陣地構築の完了の時刻は見積もれない
                                if (prevType == OrderType.Advance || prevType == OrderType.Retreat) start = prevArrive + delay;
                                else kind = EtaKind.Unknown;
                                break;
                            case TriggerKind.Time:
                                start = (kind == EtaKind.Absolute ? Math.Max(tr.TimeSec, prevStart) : tr.TimeSec) + delay;
                                kind = EtaKind.Absolute;
                                break;
                            case TriggerKind.Flare:
                            {
                                double? fired = _flareAt[(int)c.Side, (int)tr.Color];
                                if (fired.HasValue && kind == EtaKind.Absolute)
                                    start = Math.Max(fired.Value, prevStart) + delay;
                                else
                                {
                                    kind = EtaKind.AfterFlare;
                                    color = tr.Color;
                                    start = delay;
                                }
                                break;
                            }
                            default:
                                kind = EtaKind.Unknown;
                                break;
                        }
                    }
                    c.Position = pos;
                    var pv = Preview(st.Order);
                    double travel = 0;
                    if (!pv.Stays)
                        for (int i = 0; i + 1 < pv.Path.Count; i++)
                        {
                            var a = pv.Path[i];
                            var b = pv.Path[i + 1];
                            var mid = (a + b) * 0.5;
                            double mv = _map.Contains(mid) ? _map.MoveMult(_map.TerrainAt(mid)) : 1.0;
                            if (mv <= 0) mv = _map.MoveMult(TerrainMap.Ford);
                            travel += Vec2.Distance(a, b) / (_p.Movement.SpeedMps * mv);
                        }
                    var sp = new SheetStagePreview { Preview = pv, Kind = kind, Color = color, Start = start, Arrive = start + travel };
                    result.Add(sp);
                    prevStart = start;
                    prevArrive = sp.Arrive;
                    prevType = st.Order.Type;
                    if (!pv.Stays) pos = pv.Goal;
                }
            }
            finally
            {
                c.Position = savedPos;
                c.Facing = savedFacing;
            }
            return result;
        }
    }
}
