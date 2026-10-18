using System;

namespace Battalion.Sim
{
    // P2b：段階つき命令と信号弾（仕様「P2b：段階つき命令と信号弾（確定）」）。
    // 中隊は実行中の命令書と今の段を持ち、今の段が始まった後から次の段の条件（完了・時刻・信号弾）を調べる。
    // 条件を満たしたら、練度の遅れの後に次の段を始める。
    public sealed partial class Simulation
    {
        readonly double?[,] _flareAt = new double?[2, 3];

        /// <summary>
        /// 命令書を伝令を使わずに渡す（会戦開始前の命令、本部のない場面）。次の刻みの最初に第1段を始める（練度の遅れなし）。
        /// 古い命令書は段ごと全て置き換える。
        /// </summary>
        public void IssueSheet(OrderSheet sheet)
        {
            var c = _companies[sheet.CompanyId];
            // 出した順を進める（後から着いた古い伝令の命令書を捨てるため）。開始待ちの段はこの命令書に置き換わる
            c.ReceivedSeq = ++_orderSeq;
            c.ActiveSheet = sheet;
            c.StageIndex = 0;
            c.StageStarted = true;
            c.StageCompleted = false;
            c.StartWaitOrder = null;
            _pending.Add(sheet.First);
        }

        // ---------------------------------------------------------------- 信号弾

        /// <summary>
        /// 信号弾を打ち上げる（P2b）。各色は会戦中に1回だけ。打ち上げた陣営の全中隊に即座に届く（判断枠を使わない）。
        /// その色をすでに使っていれば false。
        /// </summary>
        public bool FireFlare(Side side, FlareColor color)
        {
            if (_flareAt[(int)side, (int)color].HasValue) return false;
            _flareAt[(int)side, (int)color] = Time;
            if (_log.Enabled)
                _log.Write(new JsonLineBuilder().Add("t", Time).Add("type", "flare").Add("side", SideName(side))
                    .Add("color", StageTrigger.FlareName(color)).ToString());
            return true;
        }

        /// <summary>その色の信号弾を上げた時刻（まだなら null）。</summary>
        public double? FlareTime(Side side, FlareColor color) => _flareAt[(int)side, (int)color];

        // ---------------------------------------------------------------- 段の進行

        /// <summary>
        /// 段の進行（刻みの終わり、状態遷移の後）。指揮下の中隊について、今の段が始まっていて、次の段があり、開始待ちでなければ、
        /// 次の段の条件を調べる。満たしたら、練度の遅れの後に次の段を始める（開始は <see cref="UpdateRunners"/> の開始待ちで）。
        /// </summary>
        void UpdateStages(double t)
        {
            foreach (var c in _companies)
            {
                var sheet = c.ActiveSheet;
                if (sheet == null || !c.OnBoard || !c.UnderCommand || !c.StageStarted || c.StartWaitOrder != null) continue;
                if (c.StageIndex + 1 >= sheet.Stages.Count) continue;
                var next = sheet.Stages[c.StageIndex + 1];
                if (!TriggerMet(c, sheet.Stages[c.StageIndex].Order, next.Trigger, t)) continue;

                c.StageIndex++;
                c.StageStarted = false;
                c.StageCompleted = false;
                c.StartWaitOrder = next.Order;
                c.StartWaitAt = t + _p.Of(c.Training).OrderStartSec;
                if (_log.Enabled)
                    _log.Write(Ev(t, "stage_advance", c).Add("side", SideName(c.Side)).Add("stage", c.StageIndex + 1).Add("of", sheet.Stages.Count)
                        .Add("reason", next.Trigger.LogName()).ToString());
            }
        }

        bool TriggerMet(Company c, Order current, StageTrigger trigger, double t)
        {
            switch (trigger.Kind)
            {
                case TriggerKind.Complete: return StageComplete(c, current);
                case TriggerKind.Time: return t >= trigger.TimeSec - 1e-9;
                // 信号弾は上がったことが残る（命令書が届く前・今の段が始まる前に上がっていても満たす）
                case TriggerKind.Flare: return _flareAt[(int)c.Side, (int)trigger.Color].HasValue;
                default: return false;
            }
        }

        /// <summary>
        /// 今の段が完了したか：前進・後退は着いた（動揺で保持に変わった・阻まれて止まった場合は完了しない）、
        /// 攻撃は目標が指揮下でなくなった、陣地構築は陣地が完成した。保持と拘束は完了しない。
        /// </summary>
        bool StageComplete(Company c, Order stageOrder)
        {
            switch (stageOrder.Type)
            {
                case OrderType.Advance:
                case OrderType.Retreat:
                    return ReferenceEquals(c.CurrentOrder, stageOrder) && c.Arrived && !c.BlockedByEnemy;
                case OrderType.Attack:
                    return c.StageCompleted;
                case OrderType.Entrench:
                    return ReferenceEquals(c.CurrentOrder, stageOrder) && c.EntrenchProgress >= _p.MapRules.EntrenchSec;
                default:
                    return false;
            }
        }
    }
}
