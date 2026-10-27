using System;
using System.Collections.Generic;
using System.IO;

namespace Battalion.Sim
{
    /// <summary>
    /// sim_params.json の内容。キーは docs/reference/sim_params.initial.json と同じ。
    /// "_" で始まるキー（注記）は読み飛ばす。
    /// </summary>
    public sealed class SimParams
    {
        public double TickSec;
        public FireParams Fire = new FireParams();
        public MoraleParams Morale = new MoraleParams();
        public readonly Dictionary<Training, TrainingParams> TrainingTable = new Dictionary<Training, TrainingParams>();
        public EncirclementParams Encirclement = new EncirclementParams();
        public RoutParams Rout = new RoutParams();
        public PinningParams Pinning = new PinningParams();
        public CloseCombatParams CloseCombat = new CloseCombatParams();
        public MovementParams Movement = new MovementParams();
        public TerrainParams Terrain = new TerrainParams();
        public SightParams Sight = new SightParams();
        public MapRulesParams MapRules = new MapRulesParams();
        public BehaviorParams Behavior = new BehaviorParams();
        public RunnersParams Runners = new RunnersParams();
        public OrdersParams Orders = new OrdersParams();
        public AutonomyParams Autonomy = new AutonomyParams();
        public CouriersParams Couriers = new CouriersParams();
        public SuppressionParams Suppression = new SuppressionParams();
        public DefenseParams Defense = new DefenseParams();
        public OccupationParams Occupation = new OccupationParams();

        /// <summary>E1 の赤の守り方（"defense"）：前線の後退・即座の逆襲・奪回。</summary>
        public sealed class DefenseParams
        {
            /// <summary>false なら今までの赤（受け身、予備は台本で一度だけ逆襲。伝令あり）。</summary>
            public bool Enabled;
            /// <summary>前線・予備の中隊が判断する間隔（秒）。</summary>
            public double CheckSec;
            /// <summary>迂回：見えている青がこの距離以内で、陣地の線より <see cref="BypassBehindM"/> 以上後ろにいる。</summary>
            public double BypassRadiusM;
            public double BypassBehindM;
            /// <summary>迂回：直近この秒数に、陣地の正面の弧の外から撃たれた。</summary>
            public double FlankFireMemorySec;
            /// <summary>拘束：見えている青が、正面の弧の中でこの距離以内にいる。</summary>
            public double PinRadiusM;
            /// <summary>予備の余力：損害が初めの兵力のこの割合未満（士気は morale.shaken_threshold より上）。</summary>
            public double ReserveSpentLoss;
            /// <summary>赤の本部が奪回を判断する間隔（秒）。</summary>
            public double HqEvalSec;
            /// <summary>奪回の出し方："assemble"（集めてから出す）か "immediate"（小出し。比べ用）。</summary>
            public bool RetakeAssemble;
            public double AssembleRadiusM;
            public int AssembleMinCompanies;
            public double AssembleMaxWaitSec;
            /// <summary>即座の逆襲の目標の孤立：中心どうしこの距離以内のほかの指揮下の敵が <see cref="CounterMaxSupport"/> 個以下。</summary>
            public double CounterIsolationRadiusM;
            public int CounterMaxSupport;
            /// <summary>止められた突撃（3回目の直しの3）：この秒数の間に、目標との距離が <see cref="AssaultStallProgressM"/> 以上縮まなければやめる。</summary>
            public double AssaultStallSec;
            public double AssaultStallProgressM;
        }

        /// <summary>C12 の占領の乱れ（"occupation"。両軍）：敵の側が持ち主の陣地に入った中隊は、しばらく撃つ力が落ち、守りの用意を始めない。</summary>
        public sealed class OccupationParams
        {
            public bool Enabled;
            public double DisarraySec;
            public double DisarrayFireMult;
        }

        /// <summary>
        /// R1 の制圧（"suppression"）。受けている火力の量（自分を撃つ全ての敵の、損害の式から守りを除いた値の和。毎分）が
        /// しきい値以上の状態が <see cref="OnsetSec"/> 続いたら制圧下、しきい値 × <see cref="ReleaseRatio"/> 未満が
        /// <see cref="ReleaseSec"/> 続いたら解ける。
        /// </summary>
        public sealed class SuppressionParams
        {
            /// <summary>動いている中隊（移動を伴う命令でまだ着いていない、自律行動の移動の途中）のしきい値。性格の倍率を掛ける。</summary>
            public double ThresholdMovingPerMin;
            /// <summary>止まっている中隊のしきい値。</summary>
            public double ThresholdStationaryPerMin;
            public double OnsetSec;
            public double ReleaseRatio;
            public double ReleaseSec;
            /// <summary>制圧下の射手の撃つ力の倍率（小銃の制圧。砲撃の <see cref="FireParams.SuppressedMult"/> とは別）。</summary>
            public double FireMult;
            /// <summary>動いている間のしきい値に掛ける性格の倍率（表にない性格は 1.0）。</summary>
            public readonly Dictionary<Personality, double> MovingThresholdPersonalityMult = new Dictionary<Personality, double>();
        }

