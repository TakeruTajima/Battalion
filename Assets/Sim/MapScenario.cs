using System;
using System.Collections.Generic;
using System.IO;

namespace Battalion.Sim
{
    /// <summary>作戦目標の種類（地図ファイルの objective.type。なければ村の奪取）。</summary>
    public enum ObjectiveKind
    {
        /// <summary>村の奪取（シナリオA）。</summary>
        Village,
        /// <summary>塹壕から敵を追い出して確保する（教程1。trench_clear）。</summary>
        TrenchClear,
    }

    /// <summary>
    /// 地図ファイル（maps/*.json）のシナリオ：地図、初期配置、守りの計画（E1）、作戦目標（村・塹壕）、本部、任務の文、制限時間。
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
        /// <summary>題（地図ファイルの title）。</summary>
        public string Title;
        /// <summary>開始時に出す任務の文（地図ファイルの briefing。なければ null）。</summary>
        public string Briefing;
        public int Stage;
        public ObjectiveKind Objective = ObjectiveKind.Village;
        /// <summary>大隊本部の位置（地図ファイルの headquarters。なければシナリオA の位置）。</summary>
        public Vec2 BlueHq = BlueHeadquarters, RedHq = RedHeadquarters;
        /// <summary>地図の塹壕のマス（trench_clear の判定に使う）。</summary>
        public readonly HashSet<(int i, int j)> TrenchCells = new HashSet<(int i, int j)>();
        public JsonValue Source;
        public readonly List<Deploy> Units = new List<Deploy>();
        public readonly HashSet<(int i, int j)> VillageCells = new HashSet<(int i, int j)>();
        public double TimeLimitMin;

        /// <summary>守りの計画（E1。地図ファイルの defense_plan）。</summary>
        public DefensePlan Defense;

        /// <summary>予備の中隊と引き金の円（defense.enabled が false のときの、今までの予備隊の台本に使う）。予備がなければ null。</summary>
        public string ReserveUnit;
        public Vec2 ReserveTriggerCenter;
        public double ReserveTriggerRadiusM;

        /// <summary>
        /// 塹壕に配置した赤の中隊（V1。青の第二目標「塹壕の敵の排除」の対象）。trench_clear の地図は objective.trench_units。
        /// シナリオA の地図にはないので、ステージごとにここで持つ：ステージ1 は稜線の塹壕の R1、ステージ2 は前の塹壕の R1・R2（R3 は村の予備隊）。
        /// </summary>
        public readonly List<string> TrenchUnits = new List<string>();

        public static string[] TrenchUnitsOf(int stage) => stage == 1 ? new[] { "R1" } : stage == 2 ? new[] { "R1", "R2" } : new string[0];

        /// <summary>勝ちの判定に使う村の中心（シナリオA。概算シミュレーションと同じ）。</summary>
        public static readonly Vec2 VillageCenter = new Vec2(1975, 1000);
        /// <summary>シナリオA の大隊本部の位置（P2a。両ステージ共通、試作では動かない）。</summary>
        public static readonly Vec2 BlueHeadquarters = new Vec2(100, 800);
        public static readonly Vec2 RedHeadquarters = new Vec2(2700, 1000);
        public const double VillageHoldRadiusM = 250.0;

        public static MapScenario LoadFile(string path) => FromJson(JsonValue.Parse(File.ReadAllText(path)));

        public static MapScenario FromJson(JsonValue d)
        {
            var s = new MapScenario
            {
                Name = d["name"].String,
                Title = d.Has("title") ? d["title"].String : d["name"].String,
                Briefing = d.Has("briefing") ? d["briefing"].String : null,
                Stage = (int)d.D("stage"),
                Source = d,
                TimeLimitMin = d.D("time_limit_min"),
            };
            if (d.Has("headquarters"))
            {
                s.BlueHq = Point(d["headquarters"]["blue"]);
                s.RedHq = Point(d["headquarters"]["red"]);
            }
            foreach (var side in new[] { Side.Blue, Side.Red })
            {
                foreach (var u in d["deploy"][Names.Of(side)].Array)
                    s.Units.Add(new Deploy { Id = u["id"].String, Side = side, X = u.D("x"), Y = u.D("y"), FacingDeg = u.D("facing") });
            }
            if (d["objects"].Has("village"))
                foreach (var c in d["objects"]["village"]["cells"].Array)
                    s.VillageCells.Add(((int)c.Array[0].Number, (int)c.Array[1].Number));
            ReadTrenchCells(d, s.TrenchCells);
            s.Defense = ReadDefensePlan(d["defense_plan"]);
            foreach (var kv in s.Defense.Units)
            {
                if (kv.Value.Role != DefenseRole.Reserve) continue;
                s.ReserveUnit = kv.Key;
                s.ReserveTriggerCenter = kv.Value.TriggerCenter;
                s.ReserveTriggerRadiusM = kv.Value.TriggerRadiusM;
            }
            if (d.Has("objective"))
            {
                var ob = d["objective"];
                switch (ob["type"].String)
                {
                    case "trench_clear":
                        s.Objective = ObjectiveKind.TrenchClear;
                        foreach (var u in ob["trench_units"].Array) s.TrenchUnits.Add(u.String);
                        break;
                    case "village":
                        break;
                    default:
                        throw new FormatException($"objective の種類が不明: {ob["type"].String}");
                }
            }
            if (s.Objective == ObjectiveKind.Village)
            {
                if (s.VillageCells.Count == 0) throw new FormatException("村の奪取の地図に村（objects.village）がない");
                s.TrenchUnits.AddRange(TrenchUnitsOf(s.Stage));
            }
            return s;
        }

