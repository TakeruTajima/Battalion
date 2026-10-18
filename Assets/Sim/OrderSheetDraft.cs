using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>
    /// 起案中の命令書（P2b。本部の机の上にある1中隊分）。段の追加・削除・入れ替え、命令と移る条件の変更ができる。
    /// 表示（View）が持ち、発令のときに <see cref="ToSheet"/> で命令書にして Sim に渡す。会戦の状態には関わらない。
    /// </summary>
    public sealed class OrderSheetDraft
    {
        public readonly int CompanyId;
        readonly List<Stage> _stages = new List<Stage>();

        public IReadOnlyList<Stage> Stages => _stages;

        /// <summary>命令を書き込む段（地図のクリックや命令のキーは、この段の命令を置き換える）。</summary>
        public int Selected;

        /// <summary>準備中の命令書を直しているなら、その伝令（発令で差し替える）。新しく出すなら null。</summary>
        public int? ReviseRunnerId;

        public OrderSheetDraft(int companyId) => CompanyId = companyId;

        /// <summary>実行中・送付中の命令書の写し（<paramref name="fromStage"/> の段から後。写した最初の段は条件を外す）。</summary>
        public static OrderSheetDraft CopyOf(OrderSheet sheet, int fromStage = 0)
        {
            var d = new OrderSheetDraft(sheet.CompanyId);
            for (int k = fromStage; k < sheet.Stages.Count; k++)
                d._stages.Add(new Stage(sheet.Stages[k].Order, k == fromStage ? StageTrigger.None : sheet.Stages[k].Trigger));
            return d;
        }

        public int Count => _stages.Count;

        /// <summary>
        /// 段を足す（<paramref name="max"/> 段まで）。足した段が書き込む段になる。第2段以降の条件は、前の段が終わる命令なら「完了したら」、
        /// そうでなければ未設定（選ぶまで発令できない）。足せたら true。
        /// </summary>
        public bool Add(Order order, int max)
        {
            if (_stages.Count >= max) return false;
            var trigger = StageTrigger.None;
            if (_stages.Count > 0)
            {
                var prev = _stages[_stages.Count - 1].Order;
                if (prev != null && OrderSheet.Completes(prev.Type)) trigger = StageTrigger.Complete;
            }
            _stages.Add(new Stage(order, trigger));
            Selected = _stages.Count - 1;
            return true;
        }

        /// <summary>段の命令を置き換える（条件はそのまま）。</summary>
        public void SetOrder(int index, Order order)
        {
            if (index < 0 || index >= _stages.Count) return;
            _stages[index] = new Stage(order, _stages[index].Trigger);
        }

        /// <summary>段の移る条件を変える（第1段には付けない）。</summary>
        public void SetTrigger(int index, StageTrigger trigger)
        {
            if (index <= 0 || index >= _stages.Count) return;
            _stages[index] = new Stage(_stages[index].Order, trigger);
        }

        /// <summary>段を消す。第1段を消したら、新しい第1段の条件は外す。</summary>
        public void Remove(int index)
        {
            if (index < 0 || index >= _stages.Count) return;
            _stages.RemoveAt(index);
            FixFirst();
            if (Selected >= _stages.Count) Selected = _stages.Count - 1;
            if (Selected < 0) Selected = 0;
        }

        /// <summary>
        /// 段を1つ上へ（<paramref name="index"/> と前の段を入れ替える）。第1段になった段の条件は外し、
        /// 第1段から下がった段は条件なしにする（条件を付けるまで発令できない）。
        /// </summary>
        public void MoveUp(int index)
        {
            if (index <= 0 || index >= _stages.Count) return;
            var a = _stages[index - 1];
            var b = _stages[index];
            bool topSwap = index - 1 == 0;
            _stages[index - 1] = topSwap ? new Stage(b.Order, StageTrigger.None) : b;
            _stages[index] = topSwap ? new Stage(a.Order, StageTrigger.None) : a;
            if (Selected == index) Selected = index - 1;
            else if (Selected == index - 1) Selected = index;
        }

        public void MoveDown(int index) => MoveUp(index + 1);

        void FixFirst()
        {
            if (_stages.Count > 0 && _stages[0].Trigger.Kind != TriggerKind.None)
                _stages[0] = new Stage(_stages[0].Order, StageTrigger.None);
        }

        /// <summary>段ごとの、発令できない理由（なければ null）。</summary>
        public string[] Problems(int maxStages) => OrderSheet.StageProblems(_stages, maxStages);

        public bool IsValid(int maxStages) => OrderSheet.IsValid(_stages, maxStages);

        /// <summary>命令書にする（発令できなければ null）。</summary>
        public OrderSheet ToSheet(int maxStages) => IsValid(maxStages) ? new OrderSheet(_stages) : null;
    }
}