        /// <summary>P2c.2 の中隊の伝令（意見具申。"couriers"）。速さ・喪失・経路は <see cref="RunnersParams"/> を使う。</summary>
        public sealed class CouriersParams
        {
            /// <summary>中隊が伝令を出すまでの準備（秒）。</summary>
            public double PrepSec;
            /// <summary>1つの中隊が同時に出せる伝令の数（越える分は出さずに捨てる）。</summary>
            public int MaxOutPerCompany;
            /// <summary>報告・要請をまとめる間隔（前の伝令からこの秒数あける）。</summary>
            public double BatchSec;
            /// <summary>敵発見：気付いている指揮下の敵がこの距離以内に初めて入った時。</summary>
            public double DiscoverRadiusM;
            /// <summary>損害大：前の「損害大」から、初めの兵力に対してこの割合を新たに失った時（性格ごと）。</summary>
            public readonly Dictionary<Personality, double> HeavyLossStep = new Dictionary<Personality, double>();
            /// <summary>隣の崩壊：この距離以内の味方が壊走した時。</summary>
            public double NeighborRoutRadiusM;
            /// <summary>命令を請う：命令待ちがこの秒数続いた時、以後この秒数ごと。</summary>
            public double AskOrdersAfterSec;
            public double AskOrdersRepeatSec;
            /// <summary>後退の許可：士気がこの値（慎重は未満、ほかは以下）で撃たれている時。この秒数撃たれなければ状況が変わったとみなす。</summary>
            public readonly Dictionary<Personality, double> RetreatAskMorale = new Dictionary<Personality, double>();
            public double RetreatAskQuietSec;
        }

        /// <summary>P2c の中隊長の自律行動（"autonomy"）。</summary>
        public sealed class AutonomyParams
        {
            /// <summary>全ての中隊長の自律行動は、持ち場からこの距離を出ない。</summary>
            public double PostRadiusM;
            /// <summary>撃てる目標がないとき、向く相手（P2c.1）：直近この秒数に自分を撃った敵、自分から見えるこの距離以内（近い・遠い）の最も近い敵。</summary>
            public double FaceShooterMemorySec;
            public double FaceNearRadiusM;
            public double FaceEnemyRadiusM;
            /// <summary>今の正面の弧のこの距離以内に自分から見える敵がいれば向き直らない（P2c.1）。</summary>
            public double FaceFrontHoldM;
            /// <summary>被弾倍率がこれ以上の所（開けた所）でこの秒数撃たれ続けたら物陰を探す。</summary>
            public double CoverUnderFireSec;
            public double CoverOpenMult;
            /// <summary>物陰の候補：被弾倍率が今のこの倍以下のマス。</summary>
            public double CoverMultRatio;
            /// <summary>撃たれ「続けた」の途切れ：この秒数撃たれなければ数え直す。</summary>
            public double CoverFireGapSec;
            /// <summary>物陰が見つからなかったときに探し直すまで。</summary>
            public double CoverRetrySec;
            /// <summary>物陰の候補から、今撃ってきている敵の中心からこの距離以内のマスを除く。</summary>
            public double CoverMinShooterDistM;
            /// <summary>浅い掩体：この秒数動かず、この秒数撃たれていなければ掘り始め、掘り始めてこの秒数で被弾倍率がこの値になる。</summary>
            public double ScrapeStillSec;
            public double ScrapeQuietSec;
            public double ScrapeDigSec;
            public double ScrapeDamageMult;
            /// <summary>崩した敵の陣地（塹壕・村）が、確保した地点からこの距離以内なら入る。</summary>
            public double EnterPositionRadiusM;
            /// <summary>任せるのとき、前進・攻撃を拒む士気（慎重は「未満」、ほかは「以下」）。厳命は morale.shaken_threshold。</summary>
            public readonly Dictionary<Personality, double> AdvanceRefuseMorale = new Dictionary<Personality, double>();
            /// <summary>猪突：追撃の後に、この距離以内の動揺・壊走中の敵へ続けて攻めかかる。</summary>
            public double RecklessFollowupRadiusM;
            /// <summary>機敏：この距離以内の、別の味方に正面から撃たれている敵の側面へ回り込む。経路と、持ち場からの発起点の限度。</summary>
            public double AgileWatchRadiusM;
            public double AgilePathMaxM;
            public double AgilePostLimitM;
            /// <summary>機敏の見回りの間隔。</summary>
            public double AgileCheckSec;
            /// <summary>慎重（任せる）：物陰を探す範囲（後ろ向きも可）。</summary>
            public double CautiousCoverRadiusM;
        }

        /// <summary>P2b の命令書（"orders"）。</summary>
        public sealed class OrdersParams
        {
            /// <summary>命令書の段の最大数。</summary>
            public int MaxStages;
        }

        /// <summary>P2a の伝令（"runners"）。</summary>
        public sealed class RunnersParams
        {
            /// <summary>発令から伝令が出るまで（秒）。</summary>
            public double PrepSec;
            /// <summary>伝令の速さ（行きも帰りも）。</summary>
            public double SpeedMps;
            /// <summary>射界を通る間、1秒あたりに倒れる確率。</summary>
            public double LossPerSec;
            /// <summary>往復の予定時間のこの倍で、受領が来なければ判断枠を空ける。</summary>
            public double OverdueMult;
            /// <summary>判断枠（同時に外に出ている伝令の上限）。</summary>
            public int MaxOut;
            /// <summary>P2b：伝令の経路の脅威の重み（慎重と同じ）と、経路を引き直す間隔（秒）。</summary>
            public double PathThreatWeight;
            public double ReplanSec;
        }