        /// <summary>地図の文字の並びから、塹壕のマスを集める（行は北から、マスの添字は南西が原点）。</summary>
        static void ReadTrenchCells(JsonValue d, HashSet<(int i, int j)> into)
        {
            var rows = d["terrain"].Array;
            int h = rows.Count;
            for (int r = 0; r < h; r++)
            {
                string row = rows[r].String;
                for (int i = 0; i < row.Length; i++)
                    if (row[i] == TerrainMap.Trench) into.Add((i, h - 1 - r));
            }
        }

        /// <summary>作戦目標の地点（表示の旗の位置）：村の中心、または塹壕のマスの中心の平均。</summary>
        public Vec2 ObjectivePoint(TerrainMap map)
        {
            if (Objective == ObjectiveKind.Village) return VillageCenter;
            double x = 0, y = 0;
            foreach (var (i, j) in TrenchCells)
            {
                var c = map.CellCenter(i, j);
                x += c.X;
                y += c.Y;
            }
            return TrenchCells.Count == 0 ? new Vec2(0, 0) : new Vec2(x / TrenchCells.Count, y / TrenchCells.Count);
        }

        /// <summary>中心が塹壕のマスにあるか（trench_clear）。</summary>
        public bool InTrench(Simulation sim, Company c)
        {
            sim.Map.CellOf(c.Position, out int i, out int j);
            return TrenchCells.Contains((i, j));
        }

        static Vec2 Point(JsonValue v) => new Vec2(v.Array[0].Number, v.Array[1].Number);

        /// <summary>地図ファイルの defense_plan を読む（E1）。</summary>
        public static DefensePlan ReadDefensePlan(JsonValue d)
        {
            var plan = new DefensePlan { ObjectiveCenter = Point(d["objective_center"]), Assembly = Point(d["assembly"]) };
            foreach (var kv in d["units"].Object)
            {
                var u = kv.Value;
                var p = new DefenseUnitPlan();
                switch (u["role"].String)
                {
                    case "front":
                        p.Role = DefenseRole.Front;
                        p.Fallback = Point(u["fallback"]);
                        p.FallbackFacingDeg = u.D("fallback_facing");
                        break;
                    case "garrison":
                        p.Role = DefenseRole.Garrison;
                        break;
                    case "reserve":
                        p.Role = DefenseRole.Reserve;
                        p.Post = Point(u["post"]);
                        p.TriggerCenter = Point(u["trigger_center"]);
                        p.TriggerRadiusM = u.D("trigger_radius_m");
                        break;
                    default:
                        throw new FormatException($"defense_plan の役目が不明: {kv.Key} {u["role"].String}");
                }
                plan.Units[kv.Key] = p;
            }
            return plan;
        }

