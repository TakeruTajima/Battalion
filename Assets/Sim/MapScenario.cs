using System;
using System.Collections.Generic;
using System.IO;

namespace Battalion.Sim
{
    /// <summary>
    /// 地図ファイル（maps/scenario_*.json）のシナリオ：地図、初期配置、予備隊の台本、村のマス、制限時間。
    /// 中隊の性格と練度は地図ファイルにないので、仕様の兵力の表から持たせる
    /// （第1 慎重・通常、第2 冷静・通常、第3 猪突・古参、第4 機敏・通常。敵は全て通常）。
    /// </summary>
    public sealed class MapScenario
    {
        public sealed class Deploy
        {
            public string Id;
            public Side Side;
            public double X, Y, FacingDeg;
        }

        public string Name;
        public int Stage;
        public JsonValue Source;
        public readonly List<Deploy> Units = new List<Deploy>();
        public readonly HashSet<(int i, int j)> VillageCells = new HashSet<(int i, int j)>();
        public double TimeLimitMin;

        public string ReserveUnit;
        public Vec2 ReserveTriggerCenter;
        public double ReserveTriggerRadiusM;

        /// <summary>勝ちの判定に使う村の中心（概算シミュレーションと同じ）。</summary>
        public static readonly Vec2 VillageCenter = new Vec2(1975, 1000);
        public const double VillageHoldRadiusM = 250.0;

        public static MapScenario LoadFile(string path) => FromJson(JsonValue.Parse(File.ReadAllText(path)));

        public static MapScenario FromJson(JsonValue d)
        {
            var s = new MapScenario
            {
                Name = d["name"].String,
                Stage = (int)d.D("stage"),
                Source = d,
                TimeLimitMin = d.D("time_limit_min"),
            };
            foreach (var side in new[] { Side.Blue, Side.Red })
            {
                foreach (var u in d["deploy"][Names.Of(side)].Array)
                    s.Units.Add(new Deploy { Id = u["id"].String, Side = side, X = u.D("x"), Y = u.D("y"), FacingDeg = u.D("facing") });
            }
            foreach (var c in d["objects"]["village"]["cells"].Array)
                s.VillageCells.Add(((int)c.Array[0].Number, (int)c.Array[1].Number));
            var rs = d["reserve_script"];
            s.ReserveUnit = rs["unit"].String;
            var tc = rs["trigger_center"].Array;
            s.ReserveTriggerCenter = new Vec2(tc[0].Number, tc[1].Number);
            s.ReserveTriggerRadiusM = rs.D("trigger_radius_m");
            return s;
        }

        public static Personality PersonalityOf(string id)
        {
            switch (id)
            {
                case "B1": return Personality.Cautious;
                case "B3": return Personality.Reckless;
                case "B4": return Personality.Agile;
                default: return Personality.Calm;
            }
        }

        public static Training TrainingOf(string id) => id == "B3" ? Training.Veteran : Training.Regular;

        /// <summary>
        /// 地図を新しく作り（会戦中に変わる層を持つので、会戦ごとに別の地図にする）、初期配置と予備隊の台本を載せる。
        /// 味方の中心どうしが 200m 未満の初期配置は例外にする。
        /// </summary>
        public Simulation Build(SimParams p, ulong seed, IEventSink log = null)
        {
            var sim = new Simulation(p, seed, TerrainMap.FromJson(Source, p.Terrain), log);
            foreach (var u in Units)
                sim.AddCompany(u.Id, u.Side, TrainingOf(u.Id), PersonalityOf(u.Id), new Vec2(u.X, u.Y), u.FacingDeg);

            var cs = sim.Companies;
            for (int a = 0; a < cs.Count; a++)
                for (int b = a + 1; b < cs.Count; b++)
                    if (cs[a].Side == cs[b].Side && Vec2.Distance(cs[a].Position, cs[b].Position) < p.Movement.FriendlyMinSpacingM - 1e-9)
                        throw new InvalidOperationException($"初期配置で {cs[a].Name} と {cs[b].Name} が近すぎる");

            sim.AddScript(new ReserveScript(ReserveUnit, ReserveTriggerCenter, ReserveTriggerRadiusM));
            return sim;
        }

        /// <summary>
        /// 青（攻撃側）の勝ち：村のマスに指揮下の青の中隊がいて、村の中心から 250m 以内に指揮下の赤の中隊がいない。
        /// または赤が全て盤上から消え、指揮下の青が残っている。
        /// </summary>
        public bool BlueWins(Simulation sim)
        {
            bool blueInVillage = false, redNearVillage = false, redOnBoard = false, blueUnderCommand = false;
            foreach (var c in sim.Companies)
            {
                if (c.Side == Side.Blue && c.UnderCommand)
                {
                    blueUnderCommand = true;
                    sim.Map.CellOf(c.Position, out int i, out int j);
                    if (VillageCells.Contains((i, j))) blueInVillage = true;
                }
                if (c.Side == Side.Red)
                {
                    if (c.OnBoard) redOnBoard = true;
                    if (c.UnderCommand && Vec2.Distance(c.Position, VillageCenter) <= VillageHoldRadiusM) redNearVillage = true;
                }
            }
            return (blueInVillage && !redNearVillage) || (!redOnBoard && blueUnderCommand);
        }
    }

    /// <summary>
    /// 予備隊の台本：引き金の円の中に指揮下の青の中隊の中心が入ったら一度だけ発動し、
    /// 予備（R3）が、円の中にいる青の中隊のうち R3 に最も近いものへ、R3 の側の側面から攻撃する。
    /// 経路の脅威の重みは map_rules.threat_weight.scripted（0）。
    /// </summary>
    public sealed class ReserveScript : ISimScript
    {
        readonly string _unit;
        readonly Vec2 _center;
        readonly double _radius;

        public string Unit => _unit;
        public Vec2 Center => _center;
        public double Radius => _radius;
        public bool Triggered { get; private set; }
        public double? TriggeredAt { get; private set; }

        public ReserveScript(string unit, Vec2 center, double radius)
        {
            _unit = unit;
            _center = center;
            _radius = radius;
        }

        public void Update(Simulation sim, double tickStart)
        {
            if (Triggered) return;
            var r3 = sim.Find(_unit);
            if (r3 == null || !r3.UnderCommand) return;

            Company target = null;
            double best = double.MaxValue;
            foreach (var c in sim.Companies)
            {
                if (c.Side == r3.Side || !c.UnderCommand) continue;
                if (Vec2.Distance(c.Position, _center) > _radius) continue;
                double d = Vec2.Distance(c.Position, r3.Position);
                if (d < best) { best = d; target = c; }
            }
            if (target == null) return;

            Triggered = true;
            TriggeredAt = tickStart;
            r3.ThreatWeightOverride = sim.Params.MapRules.ScriptedThreatWeight;
            // 側面の範囲で攻撃する（P1e の4。逆襲は突出した敵の側面を突く）。範囲なので、目標が向きを変えれば選び直す
            sim.Issue(Order.Attack(r3.Id, target.Id, AttackAspect.Flank));
            sim.LogScriptEvent(tickStart + sim.Params.TickSec, "reserve_counterattack", r3, target);
        }
    }
}
