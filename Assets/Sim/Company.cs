namespace Battalion.Sim
{
    /// <summary>中隊。最小単位。位置は矩形（正面幅×奥行）の中心。</summary>
    public sealed class Company
    {
        public int Id { get; }
        public string Name { get; }
        public Side Side { get; }
        public Training Training { get; }
        public Personality Personality { get; }
        public int InitialStrength { get; }

        public int Strength;
        public double Morale;
        public Vec2 Position;
        /// <summary>向き（ラジアン、0 が東、反時計回り）。(-π, π] に正規化して持つ。</summary>
        public double Facing;
        public CompanyState State = CompanyState.Normal;

        /// <summary>拘束：敵が 150m 以内からこの中隊を射撃している（直前の刻みの結果）。</summary>
        public bool Pinned;
        /// <summary>包囲状態（直前の刻みの結果）。退路の見積もりで、どの方位にも逃げ道がない。</summary>
        public bool Encircled;
        /// <summary>直近の退路の見積もりで、逃げ道がなかったか（見積もりの間隔の間は保持する）。</summary>
        public bool NoEscape;
        /// <summary>次に退路を見積もる時刻（刻みの開始時刻、秒）。</summary>
        public double NextEscapeCheckTime = double.NegativeInfinity;
        /// <summary>直近の見積もりで最も損害の少ない方位（ラジアン）。壊走時に逃げる方向。まだ見積もっていなければ null。</summary>
        public double? FleeDirection;
        /// <summary>直近の見積もりの最小の損害割合（ログ用）。</summary>
        public double EscapeLoss;
        /// <summary>絶望（退路がないと分かった瞬間の −15）を受けたか。会戦中に一度だけ。</summary>
        public bool DespairTaken;
        /// <summary>
        /// 塹壕・村の中にいる（籠城。包囲の毎分の低下が半分）。毎刻み、中心のマスが村か塹壕（基本の地形・構築した塹壕）かで決める。
        /// 塹壕の被弾倍率と違い、動いているかどうかは問わない。
        /// </summary>
        public bool Fortified;
        /// <summary>拘束中に後退を始めた（離脱中）。後退の命令が終わるまで被弾×1.3。</summary>
        public bool Disengaging;

        public Order CurrentOrder;
        /// <summary>前進・後退の目的地（攻撃では発起点）に着いたか。</summary>
        public bool Arrived;

        /// <summary>移動に使う点の列（最後が目的地）と、次に向かう点の添字。</summary>
        public System.Collections.Generic.List<Vec2> Path;
        public int PathIndex;
        /// <summary>今の移動の目的地（目的地が近すぎてずらした後の点。攻撃では発起点）。</summary>
        public Vec2 MoveGoal;
        /// <summary>次に経路を引き直す時刻（秒）。</summary>
        public double NextReplanTime;
        /// <summary>後退で、向きを変えずに後ろ向きに下がっているか。</summary>
        public bool Backward;
        /// <summary>攻撃で選んだ方向と側（+1 は目標の左、−1 は右、0 は正面・背面）。</summary>
        public AttackAspect AttackChosenAspect;
        public int AttackSide;
        /// <summary>経路の脅威の重みを性格の値から置き換える（予備隊の台本で 0）。null なら性格の値。</summary>
        public double? ThreatWeightOverride;

        /// <summary>その刻みに位置が変わったか（塹壕の被弾倍率と陣地構築に使う）。</summary>
        public bool MovedThisTick;
        /// <summary>陣地構築の進捗（秒）。動くと 0 に戻る。</summary>
        public double EntrenchProgress;

        /// <summary>直前の刻みで狙った目標の ID（-1 はなし）。</summary>
        public int TargetId = -1;

        public readonly MoraleLedger Ledger = new MoraleLedger();
        public bool FlankShockTaken;
        public bool RearShockTaken;

        /// <summary>最後に狙われた刻みの終了時刻（秒）。</summary>
        public double LastTargetedTime = double.NegativeInfinity;

        /// <summary>壊走中、400m 以内に敵がいない状態が続いている秒数。</summary>
        public double RallyClearSec;

        public Company(int id, string name, Side side, Training training, Personality personality,
            int strength, double morale, Vec2 position, double facingRad)
        {
            Id = id;
            Name = name;
            Side = side;
            Training = training;
            Personality = personality;
            InitialStrength = strength;
            Strength = strength;
            Morale = morale;
            Position = position;
            Facing = Angles.Wrap(facingRad);
            CurrentOrder = Order.Hold(id);
            Arrived = true;
        }

        /// <summary>盤上にいる（降伏・離脱していない）。</summary>
        public bool OnBoard => State != CompanyState.Surrendered && State != CompanyState.LeftBattle;

        /// <summary>指揮下にあり命令を受け付ける（正常・動揺）。</summary>
        public bool UnderCommand => State == CompanyState.Normal || State == CompanyState.Shaken;

        public double LossRate => 1.0 - (double)Strength / InitialStrength;
    }
}