        /// <summary>
        /// 守りの計画を確かめる（E1）：守る地点・集結点・後退先・戻る位置が地図の中で通れるマスにあり、計画の中隊が配置にいる
        /// （でなければ例外）。後退先（後退先の向き）と戻る位置（守る地点の方角）に置いた中隊の矩形が、ほかの中隊の初めの矩形と
        /// 重なる（辺で接するものを含む）ときは、その一覧を返す。会戦中は、今の目的地の探し直し（味方の間隔）に任せる。
        /// </summary>
        public static List<string> ValidateDefensePlan(DefensePlan plan, Simulation sim)
        {
            var overlaps = new List<string>();
            void Inside(string what, Vec2 p)
            {
                if (!sim.Map.Contains(p) || !sim.Map.Passable(p)) throw new InvalidOperationException($"守りの計画の{what} ({p.X}, {p.Y}) が地図の中の通れるマスにない");
            }
            Inside("守る地点", plan.ObjectiveCenter);
            Inside("集結点", plan.Assembly);
            foreach (var kv in plan.Units)
            {
                var c = sim.Find(kv.Key);
                if (c == null) throw new InvalidOperationException($"守りの計画の {kv.Key} が配置にいない");
                Vec2? at = null;
                double facing = 0;
                if (kv.Value.Role == DefenseRole.Front) { at = kv.Value.Fallback; facing = kv.Value.FallbackFacingDeg * Angles.DegToRad; Inside($"{kv.Key} の後退先", at.Value); }
                if (kv.Value.Role == DefenseRole.Reserve)
                {
                    at = kv.Value.Post;
                    var d = plan.ObjectiveCenter - at.Value;
                    facing = Math.Atan2(d.Y, d.X);
                    Inside($"{kv.Key} の戻る位置", at.Value);
                }
                if (!at.HasValue) continue;
                var rect = sim.RectAt(at.Value, facing);
                foreach (var o in sim.Companies)
                    if (o != c && UnitRect.Overlap(rect, sim.RectOf(o)))
                        overlaps.Add($"{kv.Key} の位置 ({at.Value.X}, {at.Value.Y}) が {o.Name} の初めの矩形と重なる");
            }
            return overlaps;
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
            sim.SetHeadquarters(Side.Blue, BlueHq);
            sim.SetHeadquarters(Side.Red, RedHq);
            foreach (var u in Units)
                sim.AddCompany(u.Id, u.Side, TrainingOf(u.Id), PersonalityOf(u.Id), new Vec2(u.X, u.Y), u.FacingDeg);

            AssignTrenchFacings(sim);
            ValidateDefensePlan(Defense, sim);

            var cs = sim.Companies;
            for (int a = 0; a < cs.Count; a++)
                for (int b = a + 1; b < cs.Count; b++)
                    if (cs[a].Side == cs[b].Side && Vec2.Distance(cs[a].Position, cs[b].Position) < p.Movement.FriendlyMinSpacingM - 1e-9)
                        throw new InvalidOperationException($"初期配置で {cs[a].Name} と {cs[b].Name} が近すぎる");

            // 守りの計画（E1）。defense.enabled が false なら、今までの予備隊の台本（伝令あり、一度だけ）
            if (p.Defense.Enabled) sim.SetDefensePlan(Defense);
            else if (ReserveUnit != null) sim.AddScript(new ReserveScript(ReserveUnit, ReserveTriggerCenter, ReserveTriggerRadiusM));
            return sim;
        }

        /// <summary>
        /// 地図の塹壕の向き（R2）：塹壕のマスを、隣り合う（8方向）マスでつながった1本ずつに分け、会戦開始時にその1本に中心がある
        /// 中隊の向きにする。1本に複数の中隊がいれば、マスごとに一番近い中隊の向き。中隊のいない塹壕は向きなし（向きによらず守る）。
        /// 地図ファイルは変えない。
        /// </summary>
        static void AssignTrenchFacings(Simulation sim)
        {
            var map = sim.Map;
            var seen = new bool[map.Width * map.Height];
            for (int j0 = 0; j0 < map.Height; j0++)
                for (int i0 = 0; i0 < map.Width; i0++)
                {
                    if (seen[j0 * map.Width + i0] || map.BaseTerrainAt(i0, j0) != TerrainMap.Trench) continue;
                    // つながった1本を集める
                    var cells = new List<(int i, int j)>();
                    var stack = new Stack<(int i, int j)>();
                    stack.Push((i0, j0));
                    seen[j0 * map.Width + i0] = true;
                    while (stack.Count > 0)
                    {
                        var (i, j) = stack.Pop();
                        cells.Add((i, j));
                        for (int di = -1; di <= 1; di++)
                            for (int dj = -1; dj <= 1; dj++)
                            {
                                int a = i + di, b = j + dj;
                                if (!map.InBounds(a, b) || seen[b * map.Width + a] || map.BaseTerrainAt(a, b) != TerrainMap.Trench) continue;
                                seen[b * map.Width + a] = true;
                                stack.Push((a, b));
                            }
                    }
                    var set = new HashSet<(int, int)>(cells);
                    var occupants = new List<Company>();
                    foreach (var c in sim.Companies)
                    {
                        map.CellOf(c.Position, out int ci, out int cj);
                        if (set.Contains((ci, cj))) occupants.Add(c);
                    }
                    if (occupants.Count == 0) continue;
                    foreach (var (i, j) in cells)
                    {
                        var center = map.CellCenter(i, j);
                        Company best = null;
                        foreach (var c in occupants)
                            if (best == null || Vec2.Distance(c.Position, center) < Vec2.Distance(best.Position, center) - 1e-9) best = c;
                        map.SetFortificationFacing(i, j, best.Facing);
                    }
                }
        }

