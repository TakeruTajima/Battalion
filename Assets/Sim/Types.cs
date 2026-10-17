namespace Battalion.Sim
{
    public enum Side { Blue, Red }

    public enum Training { Green, Regular, Veteran }

    /// <summary>中隊長の性格。P0 では保持するだけで、振る舞いには使わない。</summary>
    public enum Personality { Cautious, Calm, Reckless, Agile }

    /// <summary>撃たれた側から見た射手の方向。</summary>
    public enum Aspect { Front = 0, Flank = 1, Rear = 2 }

    /// <summary>
    /// その刻みの動き方（P1e の2）。前進：向いた方へ進む。横歩き・後ろ向き：接敵中に向きを変えずに横・後ろへ動く。
    /// 旋回中：接敵していないときに、向きを進む方角へ回しながら進む（差が 20° を超える間）。
    /// </summary>
    public enum MoveStyle { None, Forward, Sideways, Backward, Turning }

    /// <summary>
    /// 中隊の主状態。拘束（Pinned）は Normal・Shaken に重なるフラグとして <see cref="Company.Pinned"/> に持つ。
    /// Surrendered と LeftBattle は盤上から取り除かれた状態。
    /// </summary>
    public enum CompanyState { Normal, Shaken, Routed, Rallying, Surrendered, LeftBattle }

    public static class Names
    {
        public static string Of(CompanyState s)
        {
            switch (s)
            {
                case CompanyState.Normal: return "normal";
                case CompanyState.Shaken: return "shaken";
                case CompanyState.Routed: return "routed";
                case CompanyState.Rallying: return "rallying";
                case CompanyState.Surrendered: return "surrendered";
                default: return "left_battle";
            }
        }

        public static string Of(Aspect a)
        {
            switch (a)
            {
                case Aspect.Front: return "front";
                case Aspect.Flank: return "flank";
                default: return "rear";
            }
        }

        public static string Of(Side s) => s == Side.Blue ? "blue" : "red";

        public static string Of(Training t)
        {
            switch (t)
            {
                case Training.Green: return "green";
                case Training.Regular: return "regular";
                default: return "veteran";
            }
        }
    }

    /// <summary>
    /// 士気の変化の要因別累計（符号付き。減少は負、回復は正）。練度の減りやすさ・回復倍率を掛けた後の値。
    /// </summary>
    public sealed class MoraleLedger
    {
        public double Front;
        public double Flank;
        public double Rear;
        public double Artillery;
        public double Encircled;
        public double NeighborRout;
        public double UnderFire;
        public double Recovery;
        /// <summary>仕様の要因一覧にない追加分類。拘束中の後退（離脱）開始時の −10。</summary>
        public double Disengage;
        /// <summary>壊走中の味方にすり抜けられた（隊形の乱れ）。P1d。</summary>
        public double Disarray;

        public void AddAspect(Aspect a, double delta)
        {
            switch (a)
            {
                case Aspect.Front: Front += delta; break;
                case Aspect.Flank: Flank += delta; break;
                default: Rear += delta; break;
            }
        }

        public JsonLineBuilder ToJson()
        {
            return new JsonLineBuilder()
                .Add("front", Front)
                .Add("flank", Flank)
                .Add("rear", Rear)
                .Add("artillery", Artillery)
                .Add("encircled", Encircled)
                .Add("neighbor_rout", NeighborRout)
                .Add("under_fire", UnderFire)
                .Add("recovery", Recovery)
                .Add("disengage", Disengage)
                .Add("disarray", Disarray);
        }
    }
}
