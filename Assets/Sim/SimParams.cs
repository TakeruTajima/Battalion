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
            public double SuppressedMult;
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