        public TrainingParams Of(Training t) => TrainingTable[t];

        public sealed class FireParams
        {
            public double KPerMin;
            public double RangeMaxM;
            public double RangeCloseM;
            public double RangeCloseMult;
            /// <summary>添字は <see cref="Aspect"/>（正面・側面・背面）。</summary>
            public double[] AspectCasualtyMult = new double[3];
            public double FrontArcDeg;
            public double FlankArcDeg;
            public double NonFrontTargetMult;
            public double ShakenMult;
            /// <summary>砲撃で制圧された射手の撃つ力の倍率（P3）。小銃の制圧は <see cref="SuppressionParams.FireMult"/>。</summary>
            public double SuppressedMult;
            /// <summary>その刻みに実際に動いた射手の撃つ力の倍率（R1 の規則3。動きながら撃つと当たりにくい）。</summary>
            public double MovingFireMult;
        }

        public sealed class MoraleParams
        {
            public double Initial;
            public double PerPctLoss;
            public double[] AspectMoraleMult = new double[3];
            public double FirstHitShockFlank;
            public double FirstHitShockRear;
            public double UnderFirePerMin;
            public double NeighborRoutShock;
            public double NeighborRoutRadiusM;
            public double RecoveryPerMin;
            public double RecoveryQuietSec;
            public double RecoveryCap;
            public double ShakenThreshold;
        }

        public sealed class TrainingParams
        {
            public double RoutThreshold;
            public double FireMult;
            public double MoraleSensitivity;
            public double RecoveryMult;
            public double OrderStartSec;
            public double ReportErrorM;
        }

        /// <summary>
        /// 包囲（v3）：士気がしきい値以下の中隊は一定間隔で退路を見積もり、どの方位に逃げても
        /// 逃げる間に <see cref="EscapeLossThreshold"/> 以上を失う見込みなら包囲とする。
        /// </summary>
        public sealed class EncirclementParams
        {
            public double CheckWhenMoraleAtOrBelow;
            public double CheckIntervalSec;
            public int EscapeDirs;
            public double EscapeLenM;
            public double EscapeSpeedMps;
            public double EscapeStepSec;
            /// <summary>逃げる中隊が撃たれる向き（背中を見せるので背面）。</summary>
            public Aspect EscapeFireAspect;
            public double EscapeLossThreshold;
            /// <summary>退路がないと分かった瞬間に一度だけ（会戦中に1回）。</summary>
            public double DespairShock;
            public double MoralePerMin;
            /// <summary>塹壕・村の中（籠城）での毎分の低下の倍率。</summary>
            public double FortifiedMoraleMult;
        }

        public sealed class RoutParams
        {
            public double FleeSpeedMps;
            public double FleeFromRadiusM;
            public double PursuitRadiusM;
            public double PursuitCasualtyMult;
            public double RallyClearRadiusM;
            public double RallyAfterSec;
            public double RallyReturnMorale;
        }

        public sealed class PinningParams
        {
            public double RadiusM;
            public double MoveMult;
            public double DisengageMoraleHit;
            public double DisengageCasualtyMult;
        }

        public sealed class CloseCombatParams
        {
            public double RadiusM;
            /// <summary>50m 以内の距離倍率。fire.range_close_mult（150m 以内の 1.3）を置き換える。重ねない。</summary>
            public double RangeMult;
            public double MoraleMult;
        }

        public sealed class MovementParams
        {
            public double SpeedMps;
            public double TurnDegPerSec;
            public double FrontageM;
            public double DepthM;
            public int CompanyStrength;
            /// <summary>後退の命令で、向きを変えずに後ろ向きに下がるときの速度倍率。</summary>
            public double RetreatSpeedMult;
            /// <summary>味方中隊の中心どうしの最小間隔（P1a で使う）。</summary>
            public double FriendlyMinSpacingM;
            /// <summary>止まっている味方の矩形と重なっている間（隊列の超越）の速さの倍率（P1e の1）。</summary>
            public double PassThroughSpeedMult;
            /// <summary>接敵中に、向きから横（60〜120°）の方角へ動くとき（横歩き）の速さの倍率（P1e の2）。</summary>
            public double SidewaysSpeedMult;
            /// <summary>接敵していないとき、向きと進む方角の差が <see cref="TurningSlowDeg"/> を超える間（旋回中）の速さの倍率。</summary>
            public double TurningSpeedMult;
            public double TurningSlowDeg;
        }

