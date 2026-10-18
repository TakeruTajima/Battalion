using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>信号弾の色（P2b）。各色は会戦中に1回だけ打ち上げられる。</summary>
    public enum FlareColor { Red, Green, White }

    /// <summary>次の段階へ移る条件の種類（P2b）。第1段は None。</summary>
    public enum TriggerKind { None, Complete, Time, Flare }

    /// <summary>次の段階へ移る条件（P2b）。</summary>
    public readonly struct StageTrigger : IEquatable<StageTrigger>
    {
        public readonly TriggerKind Kind;
        /// <summary>時刻の条件の時刻（ゲーム内の秒）。</summary>
        public readonly double TimeSec;
        /// <summary>信号弾の条件の色。</summary>
        public readonly FlareColor Color;

        StageTrigger(TriggerKind kind, double timeSec, FlareColor color)
        {
            Kind = kind;
            TimeSec = timeSec;
            Color = color;
        }

        public static StageTrigger None => default;
        public static StageTrigger Complete => new StageTrigger(TriggerKind.Complete, 0, default);
        public static StageTrigger At(double timeSec) => new StageTrigger(TriggerKind.Time, timeSec, default);
        public static StageTrigger OnFlare(FlareColor color) => new StageTrigger(TriggerKind.Flare, 0, color);

        public bool Equals(StageTrigger o) => Kind == o.Kind && TimeSec.Equals(o.TimeSec) && Color == o.Color;
        public override bool Equals(object obj) => obj is StageTrigger o && Equals(o);
        public override int GetHashCode() => ((int)Kind * 397) ^ TimeSec.GetHashCode() ^ ((int)Color << 8);

        public static string FlareName(FlareColor c) => c == FlareColor.Red ? "red" : c == FlareColor.Green ? "green" : "white";

        /// <summary>ログに出す名前（complete／time／flare_green など）。</summary>
        public string LogName()
        {
            switch (Kind)
            {
                case TriggerKind.Complete: return "complete";
                case TriggerKind.Time: return "time";
                case TriggerKind.Flare: return "flare_" + FlareName(Color);
                default: return "none";
            }
        }
    }

    /// <summary>
    /// 裁量（P2c）：任せる（性格の独断を許す。初めの値）／厳命（独断しない。命令の字面どおり）。
    /// </summary>
    public enum Discretion { Delegate, Strict }

    /// <summary>命令書の1段（P2b）。今の命令1つと、この段へ移る条件（第1段は条件なし）と、裁量（P2c）。</summary>
    public sealed class Stage
    {
        public Order Order { get; }
        public StageTrigger Trigger { get; }
        public Discretion Discretion { get; }

        public Stage(Order order, StageTrigger trigger, Discretion discretion = Discretion.Delegate)
        {
            Order = order;
            Trigger = trigger;
            Discretion = discretion;
        }

        /// <summary>命令だけ替えた段（条件・裁量はそのまま）。</summary>
        public Stage With(Order order) => new Stage(order, Trigger, Discretion);

        /// <summary>条件だけ替えた段（命令・裁量はそのまま）。</summary>
        public Stage With(StageTrigger trigger) => new Stage(Order, trigger, Discretion);

        /// <summary>裁量だけ替えた段（命令・条件はそのまま）。</summary>
        public Stage With(Discretion discretion) => new Stage(Order, Trigger, discretion);
    }

    /// <summary>
    /// 命令書（P2b）：1〜3段の命令。1人の伝令がまとめて運び、届いたら中隊の命令書を段ごと全て置き換える。
    /// 作ったら変えない（直すときは作り直して差し替える）。
    /// </summary>
    public sealed class OrderSheet
    {
        readonly List<Stage> _stages;

        public IReadOnlyList<Stage> Stages => _stages;
        public int CompanyId => _stages[0].Order.CompanyId;
        public Order First => _stages[0].Order;

        public OrderSheet(IEnumerable<Stage> stages)
        {
            _stages = new List<Stage>(stages);
            if (_stages.Count == 0) throw new ArgumentException("命令書に段がない");
        }

        /// <summary>1段だけの命令書（今までの1つの命令）。</summary>
        public static OrderSheet Single(Order order) => new OrderSheet(new[] { new Stage(order, StageTrigger.None) });

        /// <summary>保持と拘束は終わらない（「完了したら」を次の段の条件に選べない）。</summary>
        public static bool Completes(OrderType t) => t != OrderType.Hold && t != OrderType.Pin;

        /// <summary>
        /// 段ごとに、発令できない理由（なければ null）。第1段に条件がある、第2段・第3段に条件がない、
        /// 保持・拘束の後に「完了したら」、段の数が多すぎる、宛先の中隊が段で違う、
        /// 使用済みの色の信号弾が条件（P2b.1。上がったことが残るので、届いた瞬間に満たしてしまう）。
        /// </summary>
        public static string[] StageProblems(IReadOnlyList<Stage> stages, int maxStages, Func<FlareColor, bool> flareUsed = null)
        {
            var problems = new string[stages.Count];
            for (int k = 0; k < stages.Count; k++)
            {
                var s = stages[k];
                if (k >= maxStages) problems[k] = $"命令書は {maxStages} 段まで";
                else if (s.Order == null) problems[k] = "命令がない";
                else if (k == 0 && s.Trigger.Kind != TriggerKind.None) problems[k] = "第1段には移る条件を付けない";
                else if (k > 0 && s.Trigger.Kind == TriggerKind.None) problems[k] = $"第{k + 1}段に移る条件がない";
                else if (k > 0 && s.Trigger.Kind == TriggerKind.Complete && stages[k - 1].Order != null
                         && !Completes(stages[k - 1].Order.Type))
                    problems[k] = $"{(stages[k - 1].Order.Type == OrderType.Hold ? "保持" : "拘束")}は終わらないので「完了したら」は選べない";
                else if (k > 0 && stages[0].Order != null && s.Order.CompanyId != stages[0].Order.CompanyId)
                    problems[k] = "宛先の中隊が段で違う";
                else if (k > 0 && s.Trigger.Kind == TriggerKind.Flare && flareUsed != null && flareUsed(s.Trigger.Color))
                    problems[k] = $"信号弾 {FlareJa(s.Trigger.Color)}は使用済み（届いた瞬間に満たしてしまう）";
            }
            return problems;
        }

        /// <summary>発令できるか（全ての段に問題がない）。</summary>
        public static bool IsValid(IReadOnlyList<Stage> stages, int maxStages, Func<FlareColor, bool> flareUsed = null)
        {
            if (stages.Count == 0) return false;
            foreach (var p in StageProblems(stages, maxStages, flareUsed))
                if (p != null) return false;
            return true;
        }

        static string FlareJa(FlareColor c) => c == FlareColor.Red ? "赤" : c == FlareColor.Green ? "緑" : "白";
    }
}
