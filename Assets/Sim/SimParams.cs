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

        public sealed class EncirclementParams
        {
            public int MinAspects;
            public double RearCorridorDepthM;
            public double RearCorridorHalfWidthM;
            public double MoralePerMin;
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
            public double CasualtyMult;
            public double MoraleMult;
        }

        public sealed class MovementParams
        {
            public double SpeedMps;
            public double TurnDegPerSec;
            public double FrontageM;
            public double DepthM;
            public int CompanyStrength;
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
            p.Encirclement.MinAspects = (int)e.D("min_aspects");
            p.Encirclement.RearCorridorDepthM = e.D("rear_corridor_depth_m");
            p.Encirclement.RearCorridorHalfWidthM = e.D("rear_corridor_half_width_m");
            p.Encirclement.MoralePerMin = e.D("morale_per_min");

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
            p.CloseCombat.CasualtyMult = cc.D("casualty_mult");
            p.CloseCombat.MoraleMult = cc.D("morale_mult");

            var mv = root["movement"];
            p.Movement.SpeedMps = mv.D("speed_mps");
            p.Movement.TurnDegPerSec = mv.D("turn_deg_per_sec");
            p.Movement.FrontageM = mv.D("frontage_m");
            p.Movement.DepthM = mv.D("depth_m");
            p.Movement.CompanyStrength = (int)mv.D("company_strength");

            return p;
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