        /// <summary>
        /// 地形の文字ごとの移動倍率・被弾倍率・視線を遮る重み（"terrain"）。
        /// 移動倍率 0 の文字（川）は通れない。塹壕の被弾倍率は止まっている中隊にだけ効く（呼び出し側で判定する）。
        /// </summary>
        public sealed class TerrainParams
        {
            public readonly Dictionary<char, double> Move = new Dictionary<char, double>();
            public readonly Dictionary<char, double> Damage = new Dictionary<char, double>();
            public readonly Dictionary<char, double> LosWeight = new Dictionary<char, double>();
            /// <summary>
            /// 開けた場所（平地・砲撃痕・橋・浅瀬・塹壕のマス）をその刻みに移動している中隊の被弾倍率。1.0 なら効かない。
            /// </summary>
            public double MovingOpenDamageMult;
            /// <summary>
            /// 向きのある守り（塹壕・陣地構築・浅い掩体）を、陣地の向きの正面の弧の外から撃たれたときの被弾倍率（R2。守りの倍率を
            /// これに置き換える。1.0 なら守りなし）。
            /// </summary>
            public double FortificationOffArcMult;
            /// <summary>
            /// 陣地の向きを見ない（R2 の前と同じ）。sim_params.json にはなく、R2 の前のログと比べる見張りのテストだけが使う。
            /// </summary>
            public bool IgnoreFortificationFacing;

            public bool Knows(char c) => Move.ContainsKey(c);
            /// <summary>移動倍率。表にない文字は 0（通れない）。</summary>
            public double MoveMult(char c) => Move.TryGetValue(c, out var v) ? v : 0.0;
            /// <summary>被弾倍率。表にない文字は 1.0。</summary>
            public double DamageMult(char c) => Damage.TryGetValue(c, out var v) ? v : 1.0;
            /// <summary>視線を遮る重み（そのマスを通る距離に掛ける）。表にない文字は 0。</summary>
            public double LosWeightOf(char c) => LosWeight.TryGetValue(c, out var v) ? v : 0.0;
        }

        /// <summary>視線（"sight"）。</summary>
        public sealed class SightParams
        {
            public double BaseRangeM;
            /// <summary>高い側から見るとき、高低差 10m ごとに視界距離に掛ける増分（0.1 なら +10%）。</summary>
            public double RangeGainPer10mHeight;
            public double EyeHeightM;
            public double SampleStepM;
            /// <summary>森林・村落を通る重み付きの距離がこれを超えたら遮られる。</summary>
            public double CoverBlockM;
        }

        /// <summary>P1 の地図まわりの値（"map_rules"）。</summary>
        public sealed class MapRulesParams
        {
            /// <summary>露出の地図（と、退路の見積もりに使う敵ごとの見える範囲）を作り直す間隔。</summary>
            public double ExposureRebuildSec;
            /// <summary>経路（攻撃の発起点を含む）を引き直す間隔。</summary>
            public double ReplanSec;
            /// <summary>露出1マスあたりのコスト（m 換算）。脅威の重みに掛ける。</summary>
            public double ExposureCostM;
            public readonly Dictionary<Personality, double> ThreatWeightByPersonality = new Dictionary<Personality, double>();
            /// <summary>台本で動く中隊（予備隊の逆襲）の脅威の重み。</summary>
            public double ScriptedThreatWeight;
            /// <summary>攻撃の発起点：敵の中心から、指定の方向へこの距離。</summary>
            public double AttackStandoffM;
            /// <summary>機敏は、側面の経路コストが正面のこの倍以下なら側面を選ぶ。</summary>
            public double AgileFlankCostRatio;
            /// <summary>発起点に着いた後、発起点がこれ以上離れたら、また動く。</summary>
            public double AttackRepositionM;
            /// <summary>目的地が近すぎるときに、ずらす先を探す輪の刻み・最大半径・方位の数。</summary>
            public double DestinationSearchStepM;
            public double DestinationSearchMaxM;
            public int DestinationSearchDirs;
            /// <summary>経路探索で、止まっている味方の中心からこの距離未満のマスは高コスト（P1e の1。P1d までは通れなかった）。</summary>
            public double AvoidStationaryFriendM;
            /// <summary>陣地構築：完成までの時間、完成時の被弾倍率、塹壕にする左右のマス数。</summary>
            public double EntrenchSec;
            public double EntrenchDamageMult;
            public int EntrenchHalfWidthCells;
            /// <summary>退路の見積もりで、川に突き当たる方位の損害割合（逃げられない）。</summary>
            public double BlockedEscapeLoss;
            /// <summary>止まっている味方の圏（<see cref="AvoidStationaryFriendM"/> 未満のマス）の距離のコストの倍率（P1e の1）。</summary>
            public double StationaryFriendPathCostMult;

            public double ThreatWeight(Personality p) => ThreatWeightByPersonality[p];
        }

