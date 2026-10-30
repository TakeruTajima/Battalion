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
        /// <summary>
        /// 攻撃：選んだ方角（目標の中心から見た発起点の方角、地図の方角）と、選んだときの結果の向き（正面・側面・背面）。
        /// </summary>
        public AttackAspect AttackChosenAspect;
        public double AttackBearing;
        /// <summary>経路の脅威の重みを性格の値から置き換える（予備隊の台本で 0）。null なら性格の値。</summary>
        public double? ThreatWeightOverride;

        // ---- P1d：行動の規則
        /// <summary>攻撃で発起点に着いた後：目標の中心から見た自分の方角（地図の方角）と、そのときの目標の中心。</summary>
        public bool AttackFixed;
        public double AttackFixedBearing;
        public Vec2 AttackFixedTargetPos;
        /// <summary>拘束：命令を受けたときの、目標の中心から見た自分の方角。</summary>
        public double PinBearing;
        /// <summary>追撃中か。追っている中隊、追撃を始めた地点、限界の距離、次に経路を引き直す時刻。</summary>
        public bool Pursuing;
        public int PursuitTargetId = -1;
        public Vec2 PursuitStart;
        public double PursuitLimitM;
        public double NextPursuitPlan;
        /// <summary>向き直り：向き直った（向き直っている）向き。新しい命令が出るまで保つ。null なら向き直っていない。</summary>
        public double? FacingOverride;
        /// <summary>向き直りの待ちを始めた時刻（null なら待っていない）、最後に側面・背面から撃たれた時刻と、撃ってきた中隊。</summary>
        public double? TurnWaitStart;
        public double LastFlankHitTime = double.NegativeInfinity;
        public int LastFlankShooter = -1;
        /// <summary>隊形の乱れ（壊走中の味方にすり抜けられた）が続く時刻。すり抜けられた壊走中の中隊。</summary>
        public double DisarrayUntil = double.NegativeInfinity;
        public readonly System.Collections.Generic.HashSet<int> PassedBy = new System.Collections.Generic.HashSet<int>();
        /// <summary>今の逃げる方位の添字（揺れ止めに使う。−1 はまだない）。</summary>
        public int FleeDirIndex = -1;

        // ---- P1e：動くときの向きと隊列の超越
        /// <summary>接敵中か（400m 以内に視線の通る指揮下の敵がいる）。毎刻み、移動の前に、経路に沿って動く中隊だけ判定する。</summary>
        public bool InContact;
        /// <summary>接敵中（R3。止まっている中隊も含めて判定する。旋回の速さに使う）。</summary>
        public bool Engaged;
        /// <summary>直前の刻みに、接敵中にその場で movement.turning_slow_deg を超えて向きを変えていた（R3。R1 の動いている扱い）。</summary>
        public bool TurningAsMoving;
        /// <summary>自分の動き（押し離しを除く）で位置が変わっていない秒数（R3。線を張っているかの判定に使う）。</summary>
        public double StillSec;
        /// <summary>その刻みの動き方（表示用）。</summary>
        public MoveStyle MoveStyle;
        /// <summary>超越中の、止まっている味方の ID（押し離しをしない相手）。毎刻み、移動の前に判定する。</summary>
        public readonly System.Collections.Generic.HashSet<int> Passing = new System.Collections.Generic.HashSet<int>();
        // ---- P2a：伝令
        /// <summary>受けた命令の出した順（これより古い命令の伝令が後から着いたら捨てる）。</summary>
        public long ReceivedSeq;
        /// <summary>届いて（または次の段へ移って）、練度の遅れの後に始まる段の命令（null ならなし）と、始まる時刻。</summary>
        public Order StartWaitOrder;
        public double StartWaitAt;

        // ---- P2b：段階つき命令
        /// <summary>実行中の命令書（null ならなし）と、今の段の添字（0 から）。</summary>
        public OrderSheet ActiveSheet;
        public int StageIndex;
        /// <summary>今の段が始まったか（開始待ちの間は false。次の段の条件は始まった後から調べる）。</summary>
        public bool StageStarted;
        /// <summary>今の段の攻撃が完了したか（目標が指揮下でなくなった）。</summary>
        public bool StageCompleted;
        /// <summary>新しい命令書が届いた時点で命令待ちだったか（表示用。その命令書が始まるまで命令待ちのまま。P2b.1）。</summary>
        public bool AwaitingWhenDelivered;

        // ---- P2b.2：壊走・動揺と命令書
        /// <summary>壊走中・再集結中に届いて預かっている命令書（指揮下に戻ったら練度の遅れの後に始める）。null ならなし。</summary>
        public OrderSheet HeldSheet;
        /// <summary>動揺で今の段を止めているか（その場で保持）と、止めた段の命令（正常に戻ったら同じ目的地で再開する）。</summary>
        public bool ShakenHalt;
        public Order HaltedOrder;

        // ---- P1g：目標が見えないときの判断
        /// <summary>目標が見えないため、見える位置を探して動いているか（着いたら false）。</summary>
        public bool SeekingSight;
        /// <summary>拘束：見える位置を探して決めた、目標からの距離（null なら拘束の距離。遠い 350m／近い 125m）。</summary>
        public double? PinStandoffM;

        /// <summary>その刻みに、移動の命令中なのに敵の矩形の手前で止められて全く進めなかったか（P1e.1。着いた扱いにはしない）。</summary>
        public bool BlockedByEnemy;
        /// <summary>止まっている味方の矩形と重なっているか（超越の ×0.5）。毎刻み、移動の前に判定する。</summary>
        public bool PassOverlap;

        /// <summary>その刻みに位置が変わったか（塹壕の被弾倍率と陣地構築に使う）。</summary>
        public bool MovedThisTick;
        /// <summary>陣地構築の進捗（秒）。動くと 0 に戻る。</summary>
        public double EntrenchProgress;

        // ---- P2c：中隊長の自律行動
        /// <summary>持ち場：中隊が今守っている地点。全ての中隊長の自律行動はここから 100m を出ない。</summary>
        public Vec2 PostPoint;
        /// <summary>持ち場が決まったか（移動の命令は、着いて止まった位置で決まる）。</summary>
        public bool PostSettled = true;
        /// <summary>今の段の裁量（段の命令が始まった刻みに写す。命令書を終えた後も最後の段のまま）。</summary>
        public Discretion Discretion;
        /// <summary>自律行動の小さな動き（命令は変えずに、目的地と経路だけ引いて動く）。</summary>
        public AutoMove AutoMove;
        public double AutoMoveSince;
        /// <summary>撃てる目標がないときに向く向き（null なら命令の向き）。</summary>
        public double? AutoFacing;
        /// <summary>自分を撃った敵ごとの、撃たれた刻みの時刻（直近 30秒。向く相手の順位に使う。P2c.1）。</summary>
        public readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.Queue<double>> ShotBy =
            new System.Collections.Generic.Dictionary<int, System.Collections.Generic.Queue<double>>();
        /// <summary>開けた所で、止まって撃たれ続けている始まりの時刻（null なら撃たれ続けていない）。</summary>
        public double? UnderFireSince;
        /// <summary>物陰へ寄る：練度の遅れの待ちを始めた時刻、寄る先、独断（慎重の広い範囲・後ろ向き）か、次に探す時刻。</summary>
        public double? CoverWaitStart;
        public Vec2 CoverGoal;
        public bool CoverIndependent;
        public double NextCoverSearch = double.NegativeInfinity;
        /// <summary>最後に動いた時刻と、浅い掩体の進捗（秒）。動くと 0 に戻る。</summary>
        public double StillSince;
        public double ScrapeProgress;
        /// <summary>浅い掩体の向き（掘り始めたときの中隊の向き。R2）。</summary>
        public double? ScrapeFacing;
        /// <summary>独断の種類と、独断を始める前の持ち場（限度はここから測る）。</summary>
        public IndependentAct Independent;
        public Vec2 IndependentOrigin;
        /// <summary>機敏の回り込み：練度の遅れの後に始める攻撃の命令と時刻、次に見回る時刻。</summary>
        public Order IndependentWaitOrder;
        public double IndependentWaitAt;
        public double NextAgileCheck = double.NegativeInfinity;
        /// <summary>独断を限度で止めた（命令待ち。新しい命令が始まるまで）。</summary>
        public bool IndependentStopped;
        /// <summary>攻撃の目標が指揮下でなくなった刻みの、目標の中心（崩した敵の陣地に入るのに使う）。</summary>
        public Vec2? BrokenTargetPos;
        /// <summary>崩した敵の陣地に入る：経路が引けなかった（逃げる敵がまだ近い等）ときの入る先と、次に引き直す時刻。</summary>
        public Vec2? EnterGoal;
        public double NextEnterTry;

        // ---- R1：制圧
        /// <summary>その刻みに受けた火力の量（自分を撃つ全ての敵の、損害の式から守りを除いた値の和。毎分）。</summary>
        public double IncomingFirePerMin;
        /// <summary>その刻みに、火力の量が一番多かった射手の ID（−1 はなし。伏せるときに向く相手）。</summary>
        public int MainShooterId = -1;
        /// <summary>制圧下（撃たれて頭を下げている）か。</summary>
        public bool Suppressed;
        /// <summary>制圧の始まり・終わりの条件が続いている秒数（揺れ止め）。</summary>
        public double SuppressOnSec;
        public double SuppressOffSec;

        // ---- E1：赤の守り方、C12：占領の乱れ
        /// <summary>守りの計画での今の役目（前線は下がって着くと守備になる）。</summary>
        public DefenseRole Role;
        /// <summary>前線の後退（規則1）をした（会戦中に1回だけ）。</summary>
        public bool FellBack;
        /// <summary>次に守りの判断をする時刻（秒）。</summary>
        public double NextDefenseCheck;
        /// <summary>陣地の向きの正面の弧の外から撃たれた最後の刻みの時刻（迂回の判定）。</summary>
        public double LastOffArcFireTime = double.NegativeInfinity;
        /// <summary>予備の状態（規則2）。</summary>
        public ReserveState Reserve;
        /// <summary>突撃（即座の逆襲・奪回の攻撃）の目標の ID（−1 はなし）と、その命令書、次に目標の位置へ引き直す時刻。</summary>
        public int AssaultTargetId = -1;
        public OrderSheet AssaultSheet;
        public double NextAssaultReplan;
        /// <summary>突撃が即座の逆襲（true）か奪回の攻撃（false）か。追う限りが違う（直しの後に決めたこと 2）。</summary>
        public bool AssaultIsCounter;
        /// <summary>止められた突撃の判定（3回目の直しの3）：目標との距離の、これまでで一番近い値と、その時刻。</summary>
        public double AssaultBestDist, AssaultBestTime;
        /// <summary>進めずにやめた逆襲の目標（−1 はなし）と、その目標へ出直さない時刻まで（4回目の直しの1）。</summary>
        public int CounterBannedTargetId = -1;
        public double CounterBannedUntil;
        /// <summary>戻る位置へ戻るはずが、最後の守り手なのでその場で保持している（5回目の直しの1）。ほかに守り手が来たら戻る。</summary>
        public bool PendingReturn;
        /// <summary>会戦開始時の位置（E1 の規則1：前線の後退は、陣地（初めの位置の近く）にいる間だけ判断する）。</summary>
        public Vec2 InitialPosition;
        /// <summary>占領の乱れが続く時刻（C12）。</summary>
        public double OccupationDisarrayUntil = double.NegativeInfinity;

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
            PostPoint = position;
            InitialPosition = position;
        }

        /// <summary>盤上にいる（降伏・離脱していない）。</summary>
        public bool OnBoard => State != CompanyState.Surrendered && State != CompanyState.LeftBattle;

        /// <summary>指揮下にあり命令を受け付ける（正常・動揺）。</summary>
        public bool UnderCommand => State == CompanyState.Normal || State == CompanyState.Shaken;

        public double LossRate => 1.0 - (double)Strength / InitialStrength;
    }
}