        /// <summary>
        /// 青（攻撃側）の勝ち。村の奪取：村のマスに指揮下の青の中隊がいて、村の中心から 250m 以内に指揮下の赤の中隊がいない。
        /// trench_clear：塹壕のマスに中心がある指揮下の赤の中隊がなく、指揮下の青の中隊が1個以上残っている。
        /// どちらも、赤が全て盤上から消え、指揮下の青が残っていれば勝ち。
        /// </summary>
        public bool BlueWins(Simulation sim)
        {
            if (Objective == ObjectiveKind.TrenchClear) return TrenchClearWins(sim);
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

        bool TrenchClearWins(Simulation sim)
        {
            bool redInTrench = false, blueUnderCommand = false;
            foreach (var c in sim.Companies)
            {
                if (c.Side == Side.Blue && c.UnderCommand) blueUnderCommand = true;
                if (c.Side == Side.Red && c.UnderCommand && InTrench(sim, c)) redInTrench = true;
            }
            return blueUnderCommand && !redInTrench;
        }

        /// <summary>trench_clear：塹壕に配置した赤の中隊が全て、指揮下にないか盤上にいない（崩した）。</summary>
        public bool TrenchUnitsBroken(Simulation sim)
        {
            foreach (var name in TrenchUnits)
            {
                var c = sim.Find(name);
                if (c != null && c.OnBoard && c.UnderCommand) return false;
            }
            return true;
        }

        /// <summary>
        /// trench_clear：塹壕を確保している（塹壕のマスに中心がある指揮下の青の中隊がいて、指揮下の赤の中隊がいない）。
        /// 勝ちの判定には使わない（戦闘詳報の「確保した時刻」）。
        /// </summary>
        public bool TrenchSecured(Simulation sim)
        {
            bool blue = false, red = false;
            foreach (var c in sim.Companies)
            {
                if (!c.UnderCommand || !InTrench(sim, c)) continue;
                if (c.Side == Side.Blue) blue = true;
                else red = true;
            }
            return blue && !red;
        }

        /// <summary>
        /// 作戦目標の達成（V1）：青の第一目標は村の奪取・塹壕の確保（<see cref="BlueWins"/>）、第二目標は塹壕の敵の排除
        /// （塹壕に配置した赤の中隊が全て、指揮下にないか盤上にいない）。赤の目標（村の死守）は第一目標の裏。
        /// 会戦の終わり（90分の時点）に呼ぶ。
        /// </summary>
        public ObjectiveResult EvaluateObjectives(Simulation sim) => new ObjectiveResult(BlueWins(sim), TrenchUnitsBroken(sim));
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
            // 側面の範囲で攻撃する（P1e の4。逆襲は突出した敵の側面を突く）。範囲なので、目標が向きを変えれば選び直す。
            // 赤の本部から伝令で届く（P2a。判断枠は数えない）
            sim.SendScriptOrder(Order.Attack(r3.Id, target.Id, AttackAspect.Flank));
            sim.LogScriptEvent(tickStart + sim.Params.TickSec, "reserve_counterattack", r3, target);
        }
    }
}

namespace Battalion.Sim
{
    /// <summary>
    /// trench_clear の時刻の記録（戦闘詳報と教程の通し実行）：塹壕の赤を崩した時刻（<see cref="MapScenario.TrenchUnitsBroken"/> に
    /// 初めてなった刻み）と、確保した時刻（<see cref="MapScenario.TrenchSecured"/> に初めてなった刻み）。毎刻みの後に呼ぶ。読むだけ。
    /// </summary>
    public sealed class TrenchClearTimes
    {
        readonly MapScenario _sc;
        public double? BrokenSec, SecuredSec;

        public TrenchClearTimes(MapScenario sc) => _sc = sc;

        public void Observe(Simulation sim)
        {
            if (!BrokenSec.HasValue && _sc.TrenchUnitsBroken(sim)) BrokenSec = sim.Time;
            if (!SecuredSec.HasValue && _sc.TrenchSecured(sim)) SecuredSec = sim.Time;
        }
    }
}