        /// <summary>P1d の行動の規則（"behavior"）。</summary>
        public sealed class BehaviorParams
        {
            /// <summary>攻撃の側の選び直し：もう一方の側の経路コストが今の側のこの倍未満のときだけ替える。</summary>
            public double AttackSideSwitchRatio;
            /// <summary>動いている中隊は、自分の矩形と敵の矩形の隙間がこれ未満になる手前で止まる。</summary>
            public double EnemyMinGapM;
            /// <summary>近い拘束：目標の中心から、今いる方角へこの距離の点まで詰める。</summary>
            public double PinDistanceM;
            /// <summary>追撃：壊走中の目標からこの距離を保って追う。</summary>
            public double PursuitKeepDistanceM;
            /// <summary>追撃の限界（追撃を始めた地点から）。性格ごとの倍率を掛ける。</summary>
            public double PursuitLimitM;
            /// <summary>性格ごとの追撃の限界の倍率。0 は追わない（確保）。</summary>
            public readonly Dictionary<Personality, double> PursuitLimitMult = new Dictionary<Personality, double>();
            /// <summary>冷静は、この距離以内に指揮下の敵がいなければ追い、追っている間に現れたら止まる。</summary>
            public double PursuitCalmClearRadiusM;
            /// <summary>追撃の経路を引き直す間隔（逃げる目標に置いていかれないよう、通常の30秒より短い）。</summary>
            public double PursuitReplanSec;
            /// <summary>確保の向き：この距離以内に指揮下の敵がいれば最も近いその敵の方を向く。</summary>
            public double SecureFaceEnemyRadiusM;
            /// <summary>向き直り：性格ごとに、正面の弧のこの距離以内に敵がいなければ向き直る。−1 は向き直らない。</summary>
            public readonly Dictionary<Personality, double> TurnFrontClearM = new Dictionary<Personality, double>();
            /// <summary>向き直り：正面の弧の敵を数えるのに視線が要るか。</summary>
            public readonly Dictionary<Personality, bool> TurnFrontNeedsSight = new Dictionary<Personality, bool>();
            /// <summary>向き直りの待ちの間に、この秒数だけ側面・背面から撃たれなければ待ちを取り消す。</summary>
            public double TurnCancelQuietSec;
            /// <summary>壊走中の中隊が味方と重なっている間の速さの倍率。</summary>
            public double RoutPassSpeedMult;
            /// <summary>すり抜けられた味方の士気の減り（その壊走中の中隊ごとに一度だけ）。</summary>
            public double RoutPassMoraleHit;
            /// <summary>隊形の乱れの長さと、その間の射撃効率の倍率。</summary>
            public double DisarraySec;
            public double DisarrayFireMult;
            /// <summary>壊走中の方位は、新しい最小の方位の見積もり損害が今の方位よりこれ以上小さいときだけ替える。</summary>
            public double FleeSwitchMinGain;
            /// <summary>接敵：この距離以内に視線の通る指揮下の敵がいる（P1e の2）。</summary>
            public double ContactRadiusM;
            /// <summary>遠い拘束の距離（<see cref="PinDistanceM"/> は近い拘束。P1e の3）。</summary>
            public double PinDistanceFarM;
            /// <summary>範囲での攻撃の指定：今の方角が範囲の外なら、範囲の端からこの角度だけ内側の方角から攻める（P1e の4）。</summary>
            public double AttackRangeInsetDeg;
            /// <summary>P1g：互いの矩形の隙間がこれ以内（接触中）なら、視線を問わずに撃ち合える。</summary>
            public double SightContactGapM;
            /// <summary>P1g：拘束で見える位置を探す、今の方角の左右の範囲と刻み。</summary>
            public double SightSeekSideDeg;
            public double SightSeekSideStepDeg;
            /// <summary>P1g：見える位置を探して目標へ詰めるときに調べる距離の刻み。</summary>
            public double SightSeekStepM;
        }

        public static SimParams LoadFile(string path) => FromJson(File.ReadAllText(path));

