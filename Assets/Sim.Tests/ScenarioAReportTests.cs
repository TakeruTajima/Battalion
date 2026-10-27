using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// シナリオAの両ステージで、概算シミュレーション（docs/reference/sketch/battle_sketch_v3.py）と同じ5つの作戦を
    /// 各20回（シード1〜20）回し、結果を概算の結果と並べて出力する。合否は付けない（例外なく終わることだけを確かめる）。
    /// </summary>
    [Category("Slow")]
    public class ScenarioAReportTests
    {
        public const int Runs = 20;

        /// <summary>通し実行に使うパラメータを差し替える（原因の切り分け用。null なら sim_params.json）。</summary>
        public static SimParams ParamsForRuns;

        /// <summary>作戦の命令の種類（P1e で拘束・攻撃を足した）。</summary>
        public enum PlannedKind { Advance, PinFar, PinNear, AttackFlank, AttackSecure }

        /// <summary>作戦の命令：時刻（秒）、中隊、目的地、到着後の向き（度）。拘束・攻撃では目標の中隊。</summary>
        public readonly struct PlannedOrder
        {
            public readonly double AtSec;
            public readonly string Unit;
            public readonly double X, Y, FacingDeg;
            public readonly PlannedKind Kind;
            public readonly string Target;
            /// <summary>条件つきの命令（P1e.1）：この中隊が指揮下でなくなったら出す（null なら時刻だけで出す）。</summary>
            public readonly string WhenBroken;
            /// <summary>第2段（前の段の完了で）に村の中心への前進を足す（E1 の3回目の直しの4。両翼包囲）。</summary>
            public readonly bool ThenVillage;

            public PlannedOrder(double atSec, string unit, double x, double y, double facingDeg,
                PlannedKind kind = PlannedKind.Advance, string target = null, string whenBroken = null, bool thenVillage = false)
            {
                WhenBroken = whenBroken;
                ThenVillage = thenVillage;
                AtSec = atSec;
                Unit = unit;
                X = x;
                Y = y;
                FacingDeg = facingDeg;
                Kind = kind;
                Target = target;
            }
        }

        static PlannedOrder O(double at, string u, double x, double y, double f) => new PlannedOrder(at, u, x, y, f);
        static PlannedOrder T(double at, string u, PlannedKind kind, string target) => new PlannedOrder(at, u, 0, 0, 0, kind, target);

        /// <summary>
        /// 「拘束＋迂回」（P1e の6）：B2 が R1 を拘束し、B1 が R1 を側面の範囲で攻撃、B4 が 2分後に続く。B3 は B2 の南で火力支援。
        /// 攻撃は追撃なし（確保）。R1 が指揮下でなくなったら、拘束役も含む指揮下の青の全中隊が村の中心へ前進する（P1e.2。
        /// P1e.1 では B3 と B4 だけ）。
        /// </summary>
        public static PlannedOrder[] PinAndFlank(int stage, bool near)
        {
            double fx = stage == 1 ? 1450 : 1350, fy = stage == 1 ? 800 : 900;
            return new[]
            {
                T(0, "B2", near ? PlannedKind.PinNear : PlannedKind.PinFar, "R1"),
                T(0, "B1", PlannedKind.AttackFlank, "R1"),
                T(120, "B4", PlannedKind.AttackFlank, "R1"),
                O(0, "B3", fx, fy, 0),
                new PlannedOrder(0, "B1", MapScenario.VillageCenter.X, MapScenario.VillageCenter.Y, 0, whenBroken: "R1"),
                new PlannedOrder(0, "B2", MapScenario.VillageCenter.X, MapScenario.VillageCenter.Y, 0, whenBroken: "R1"),
                new PlannedOrder(0, "B3", MapScenario.VillageCenter.X, MapScenario.VillageCenter.Y, 0, whenBroken: "R1"),
                new PlannedOrder(0, "B4", MapScenario.VillageCenter.X, MapScenario.VillageCenter.Y, 0, whenBroken: "R1"),
            };
        }

        /// <summary>
        /// 「拘束＋猪突の攻撃（確保）」（P2c.1。猪突の独断の出方を見るため）：B2 が R1 を遠い拘束、B3（猪突）が R1 を
        /// 範囲の指定なし・崩れたら確保で攻撃、B1 が R1 を側面の範囲で攻撃、B4 は「拘束＋迂回」の B3 の位置で火力支援。
        /// R1 が指揮下でなくなったら、B3 以外の指揮下の青の中隊が村の中心へ前進する（B3 は確保の字面のまま、独断を見る）。
        /// </summary>
        public static PlannedOrder[] PinAndRecklessAttack(int stage)
        {
            double fx = stage == 1 ? 1450 : 1350, fy = stage == 1 ? 800 : 900;
            var v = MapScenario.VillageCenter;
            return new[]
            {
                T(0, "B2", PlannedKind.PinFar, "R1"),
                T(0, "B3", PlannedKind.AttackSecure, "R1"),
                T(0, "B1", PlannedKind.AttackFlank, "R1"),
                O(0, "B4", fx, fy, 0),
                new PlannedOrder(0, "B1", v.X, v.Y, 0, whenBroken: "R1"),
                new PlannedOrder(0, "B2", v.X, v.Y, 0, whenBroken: "R1"),
                new PlannedOrder(0, "B4", v.X, v.Y, 0, whenBroken: "R1"),
            };
        }

        /// <summary>battle_sketch_v3.py の plans と同じ（名前、順番、時刻、目的地、向き）。</summary>
        public static List<(string name, PlannedOrder[] orders)> Plans(int stage)
        {
            double fx = stage == 1 ? 1450 : 1350; // 稜線の塹壕から約375m
            var plans = new List<(string, PlannedOrder[])>
            {
                ("総員突撃", new[] { O(0, "B1", 1975, 1150, 0), O(0, "B2", 1975, 1000, 0), O(0, "B3", 1975, 850, 0), O(0, "B4", 1975, 1000, 0) }),
                ("正面火力戦", new[] { O(0, "B1", fx, 1200, 0), O(0, "B2", fx, 1000, 0), O(0, "B3", fx, 800, 0), O(0, "B4", 900, 1000, 0) }),
            };
            if (stage == 1)
            {
                plans.Add(("側面攻撃（1個中隊）", new[] { O(0, "B2", 1450, 1000, 0), O(0, "B1", 1975, 1250, -90), O(0, "B3", 900, 800, 0), O(0, "B4", 900, 1150, 0) }));
                plans.Add(("側面攻撃（2個中隊）", new[] { O(0, "B2", 1450, 1050, 0), O(0, "B3", 1450, 850, 0), O(0, "B1", 2075, 1300, -90), O(0, "B4", 1875, 1300, -90) }));
                plans.Add(("両翼包囲（鶴翼）", new[] { O(0, "B2", 1450, 1000, 0), O(0, "B1", 1975, 1250, -90), O(0, "B3", 1975, 750, 90), O(1200, "B4", 1450, 1200, 0) }));
            }
            else
            {
                plans.Add(("側面攻撃（1個中隊）", new[] { O(0, "B2", 1350, 1100, 0), O(0, "B3", 900, 900, 0), O(0, "B4", 900, 1100, 0), O(0, "B1", 1725, 1400, -90) }));
                plans.Add(("側面攻撃（2個中隊）", new[] { O(0, "B2", 1350, 1100, 0), O(0, "B3", 1350, 900, 0), O(0, "B1", 1825, 1400, -90), O(0, "B4", 1625, 1400, -90) }));
                plans.Add(("両翼包囲（鶴翼）", new[] { O(0, "B2", 1350, 1000, 0), O(0, "B1", 1725, 1400, -90), O(0, "B3", 1725, 600, 90), O(1200, "B4", 1350, 1200, 0) }));
            }
            return plans;
        }

        /// <summary>
        /// 「拘束＋迂回（段階つき）」（P2b）：全て会戦開始前の命令書（伝令なし）。目標は R1。
        /// B2：第1段 R1 を遠い拘束、第2段 村の中心へ前進（信号弾 白）。
        /// B1・B4：第1段 北の待機地点へ前進（R1 から約 550m の見えない所）、第2段 R1 を側面の範囲で攻撃（信号弾 緑）、第3段 村の中心へ前進（信号弾 白）。
        /// B3：第1段 B2 の南で火力支援、第2段 村の中心へ前進（信号弾 白）。
        /// 信号弾は、緑を迂回役が待機地点に着く頃の T+18:00 に、白を R1 が指揮下でなくなった刻みに打つ。
        /// </summary>
        public static void StagedPinAndFlankStart(Simulation sim, int stage) => StagedPinAndFlankStart(sim, stage, Discretion.Delegate);

        /// <summary>「拘束＋迂回（段階つき）」の命令書。<paramref name="d"/> は全ての段の裁量（P2c）。</summary>
        public static void StagedPinAndFlankStart(Simulation sim, int stage, Discretion d)
        {
            double fx = stage == 1 ? 1450 : 1350, fy = stage == 1 ? 800 : 900;
            var waitB1 = stage == 1 ? new Vec2(1500, 1450) : new Vec2(1400, 1550);
            var waitB4 = stage == 1 ? new Vec2(1300, 1450) : new Vec2(1200, 1550);
            var village = MapScenario.VillageCenter;
            var r1 = sim.Find("R1").Id;
            int b1 = sim.Find("B1").Id, b2 = sim.Find("B2").Id, b3 = sim.Find("B3").Id, b4 = sim.Find("B4").Id;
            var white = StageTrigger.OnFlare(FlareColor.White);
            var green = StageTrigger.OnFlare(FlareColor.Green);
            OrderSheet S(params Stage[] s) => new OrderSheet(s.Select(x => x.With(d)));
            sim.SendSheet(S(new Stage(Order.Pin(b2, r1, PinRange.Far), StageTrigger.None),
                new Stage(Order.Advance(b2, village, 0), white)));
            foreach (var (id, wait) in new[] { (b1, waitB1), (b4, waitB4) })
                sim.SendSheet(S(new Stage(Order.Advance(id, wait, -90 * Angles.DegToRad), StageTrigger.None),
                    new Stage(Order.Attack(id, r1, AttackAspect.Flank), green),
                    new Stage(Order.Advance(id, village, 0), white)));
            sim.SendSheet(S(new Stage(Order.Advance(b3, new Vec2(fx, fy), 0), StageTrigger.None),
                new Stage(Order.Advance(b3, village, 0), white)));
        }

        /// <summary>「拘束＋迂回（段階つき）」の信号弾（毎刻みの前に呼ぶ）。</summary>
        public static void StagedPinAndFlankTick(Simulation sim)
        {
            if (sim.Time >= 18 * 60) sim.FireFlare(Side.Blue, FlareColor.Green);
            if (!sim.Find("R1").UnderCommand) sim.FireFlare(Side.Blue, FlareColor.White);
        }

        public const string StagedPlanName = "拘束＋迂回（段階つき）";
        public const string RecklessPlanName = "拘束＋猪突の攻撃（確保）";

        public const string EnvelopmentPlanName = "両翼包囲（鶴翼）";

        /// <summary>
        /// 5作戦に「拘束＋迂回」の遠い版・近い版などを足したもの（P1e の6）。両翼包囲（鶴翼）は、4つの命令の後に第2段
        /// （前の段の完了で）として村の中心への前進を足す（E1 の3回目の直しの4。概算シミュレーションとは比べない）。
        /// <paramref name="legacyEnvelopment"/> が true なら、両翼包囲は概算と同じ4つの命令だけ（前の版のログと比べるテスト用）。
        /// </summary>
        public static List<(string name, PlannedOrder[] orders)> PlansWithPinAndFlank(int stage, bool legacyEnvelopment = false)
        {
            var plans = Plans(stage);
            if (!legacyEnvelopment)
            {
                int k = plans.FindIndex(p => p.name == EnvelopmentPlanName);
                plans[k] = (EnvelopmentPlanName, plans[k].orders.Select(o =>
                    new PlannedOrder(o.AtSec, o.Unit, o.X, o.Y, o.FacingDeg, o.Kind, o.Target, o.WhenBroken, thenVillage: true)).ToArray());
            }
            plans.Add(("拘束＋迂回（遠い拘束）", PinAndFlank(stage, false)));
            plans.Add(("拘束＋迂回（近い拘束）", PinAndFlank(stage, true)));
            plans.Add((RecklessPlanName, PinAndRecklessAttack(stage)));
            plans.Add((VillageAdvancePlanName, VillageAdvance()));
            return plans;
        }

        public const string VillageAdvancePlanName = "村への前進（全中隊）";

        /// <summary>
        /// 「村への前進（全中隊）」（V1）：全中隊に村の中心への前進だけを命じる。素直な作戦の基準
        /// （P2c.1.2 のプレイで、これだけでおおむね勝てると分かったため）。
        /// </summary>
        public static PlannedOrder[] VillageAdvance()
        {
            var v = MapScenario.VillageCenter;
            return new[] { O(0, "B1", v.X, v.Y, 0), O(0, "B2", v.X, v.Y, 0), O(0, "B3", v.X, v.Y, 0), O(0, "B4", v.X, v.Y, 0) };
        }

        public sealed class RunResult
        {
            /// <summary>会戦の結果の記録（V1。作戦目標の達成、損害、撃ち合った組、通り道など）。</summary>
            public BattleRecord Record;
            public bool Win;
            public double EndMin;
            public double BlueLoss;
            /// <summary>会戦中に一度でも壊走（降伏を含む）した中隊の数。</summary>
            public int BlueBroken, RedBroken;
            /// <summary>終了時に指揮下にない（壊走中・再集結中・降伏・離脱）中隊の数。</summary>
            public int BlueOutAtEnd, RedOutAtEnd;
            public int RedSurrendered;
            /// <summary>
            /// 赤が全て同時に指揮下にない（壊走中・降伏・離脱）状態になった最初の時刻（分）。概算シミュレーションは壊走した中隊を
            /// 戻さず、この状態で会戦を終えて勝ちとするので、その勝ち方に当たる。なければ null。
            /// </summary>
            public double? RedAllBrokenMin;
            /// <summary>自律行動・独断の回数（青。P2c）。キーは independent の kind（pursue／attack／flank／cover／no_pursuit）と
            /// auto_cover（独断でない物陰）、scrape_start、enter_position、independent_end。赤は red: を付ける。</summary>
            public readonly Dictionary<string, int> Autonomy = new Dictionary<string, int>();
            /// <summary>中隊ごとの最初の壊走の時刻、最初の向き直りの時刻、攻撃で発起点に着いた（方角を固定した）時刻（分）。</summary>
            public readonly Dictionary<string, double> FirstRoutMin = new Dictionary<string, double>();
            public readonly Dictionary<string, double> FirstTurnMin = new Dictionary<string, double>();
            public readonly Dictionary<string, double> AttackFixedMin = new Dictionary<string, double>();
            /// <summary>E1 の出来事：前線の後退の時刻（分）、即座の逆襲・奪回の命令・奪回の攻撃・占領の乱れの回数。</summary>
            public readonly List<double> FallbackMin = new List<double>();
            public int Counterattacks, RetakeOrders, RetakeAttacks, Disarrays;
        }

        sealed class RoutCounter : IEventSink
        {
            public readonly HashSet<string> Routed = new HashSet<string>();
            /// <summary>最初の壊走・向き直りの時刻（秒）。</summary>
            public readonly Dictionary<string, double> RoutAt = new Dictionary<string, double>();
            public readonly Dictionary<string, double> TurnAt = new Dictionary<string, double>();
            public readonly Dictionary<string, int> Autonomy = new Dictionary<string, int>();
            /// <summary>E1 の出来事（前線の後退の時刻（秒）と、逆襲・奪回・占領の乱れの回数）。</summary>
            public readonly List<double> FallbackSec = new List<double>();
            public int Counterattacks, RetakeOrders, RetakeAttacks, Disarrays;
            public bool Enabled => true;

            static readonly string[] AutonomyTypes =
                { "\"type\":\"independent\"", "\"type\":\"independent_end\"", "\"type\":\"auto_cover\"", "\"type\":\"scrape_start\"", "\"type\":\"enter_position\"" };

            public void Write(string jsonLine)
            {
                // E1 の出来事
                if (jsonLine.Contains("\"type\":\"defense_fallback\"")) FallbackSec.Add(JsonValue.Parse(jsonLine).D("t"));
                else if (jsonLine.Contains("\"type\":\"defense_counterattack\"")) Counterattacks++;
                else if (jsonLine.Contains("\"type\":\"retake_order\"")) RetakeOrders++;
                else if (jsonLine.Contains("\"type\":\"retake_attack\"")) RetakeAttacks++;
                else if (jsonLine.Contains("\"type\":\"occupation_disarray\"")) Disarrays++;
                // 自律行動・独断の回数（P2c）
                foreach (var key in AutonomyTypes)
                {
                    if (jsonLine.IndexOf(key, StringComparison.Ordinal) < 0) continue;
                    var v = JsonValue.Parse(jsonLine);
                    string type = v["type"].String;
                    string name = type == "independent" ? v["kind"].String
                        : type == "auto_cover" && v["independent"].Bool ? null
                        : type;
                    if (name == null) break;
                    if (v["side"].String != "blue") name = "red:" + name;
                    Autonomy[name] = (Autonomy.TryGetValue(name, out int n) ? n : 0) + 1;
                    break;
                }
                // "type":"rout" と "type":"turn_start" の行だけ見る（"unit" はその直後に出る）
                Note(jsonLine, "\"type\":\"rout\",\"unit\":\"", RoutAt, Routed);
                Note(jsonLine, "\"type\":\"turn_start\",\"unit\":\"", TurnAt, null);
            }

            static void Note(string line, string key, Dictionary<string, double> at, HashSet<string> set)
            {
                int k = line.IndexOf(key, StringComparison.Ordinal);
                if (k < 0) return;
                int s = k + key.Length;
                string unit = line.Substring(s, line.IndexOf('"', s) - s);
                set?.Add(unit);
                if (!at.ContainsKey(unit)) at[unit] = JsonValue.Parse(line).D("t");
            }
        }

        public static RunResult RunOne(MapScenario sc, PlannedOrder[] plan, ulong seed) => RunOne(sc, plan, seed, null, null);

        /// <summary>作戦を1回回す。<paramref name="start"/> は会戦開始前に1回、<paramref name="tick"/> は毎刻みの前に呼ぶ（段階つきの作戦。P2b）。</summary>
        public static RunResult RunOne(MapScenario sc, PlannedOrder[] plan, ulong seed, Action<Simulation> start, Action<Simulation> tick) =>
            RunOne(sc, plan, seed, start, tick, Discretion.Delegate);

        /// <summary>作戦を1回回す。<paramref name="d"/> は作戦の命令の裁量（P2c。段階つきの作戦は <paramref name="start"/> の中で渡す）。</summary>
        public static RunResult RunOne(MapScenario sc, PlannedOrder[] plan, ulong seed, Action<Simulation> start, Action<Simulation> tick, Discretion d)
        {
            var counter = new RoutCounter();
            var sim = sc.Build(ParamsForRuns ?? TestUtil.Params, seed, counter);
            start?.Invoke(sim);
            // 会戦の結果の記録（V1。読むだけで、会戦とログを変えない）
            var recorder = new BattleRecorder(sim, sc, seed);
            recorder.Observe();
            var orders = plan.Where(o => o.WhenBroken == null).OrderBy(o => o.AtSec).ToList();
            var conditional = plan.Where(o => o.WhenBroken != null).ToList();
            int next = 0;
            double? redAllBroken = null;
            var fixedAt = new Dictionary<string, double>();
            double endSec = sc.TimeLimitMin * 60;
            while (sim.Time < endSec)
            {
                // 命令は伝令で出す（P2a）。時刻 0（会戦開始前）の命令は伝令を使わずに届く。判断枠がなければ、空くまで毎刻み出し直す
                while (next < orders.Count && orders[next].AtSec <= sim.Time && Issue(sim, orders[next], d)) next++;
                for (int k = conditional.Count - 1; k >= 0; k--)
                {
                    var o = conditional[k];
                    if (o.AtSec > sim.Time || sim.Find(o.WhenBroken).UnderCommand) continue;
                    if (Issue(sim, o, d)) conditional.RemoveAt(k);
                }
                tick?.Invoke(sim);
                sim.Step();
                recorder.Observe();
                foreach (var c in sim.Companies)
                    if (c.AttackFixed && c.CurrentOrder.Type == OrderType.Attack && !fixedAt.ContainsKey(c.Name)) fixedAt[c.Name] = sim.Time / 60;
                if (!redAllBroken.HasValue && sim.Companies.Where(c => c.Side == Side.Red).All(c => !c.UnderCommand))
                    redAllBroken = sim.Time / 60;
                bool redLeft = sim.Companies.Any(c => c.Side == Side.Red && c.OnBoard);
                bool blueLeft = sim.Companies.Any(c => c.Side == Side.Blue && c.OnBoard);
                if (!redLeft || !blueLeft) break;
            }
            RunProgress.Tick();
            var result = Summarize(sc, sim, counter, redAllBroken, fixedAt);
            result.Record = recorder.Finish();
            return result;
        }

        /// <summary>作戦の命令を伝令で出す（P2a）。出せた（判断枠があった）ら true。</summary>
        public static bool Issue(Simulation sim, PlannedOrder o) =>
            o.ThenVillage ? Issue(sim, o, Discretion.Delegate) : sim.SendOrder(ToOrder(sim, o)) != SendResult.NoSlot;

        /// <summary>
        /// 作戦の命令を、裁量 <paramref name="d"/> の1段の命令書として伝令で出す（P2c）。<see cref="PlannedOrder.ThenVillage"/> なら、
        /// 第2段（前の段の完了で）に村の中心への前進を足した2段の命令書。
        /// </summary>
        public static bool Issue(Simulation sim, PlannedOrder o, Discretion d)
        {
            if (o.ThenVillage)
                return sim.SendSheet(new OrderSheet(new[]
                {
                    new Stage(ToOrder(sim, o), StageTrigger.None, d),
                    new Stage(Order.Advance(sim.Find(o.Unit).Id, MapScenario.VillageCenter, 0), StageTrigger.Complete, d),
                })) != SendResult.NoSlot;
            return d == Discretion.Delegate
                ? Issue(sim, o)
                : sim.SendSheet(new OrderSheet(new[] { new Stage(ToOrder(sim, o), StageTrigger.None, d) })) != SendResult.NoSlot;
        }

        public static Order ToOrder(Simulation sim, PlannedOrder o)
        {
            var c = sim.Find(o.Unit);
            switch (o.Kind)
            {
                case PlannedKind.PinFar: return Order.Pin(c.Id, sim.Find(o.Target).Id, PinRange.Far);
                case PlannedKind.PinNear: return Order.Pin(c.Id, sim.Find(o.Target).Id, PinRange.Near);
                case PlannedKind.AttackFlank: return Order.Attack(c.Id, sim.Find(o.Target).Id, AttackAspect.Flank);
                case PlannedKind.AttackSecure: return Order.Attack(c.Id, sim.Find(o.Target).Id, AttackAspect.None, AttackOnBreak.Secure);
                default: return Order.Advance(c.Id, new Vec2(o.X, o.Y), o.FacingDeg * Angles.DegToRad);
            }
        }

        static RunResult Summarize(MapScenario sc, Simulation sim, RoutCounter counter, double? redAllBroken, Dictionary<string, double> fixedAt)
        {
            var blue = sim.Companies.Where(c => c.Side == Side.Blue).ToList();
            var red = sim.Companies.Where(c => c.Side == Side.Red).ToList();
            var result = new RunResult
            {
                Win = sc.BlueWins(sim),
                EndMin = sim.Time / 60,
                BlueLoss = blue.Sum(c => c.InitialStrength - c.Strength) / (double)blue.Sum(c => c.InitialStrength),
                BlueBroken = blue.Count(c => counter.Routed.Contains(c.Name)),
                RedBroken = red.Count(c => counter.Routed.Contains(c.Name)),
                BlueOutAtEnd = blue.Count(c => !c.UnderCommand),
                RedOutAtEnd = red.Count(c => !c.UnderCommand),
                RedSurrendered = red.Count(c => c.State == CompanyState.Surrendered),
                RedAllBrokenMin = redAllBroken,
            };
            foreach (var kv in counter.RoutAt) result.FirstRoutMin[kv.Key] = kv.Value / 60;
            foreach (var kv in counter.TurnAt) result.FirstTurnMin[kv.Key] = kv.Value / 60;
            foreach (var kv in fixedAt) result.AttackFixedMin[kv.Key] = kv.Value;
            foreach (var kv in counter.Autonomy) result.Autonomy[kv.Key] = kv.Value;
            foreach (double s in counter.FallbackSec) result.FallbackMin.Add(s / 60);
            result.Counterattacks = counter.Counterattacks;
            result.RetakeOrders = counter.RetakeOrders;
            // 小出しの版は命令がそのまま攻撃
            result.RetakeAttacks = (ParamsForRuns ?? TestUtil.Params).Defense.RetakeAssemble ? counter.RetakeAttacks : counter.RetakeOrders;
            result.Disarrays = counter.Disarrays;
            return result;
        }

        /// <summary>
        /// P2c：作戦ごとに、任せた版と全中隊を厳命にした版（各20回）の結果と、独断・自律行動の1回あたりの回数を並べる。
        /// 独断：猪突の追撃（確保の字面で追う）・続けて攻撃、機敏の回り込み、慎重の後退（物陰へ）・追撃せず、独断の限度で停止。
        /// 全ての中隊長：物陰へ寄る、浅い掩体（掘り始め）、崩した敵の陣地に入る。赤は物陰・浅い掩体・陣地の合計。
        /// </summary>
        [Test]
        public void DelegatedVersusStrictWithAutonomyCounts()
        {
            using var progress = RunProgress.Begin("任せる・厳命と独断の回数", 2 * AllPlanRuns());
            TestContext.WriteLine(AutonomyTable());
            Assert.Pass();
        }

        /// <summary>
        /// P2c.1：P2c の報告で切り分けた3作戦（ステージ1の遠い拘束・近い拘束、ステージ2の総員突撃）を、撃てる目標がないときの向きの
        /// 規則を止めた版（向く相手の距離と記憶を 0）で回す。任せる・厳命の両方。
        /// </summary>
        [Test]
        public void ThreePlansWithoutAutoFacing()
        {
            using var progress = RunProgress.Begin("向きの規則を止めた版", 3 * 2 * Runs);
            var off = TestUtil.ParamsWith(("\"face_enemy_radius_m\": 600", "\"face_enemy_radius_m\": 0"),
                ("\"face_shooter_memory_sec\": 30", "\"face_shooter_memory_sec\": 0"), ("\"face_near_radius_m\": 400", "\"face_near_radius_m\": 0"));
            var sb = new StringBuilder();
            sb.AppendLine("| ステージ | 作戦 | 裁量 | 勝率 | 赤が全て崩れた割合 | 青の損害率 | 崩れた青 | 崩れた赤 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            ParamsForRuns = off;
            try
            {
                foreach (var (stage, name) in new[] { (1, "拘束＋迂回（遠い拘束）"), (1, "拘束＋迂回（近い拘束）"), (2, "総員突撃") })
                {
                    var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                    var plan = PlansWithPinAndFlank(stage).First(p => p.name == name).orders;
                    foreach (var d in new[] { Discretion.Delegate, Discretion.Strict })
                    {
                        var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, plan, (ulong)s, null, null, d)).ToList();
                        sb.AppendLine($"| {stage} | {name} | {(d == Discretion.Strict ? "厳命" : "任せる")} | {rs.Average(r => r.Win ? 1.0 : 0.0):0.00} | " +
                                      $"{rs.Average(r => r.RedAllBrokenMin.HasValue ? 1.0 : 0.0):0.00} | {rs.Average(r => r.BlueLoss):0.000} | " +
                                      $"{rs.Average(r => r.BlueBroken):0.00} | {rs.Average(r => r.RedBroken):0.00} |");
                    }
                }
            }
            finally
            {
                ParamsForRuns = null;
            }
            TestContext.WriteLine("向きの規則を止めた版");
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }

        /// <summary>
        /// 目標ごとの達成率の欄（V1）：「第一目標 | 第二目標 | 両方 | 村を取った時刻の中央値」。
        /// 村を取った時刻は、初めて第一目標の条件を満たした時刻の中央値（満たした回だけ。「（回数/20）」を添える）。
        /// </summary>
        static string ObjectiveCells(List<RunResult> rs)
        {
            double p = rs.Average(r => r.Record.Objectives.Primary ? 1.0 : 0.0);
            double s = rs.Average(r => r.Record.Objectives.Secondary ? 1.0 : 0.0);
            double both = rs.Average(r => r.Record.Objectives.Primary && r.Record.Objectives.Secondary ? 1.0 : 0.0);
            var taken = rs.Where(r => r.Record.FirstPrimarySec.HasValue).Select(r => r.Record.FirstPrimarySec.Value / 60).ToList();
            string t = taken.Count == 0 ? "—" : $"{TestUtil.Median(taken):0.0}分（{taken.Count}/{rs.Count}）";
            return $"{p:0.00} | {s:0.00} | {both:0.00} | {t}";
        }

        /// <summary>
        /// 環境変数 BATTALION_RECORD_DIR があれば、会戦ごとの記録（JSON）をそこへ書き出す
        /// （ファイル名：stage{ステージ}_{作戦}_{裁量}_seed{シード}.json。作戦の名前は記録の "plan" に入る）。
        /// </summary>
        static void WriteRecords(List<RunResult> rs, string plan, string discretion)
        {
            string dir = Environment.GetEnvironmentVariable("BATTALION_RECORD_DIR");
            foreach (var r in rs) r.Record.Plan = plan;
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            foreach (var r in rs)
            {
                string safe = string.Concat(plan.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                File.WriteAllText(Path.Combine(dir, $"stage{r.Record.Stage}_{safe}_{discretion}_seed{r.Record.Seed}.json"), r.Record.ToJson(),
                    new UTF8Encoding(false));
            }
        }

        static string AutonomyTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| ステージ | 作戦 | 裁量 | 勝率 | 第一目標 | 第二目標 | 両方 | 村を取った時刻の中央値 | 青の損害率 | 崩れた青 | 崩れた赤 | 猪突 追撃 | 猪突 続けて攻撃 | 機敏 回り込み | 慎重 後退 | 慎重 追撃せず | 限度で停止 | 物陰 | 浅い掩体 | 陣地に入る | 赤の自律 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                int st = stage;
                foreach (var d in new[] { Discretion.Delegate, Discretion.Strict })
                {
                    var all = PlansWithPinAndFlank(stage).Select(p => (p.name, p.orders, (Action<Simulation>)null, (Action<Simulation>)null)).ToList();
                    var dd = d;
                    all.Add((StagedPlanName, new PlannedOrder[0], s => StagedPinAndFlankStart(s, st, dd), StagedPinAndFlankTick));
                    foreach (var (name, plan, start, tick) in all)
                    {
                        var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, plan, (ulong)s, start, tick, d)).ToList();
                        WriteRecords(rs, name, d == Discretion.Strict ? "strict" : "delegate");
                        string A(params string[] keys) =>
                            rs.Average(r => keys.Sum(k => r.Autonomy.TryGetValue(k, out int n) ? n : 0)).ToString("0.00");
                        sb.AppendLine($"| {stage} | {name} | {(d == Discretion.Strict ? "厳命" : "任せる")} | " +
                                      $"{rs.Average(r => r.Win ? 1.0 : 0.0):0.00} | " + ObjectiveCells(rs) + $" | {rs.Average(r => r.BlueLoss):0.000} | " +
                                      $"{rs.Average(r => r.BlueBroken):0.00} | {rs.Average(r => r.RedBroken):0.00} | " +
                                      $"{A("pursue")} | {A("attack")} | {A("flank")} | {A("cover")} | {A("no_pursuit")} | {A("independent_end")} | " +
                                      $"{A("auto_cover")} | {A("scrape_start")} | {A("enter_position")} | " +
                                      $"{A("red:auto_cover", "red:scrape_start", "red:enter_position")} |");
                    }
                }
            }
            return "回数は1回の会戦あたりの平均（20回）。慎重の後退は独断で 100m を越える・後ろへ寄る物陰。物陰は独断でないもの\n" + sb;
        }

        /// <summary>両ステージの作戦の数（段階つきを含む）×20回。</summary>
        static int AllPlanRuns() => new[] { 1, 2 }.Sum(st => PlansWithPinAndFlank(st).Count + 1) * Runs;

        [Test]
        public void FivePlansOnBothStages()
        {
            using var progress = RunProgress.Begin("通し実行の表", AllPlanRuns());
            var sketch = JsonValue.Parse(File.ReadAllText(TestUtil.PathOf("docs/reference/sketch/battle_sketch_v3_results.json")));
            var sb = new StringBuilder();
            var events = new StringBuilder();
            events.AppendLine("| ステージ | 作戦 | 前線の後退（回/会戦） | 後退の時刻の中央値 | 即座の逆襲（回/会戦） | 奪回の命令（回/会戦） | 奪回の攻撃（回/会戦） | 奪回して守り切った割合 | 占領の乱れ（回/会戦） |");
            events.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            sb.AppendLine("| ステージ | 作戦 | 勝率 | 第一目標 | 第二目標 | 両方 | 村を取った時刻の中央値 | 赤が全て崩れた割合 | 青の損害率 | 崩れた青（終了時） | 崩れた赤（終了時） | 降伏した赤 | 終了時刻の中央値 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                var all = PlansWithPinAndFlank(stage).Select(p => (p.name, p.orders, (Action<Simulation>)null, (Action<Simulation>)null)).ToList();
                int st = stage;
                all.Add((StagedPlanName, new PlannedOrder[0], s => StagedPinAndFlankStart(s, st), StagedPinAndFlankTick));
                foreach (var (name, plan, start, tick) in all)
                {
                    var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, plan, (ulong)s, start, tick)).ToList();
                    WriteRecords(rs, name, "delegate");
                    // 「拘束＋迂回」は概算シミュレーションにない。両翼包囲は村へ入る段を足したので比べない（E1 の3回目の直しの4）
                    bool hasSketch = sketch.TryGet($"stage{stage}:{name}", out var k) && name != EnvelopmentPlanName;
                    string Ref(string key, string fmt) => hasSketch ? "［" + k.D(key).ToString(fmt) + "］" : "";
                    sb.AppendLine($"| {stage} | {name} | " +
                                  $"{rs.Average(r => r.Win ? 1.0 : 0.0):0.00}{Ref("win", "0.00")} | " +
                                  ObjectiveCells(rs) + " | " +
                                  $"{rs.Average(r => r.RedAllBrokenMin.HasValue ? 1.0 : 0.0):0.00} | " +
                                  $"{rs.Average(r => r.BlueLoss):0.000}{Ref("blue_loss", "0.000")} | " +
                                  $"{rs.Average(r => r.BlueBroken):0.00}（{rs.Average(r => r.BlueOutAtEnd):0.00}）{Ref("blue_out", "0.00")} | " +
                                  $"{rs.Average(r => r.RedBroken):0.00}（{rs.Average(r => r.RedOutAtEnd):0.00}）{Ref("red_out", "0.00")} | " +
                                  $"{rs.Average(r => r.RedSurrendered):0.00}{Ref("red_surr", "0.00")} | " +
                                  $"{TestUtil.Median(rs.Select(r => r.EndMin).ToList()):0.0}分{Ref("t_end", "0.0")} |");
                    events.AppendLine(EventRow(stage, name, rs));
                }
            }
            TestContext.WriteLine("［ ］内は概算シミュレーションの値。崩れた中隊は、会戦中に一度でも壊走した数（ ）内は終了時に指揮下にない数");
            TestContext.WriteLine(sb.ToString());
            TestContext.WriteLine("E1 の出来事の回数（1回の会戦あたりの平均、20回）。奪回して守り切った割合＝奪回の攻撃があった会戦のうち、終わりに青の勝ちでない割合");
            TestContext.WriteLine(events.ToString());
            Assert.Pass();
        }

        static string EventRow(int stage, string name, List<RunResult> rs)
        {
            var times = rs.SelectMany(r => r.FallbackMin).ToList();
            var retaken = rs.Where(r => r.RetakeAttacks > 0).ToList();
            return $"| {stage} | {name} | {rs.Average(r => r.FallbackMin.Count):0.00} | {(times.Count == 0 ? "—" : $"{TestUtil.Median(times):0.0}分")} | " +
                   $"{rs.Average(r => r.Counterattacks):0.00} | {rs.Average(r => r.RetakeOrders):0.00} | {rs.Average(r => r.RetakeAttacks):0.00} | " +
                   $"{(retaken.Count == 0 ? "—" : $"{retaken.Average(r => r.Win ? 0.0 : 1.0):0.00}（{retaken.Count}/{rs.Count}）")} | {rs.Average(r => r.Disarrays):0.00} |";
        }

        /// <summary>E1 の比べの表の主な作戦（総員突撃、村への前進、拘束＋迂回の遠い・近い、村へ入る段を足した両翼包囲）。</summary>
        static readonly string[] E1ComparePlans = { "総員突撃", VillageAdvancePlanName, "拘束＋迂回（遠い拘束）", "拘束＋迂回（近い拘束）", EnvelopmentPlanName };

        /// <summary>
        /// E1 の比べの表：主な作戦を、受け身の赤（defense・occupation とも止める）、E1 で占領の乱れなし、E1 で小出しの奪回（immediate）で
        /// 各 20回回す（E1 そのものの値は作戦ごとの表）。
        /// </summary>
        [Test]
        public void E1ComparisonTable()
        {
            var variants = new (string name, Action<SimParams> set)[]
            {
                ("受け身の赤", p => { p.Defense.Enabled = false; p.Occupation.Enabled = false; }),
                ("E1・占領の乱れなし", p => { p.Occupation.Enabled = false; }),
                ("E1・小出しの奪回", p => { p.Defense.RetakeAssemble = false; }),
            };
            using var progress = RunProgress.Begin("E1 の比べの表", variants.Length * 2 * E1ComparePlans.Length * Runs);
            var sb = new StringBuilder();
            sb.AppendLine("| ステージ | 作戦 | 版 | 勝率 | 第一目標 | 第二目標 | 両方 | 村を取った時刻の中央値 | 青の損害率 | 赤の損害率 | 前線の後退（回/会戦） | 即座の逆襲（回/会戦） | 奪回の攻撃（回/会戦） | 占領の乱れ（回/会戦） |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            try
            {
                foreach (int stage in new[] { 1, 2 })
                {
                    var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                    var plans = PlansWithPinAndFlank(stage).Where(p => E1ComparePlans.Contains(p.name)).ToList();
                    foreach (var (name, plan) in plans)
                        foreach (var (vname, set) in variants)
                        {
                            var p = SimParams.FromJson(File.ReadAllText(TestUtil.PathOf(TestUtil.ParamsRelPath)));
                            set(p);
                            ParamsForRuns = p;
                            var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, plan, (ulong)s)).ToList();
                            sb.AppendLine($"| {stage} | {name} | {vname} | {rs.Average(r => r.Win ? 1.0 : 0.0):0.00} | " + ObjectiveCells(rs) +
                                          $" | {rs.Average(r => r.BlueLoss):0.000} | {rs.Average(r => r.Record.RedLost / (double)r.Record.RedInitial):0.000} | " +
                                          $"{rs.Average(r => r.FallbackMin.Count):0.00} | {rs.Average(r => r.Counterattacks):0.00} | " +
                                          $"{rs.Average(r => r.RetakeAttacks):0.00} | {rs.Average(r => r.Disarrays):0.00} |");
                        }
                }
            }
            finally
            {
                ParamsForRuns = null;
            }
            TestContext.WriteLine("E1 の比べの表（各 20回、任せる版）。E1 そのもの（攻めを集めて出す奪回・占領の乱れあり）は作戦ごとの表");
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }

        /// <summary>
        /// 「拘束＋迂回」の経過（P1e の6）：拘束役 B2 が崩れた割合と時刻、迂回 B1・B4 が発起点に着いた時刻、R1 の向き直り、
        /// R1 が崩れた割合と時刻、R3 の逆襲の割合。時刻は起きた回の中央値（分）。
        /// </summary>
        [Test]
        public void PinAndFlankTimeline()
        {
            using var progress = RunProgress.Begin("拘束＋迂回の経過", 2 * 2 * Runs);
            var sb = new StringBuilder();
            sb.AppendLine("| ステージ | 拘束 | B2 壊走 | B1 着 | B4 着 | R1 向き直り | R1 壊走 | R2 壊走 | B1 壊走 | B4 壊走 | 勝率 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (bool near in new[] { false, true })
                {
                    var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, PinAndFlank(stage, near), (ulong)s)).ToList();
                    string Cell(Func<RunResult, Dictionary<string, double>> pick, string unit)
                    {
                        var ts = rs.Where(r => pick(r).ContainsKey(unit)).Select(r => pick(r)[unit]).ToList();
                        return ts.Count == 0 ? "0/20" : $"{ts.Count}/{Runs}・{TestUtil.Median(ts):0.0}分";
                    }
                    sb.AppendLine($"| {stage} | {(near ? "近い" : "遠い")} | {Cell(r => r.FirstRoutMin, "B2")} | " +
                                  $"{Cell(r => r.AttackFixedMin, "B1")} | {Cell(r => r.AttackFixedMin, "B4")} | " +
                                  $"{Cell(r => r.FirstTurnMin, "R1")} | {Cell(r => r.FirstRoutMin, "R1")} | {Cell(r => r.FirstRoutMin, "R2")} | " +
                                  $"{Cell(r => r.FirstRoutMin, "B1")} | {Cell(r => r.FirstRoutMin, "B4")} | " +
                                  $"{rs.Average(r => r.Win ? 1.0 : 0.0):0.00} |");
                }
            }
            TestContext.WriteLine("各欄は「起きた回数/20・その時刻の中央値」");
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }
    }
}