        public static SimParams FromJson(string json)
        {
            var root = JsonValue.Parse(json);
            var p = new SimParams();

            p.TickSec = root["time"].D("tick_sec");

            var f = root["fire"];
            p.Fire.KPerMin = f.D("k_per_min");
            p.Fire.RangeMaxM = f.D("range_max_m");
            p.Fire.RangeCloseM = f.D("range_close_m");
            p.Fire.RangeCloseMult = f.D("range_close_mult");
            ReadAspects(f["aspect_casualty_mult"], p.Fire.AspectCasualtyMult);
            p.Fire.FrontArcDeg = f.D("front_arc_deg");
            p.Fire.FlankArcDeg = f.D("flank_arc_deg");
            p.Fire.NonFrontTargetMult = f.D("non_front_target_mult");
            p.Fire.ShakenMult = f.D("shaken_mult");
            p.Fire.SuppressedMult = f.D("suppressed_mult");
            p.Fire.MovingFireMult = f.D("moving_fire_mult");

            var m = root["morale"];
            p.Morale.Initial = m.D("initial");
            p.Morale.PerPctLoss = m.D("per_pct_loss");
            ReadAspects(m["aspect_morale_mult"], p.Morale.AspectMoraleMult);
            p.Morale.FirstHitShockFlank = m["first_hit_shock"].D("flank");
            p.Morale.FirstHitShockRear = m["first_hit_shock"].D("rear");
            p.Morale.UnderFirePerMin = m.D("under_fire_per_min");
            p.Morale.NeighborRoutShock = m.D("neighbor_rout_shock");
            p.Morale.NeighborRoutRadiusM = m.D("neighbor_rout_radius_m");
            p.Morale.RecoveryPerMin = m.D("recovery_per_min");
            p.Morale.RecoveryQuietSec = m.D("recovery_quiet_sec");
            p.Morale.RecoveryCap = m.D("recovery_cap");
            p.Morale.ShakenThreshold = m.D("shaken_threshold");

            var tr = root["training"];
            p.TrainingTable[Training.Green] = ReadTraining(tr["green"]);
            p.TrainingTable[Training.Regular] = ReadTraining(tr["regular"]);
            p.TrainingTable[Training.Veteran] = ReadTraining(tr["veteran"]);

            var e = root["encirclement"];
            p.Encirclement.CheckWhenMoraleAtOrBelow = e.D("check_when_morale_at_or_below");
            p.Encirclement.CheckIntervalSec = e.D("check_interval_sec");
            p.Encirclement.EscapeDirs = (int)e.D("escape_dirs");
            p.Encirclement.EscapeLenM = e.D("escape_len_m");
            p.Encirclement.EscapeSpeedMps = e.D("escape_speed_mps");
            p.Encirclement.EscapeStepSec = e.D("escape_step_sec");
            p.Encirclement.EscapeFireAspect = ParseAspect(e["escape_fire_aspect"].String);
            p.Encirclement.EscapeLossThreshold = e.D("escape_loss_threshold");
            p.Encirclement.DespairShock = e.D("despair_shock");
            p.Encirclement.MoralePerMin = e.D("morale_per_min");
            p.Encirclement.FortifiedMoraleMult = e.D("fortified_morale_mult");

            var r = root["rout"];
            p.Rout.FleeSpeedMps = r.D("flee_speed_mps");
            p.Rout.FleeFromRadiusM = r.D("flee_from_radius_m");
            p.Rout.PursuitRadiusM = r.D("pursuit_radius_m");
            p.Rout.PursuitCasualtyMult = r.D("pursuit_casualty_mult");
            p.Rout.RallyClearRadiusM = r.D("rally_clear_radius_m");
            p.Rout.RallyAfterSec = r.D("rally_after_sec");
            p.Rout.RallyReturnMorale = r.D("rally_return_morale");

            var pin = root["pinning"];
            p.Pinning.RadiusM = pin.D("radius_m");
            p.Pinning.MoveMult = pin.D("move_mult");
            p.Pinning.DisengageMoraleHit = pin.D("disengage_morale_hit");
            p.Pinning.DisengageCasualtyMult = pin.D("disengage_casualty_mult");

            var cc = root["close_combat"];
            p.CloseCombat.RadiusM = cc.D("radius_m");
            p.CloseCombat.RangeMult = cc.D("range_mult");
            p.CloseCombat.MoraleMult = cc.D("morale_mult");

            var mv = root["movement"];
            p.Movement.SpeedMps = mv.D("speed_mps");
            p.Movement.TurnDegPerSec = mv.D("turn_deg_per_sec");
            p.Movement.FrontageM = mv.D("frontage_m");
            p.Movement.DepthM = mv.D("depth_m");
            p.Movement.CompanyStrength = (int)mv.D("company_strength");
            p.Movement.RetreatSpeedMult = mv.D("retreat_speed_mult");
            p.Movement.FriendlyMinSpacingM = mv.D("friendly_min_spacing_m");
            p.Movement.PassThroughSpeedMult = mv.D("pass_through_speed_mult");
            p.Movement.SidewaysSpeedMult = mv.D("sideways_speed_mult");
            p.Movement.TurningSpeedMult = mv.D("turning_speed_mult");
            p.Movement.TurningSlowDeg = mv.D("turning_slow_deg");

            var te = root["terrain"];
            ReadCharTable(te["move_mult"], p.Terrain.Move);
            ReadCharTable(te["damage_mult"], p.Terrain.Damage);
            ReadCharTable(te["los_weight"], p.Terrain.LosWeight);
            p.Terrain.MovingOpenDamageMult = te.D("moving_open_damage_mult");
            p.Terrain.FortificationOffArcMult = te.D("fortification_off_arc_mult");

            var si = root["sight"];
            p.Sight.BaseRangeM = si.D("base_range_m");
            p.Sight.RangeGainPer10mHeight = si.D("range_gain_per_10m_height");
            p.Sight.EyeHeightM = si.D("eye_height_m");
            p.Sight.SampleStepM = si.D("sample_step_m");
            p.Sight.CoverBlockM = si.D("cover_block_m");

            var mr = root["map_rules"];
            p.MapRules.ExposureRebuildSec = mr.D("exposure_rebuild_sec");
            p.MapRules.ReplanSec = mr.D("replan_sec");
            p.MapRules.ExposureCostM = mr.D("exposure_cost_m");
            var tw = mr["threat_weight"];
            p.MapRules.ThreatWeightByPersonality[Personality.Cautious] = tw.D("cautious");
            p.MapRules.ThreatWeightByPersonality[Personality.Calm] = tw.D("calm");
            p.MapRules.ThreatWeightByPersonality[Personality.Agile] = tw.D("agile");
            p.MapRules.ThreatWeightByPersonality[Personality.Reckless] = tw.D("reckless");
            p.MapRules.ScriptedThreatWeight = tw.D("scripted");
            p.MapRules.AttackStandoffM = mr.D("attack_standoff_m");
            p.MapRules.AgileFlankCostRatio = mr.D("agile_flank_cost_ratio");
            p.MapRules.AttackRepositionM = mr.D("attack_reposition_m");
            p.MapRules.DestinationSearchStepM = mr.D("destination_search_step_m");
            p.MapRules.DestinationSearchMaxM = mr.D("destination_search_max_m");
            p.MapRules.DestinationSearchDirs = (int)mr.D("destination_search_dirs");
            p.MapRules.AvoidStationaryFriendM = mr.D("avoid_stationary_friend_m");
            p.MapRules.EntrenchSec = mr.D("entrench_sec");
            p.MapRules.EntrenchDamageMult = mr.D("entrench_damage_mult");
            p.MapRules.EntrenchHalfWidthCells = (int)mr.D("entrench_half_width_cells");
            p.MapRules.BlockedEscapeLoss = mr.D("blocked_escape_loss");
            p.MapRules.StationaryFriendPathCostMult = mr.D("stationary_friend_path_cost_mult");

            var be = root["behavior"];
            p.Behavior.AttackSideSwitchRatio = be.D("attack_side_switch_ratio");
            p.Behavior.EnemyMinGapM = be.D("enemy_min_gap_m");
            p.Behavior.PinDistanceM = be.D("pin_distance_m");
            p.Behavior.PursuitKeepDistanceM = be.D("pursuit_keep_distance_m");
            p.Behavior.PursuitLimitM = be.D("pursuit_limit_m");
            ReadPersonalityTable(be["pursuit_limit_mult"], p.Behavior.PursuitLimitMult);
            p.Behavior.PursuitCalmClearRadiusM = be.D("pursuit_calm_clear_radius_m");
            p.Behavior.PursuitReplanSec = be.D("pursuit_replan_sec");
            p.Behavior.SecureFaceEnemyRadiusM = be.D("secure_face_enemy_radius_m");
            ReadPersonalityTable(be["turn_front_clear_m"], p.Behavior.TurnFrontClearM);
            var ns = be["turn_front_needs_sight"];
            foreach (var (key, pers) in PersonalityKeys) p.Behavior.TurnFrontNeedsSight[pers] = ns[key].Bool;
            p.Behavior.TurnCancelQuietSec = be.D("turn_cancel_quiet_sec");
            p.Behavior.RoutPassSpeedMult = be.D("rout_pass_speed_mult");
            p.Behavior.RoutPassMoraleHit = be.D("rout_pass_morale_hit");
            p.Behavior.DisarraySec = be.D("disarray_sec");
            p.Behavior.DisarrayFireMult = be.D("disarray_fire_mult");
            p.Behavior.FleeSwitchMinGain = be.D("flee_switch_min_gain");
            p.Behavior.ContactRadiusM = be.D("contact_radius_m");
            p.Behavior.PinDistanceFarM = be.D("pin_distance_far_m");
            p.Behavior.AttackRangeInsetDeg = be.D("attack_range_inset_deg");
            p.Behavior.SightContactGapM = be.D("sight_contact_gap_m");
            p.Behavior.SightSeekSideDeg = be.D("sight_seek_side_deg");
            p.Behavior.SightSeekSideStepDeg = be.D("sight_seek_side_step_deg");
            p.Behavior.SightSeekStepM = be.D("sight_seek_step_m");

            var rn = root["runners"];
            p.Runners.PrepSec = rn.D("prep_sec");
            p.Runners.SpeedMps = rn.D("speed_mps");
            p.Runners.LossPerSec = rn.D("loss_per_sec");
            p.Runners.OverdueMult = rn.D("overdue_mult");
            p.Runners.MaxOut = (int)rn.D("max_out");
            p.Runners.PathThreatWeight = rn.D("path_threat_weight");
            p.Runners.ReplanSec = rn.D("replan_sec");
            p.Orders.MaxStages = (int)root["orders"].D("max_stages");

            var au = root["autonomy"];
            p.Autonomy.PostRadiusM = au.D("post_radius_m");
            p.Autonomy.FaceEnemyRadiusM = au.D("face_enemy_radius_m");
            p.Autonomy.FaceShooterMemorySec = au.D("face_shooter_memory_sec");
            p.Autonomy.FaceNearRadiusM = au.D("face_near_radius_m");
            p.Autonomy.FaceFrontHoldM = au.D("face_front_hold_m");
            p.Autonomy.CoverUnderFireSec = au.D("cover_under_fire_sec");
            p.Autonomy.CoverOpenMult = au.D("cover_open_mult");
            p.Autonomy.CoverMultRatio = au.D("cover_mult_ratio");
            p.Autonomy.CoverFireGapSec = au.D("cover_fire_gap_sec");
            p.Autonomy.CoverRetrySec = au.D("cover_retry_sec");
            p.Autonomy.CoverMinShooterDistM = au.D("cover_min_shooter_dist_m");
            p.Autonomy.ScrapeStillSec = au.D("scrape_still_sec");
            p.Autonomy.ScrapeQuietSec = au.D("scrape_quiet_sec");
            p.Autonomy.ScrapeDigSec = au.D("scrape_dig_sec");
            p.Autonomy.ScrapeDamageMult = au.D("scrape_damage_mult");
            p.Autonomy.EnterPositionRadiusM = au.D("enter_position_radius_m");
            ReadPersonalityTable(au["advance_refuse_morale"], p.Autonomy.AdvanceRefuseMorale);
            p.Autonomy.RecklessFollowupRadiusM = au.D("reckless_followup_radius_m");
            p.Autonomy.AgileWatchRadiusM = au.D("agile_watch_radius_m");
            p.Autonomy.AgilePathMaxM = au.D("agile_path_max_m");
            p.Autonomy.AgilePostLimitM = au.D("agile_post_limit_m");
            p.Autonomy.AgileCheckSec = au.D("agile_check_sec");
            p.Autonomy.CautiousCoverRadiusM = au.D("cautious_cover_radius_m");

            var co = root["couriers"];
            p.Couriers.PrepSec = co.D("prep_sec");
            p.Couriers.MaxOutPerCompany = (int)co.D("max_out_per_company");
            p.Couriers.BatchSec = co.D("batch_sec");
            p.Couriers.DiscoverRadiusM = co.D("discover_radius_m");
            ReadPersonalityTable(co["heavy_loss_step"], p.Couriers.HeavyLossStep);
            p.Couriers.NeighborRoutRadiusM = co.D("neighbor_rout_radius_m");
            p.Couriers.AskOrdersAfterSec = co.D("ask_orders_after_sec");
            p.Couriers.AskOrdersRepeatSec = co.D("ask_orders_repeat_sec");
            ReadPersonalityTable(co["retreat_ask_morale"], p.Couriers.RetreatAskMorale);
            p.Couriers.RetreatAskQuietSec = co.D("retreat_ask_quiet_sec");

            var su = root["suppression"];
            p.Suppression.ThresholdMovingPerMin = su.D("threshold_moving_per_min");
            p.Suppression.ThresholdStationaryPerMin = su.D("threshold_stationary_per_min");
            p.Suppression.OnsetSec = su.D("onset_sec");
            p.Suppression.ReleaseRatio = su.D("release_ratio");
            p.Suppression.ReleaseSec = su.D("release_sec");
            p.Suppression.FireMult = su.D("fire_mult");
            ReadPersonalityTable(su["moving_threshold_personality_mult"], p.Suppression.MovingThresholdPersonalityMult);

            var de = root["defense"];
            p.Defense.Enabled = de["enabled"].Bool;
            p.Defense.CheckSec = de.D("check_sec");
            p.Defense.BypassRadiusM = de.D("bypass_radius_m");
            p.Defense.BypassBehindM = de.D("bypass_behind_m");
            p.Defense.FlankFireMemorySec = de.D("flank_fire_memory_sec");
            p.Defense.PinRadiusM = de.D("pin_radius_m");
            p.Defense.ReserveSpentLoss = de.D("reserve_spent_loss");
            p.Defense.HqEvalSec = de.D("hq_eval_sec");
            string mode = de["retake_mode"].String;
            if (mode != "assemble" && mode != "immediate") throw new FormatException("defense.retake_mode は assemble か immediate: " + mode);
            p.Defense.RetakeAssemble = mode == "assemble";
            p.Defense.AssembleRadiusM = de.D("assemble_radius_m");
            p.Defense.AssembleMinCompanies = (int)de.D("assemble_min_companies");
            p.Defense.AssembleMaxWaitSec = de.D("assemble_max_wait_sec");
            p.Defense.CounterIsolationRadiusM = de.D("counter_isolation_radius_m");
            p.Defense.CounterMaxSupport = (int)de.D("counter_max_support");
            p.Defense.AssaultStallSec = de.D("assault_stall_sec");
            p.Defense.AssaultStallProgressM = de.D("assault_stall_progress_m");

            var oc = root["occupation"];
            p.Occupation.Enabled = oc["enabled"].Bool;
            p.Occupation.DisarraySec = oc.D("disarray_sec");
            p.Occupation.DisarrayFireMult = oc.D("disarray_fire_mult");

            return p;
        }

        static readonly (string key, Personality pers)[] PersonalityKeys =
        {
            ("cautious", Personality.Cautious), ("calm", Personality.Calm), ("agile", Personality.Agile), ("reckless", Personality.Reckless),
        };

        static void ReadPersonalityTable(JsonValue v, Dictionary<Personality, double> into)
        {
            foreach (var (key, pers) in PersonalityKeys) into[pers] = v.D(key);
        }

        /// <summary>1文字のキーから数値への表。"_" で始まるキー（注記）は読み飛ばす。</summary>
        static void ReadCharTable(JsonValue v, Dictionary<char, double> into)
        {
            foreach (var kv in v.Object)
            {
                if (kv.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                if (kv.Key.Length != 1) throw new FormatException($"地形の文字は1文字: '{kv.Key}'");
                if (kv.Value.Kind != JsonKind.Number) throw new FormatException($"'{kv.Key}' は数値ではない");
                into[kv.Key[0]] = kv.Value.Number;
            }
        }

        static Aspect ParseAspect(string s)
        {
            switch (s)
            {
                case "front": return Aspect.Front;
                case "flank": return Aspect.Flank;
                case "rear": return Aspect.Rear;
                default: throw new FormatException("向きの名前が不正: " + s);
            }
        }

        static void ReadAspects(JsonValue v, double[] into)
        {
            into[(int)Aspect.Front] = v.D("front");
            into[(int)Aspect.Flank] = v.D("flank");
            into[(int)Aspect.Rear] = v.D("rear");
        }

        static TrainingParams ReadTraining(JsonValue v)
        {
            return new TrainingParams
            {
                RoutThreshold = v.D("rout_threshold"),
                FireMult = v.D("fire_mult"),
                MoraleSensitivity = v.D("morale_sensitivity"),
                RecoveryMult = v.D("recovery_mult"),
                OrderStartSec = v.D("order_start_sec"),
                ReportErrorM = v.D("report_error_m"),
            };
        }
    }
}
