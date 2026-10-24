using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Battalion.Sim
{
    /// <summary>作戦目標の達成（V1）。</summary>
    public readonly struct ObjectiveResult
    {
        /// <summary>青の第一目標：村の奪取（今の勝ちの条件。<see cref="MapScenario.BlueWins"/>）。</summary>
        public readonly bool Primary;
        /// <summary>青の第二目標：塹壕の敵の排除（塹壕に配置した赤の中隊が全て、指揮下にないか盤上にいない）。</summary>
        public readonly bool Secondary;

        public ObjectiveResult(bool primary, bool secondary)
        {
            Primary = primary;
            Secondary = secondary;
        }

        /// <summary>赤の目標：村の死守（青の第一目標の裏）。</summary>
        public bool RedHolds => !Primary;
    }

    /// <summary>会戦の結果の記録（V1）。V2 の作戦の探索がそのまま読む。<see cref="ToJson"/> で JSON にする。</summary>
    public sealed class BattleRecord
    {
        /// <summary>中隊ごとの結果。時刻は秒（会戦の初めからの経過）。</summary>
        public sealed class CompanyRecord
        {
            public string Name;
            public Side Side;
            public int InitialStrength;
            public int FinalStrength;
            public CompanyState FinalState;
            /// <summary>壊走した時刻（再集結して戻り、また壊走すれば複数）。</summary>
            public readonly List<double> RoutSec = new List<double>();
            /// <summary>降伏した・盤上から離脱した時刻（なければ null）。</summary>
            public double? SurrenderSec;
            public double? LeftBattleSec;
            /// <summary>指揮下を初めて離れた時刻（壊走・降伏の早い方。なければ null）。</summary>
            public double? FirstBrokenSec => RoutSec.Count > 0 ? (SurrenderSec.HasValue ? Math.Min(RoutSec[0], SurrenderSec.Value) : RoutSec[0]) : SurrenderSec;
        }

        /// <summary>撃ち合った組：射手が目標を、いつからいつまで、何刻み撃ち、何人倒したか（射手から目標への向きごとではなく、組ごと）。</summary>
        public sealed class FirePair
        {
            public string Shooter;
            public string Target;
            public Side ShooterSide;
            public double FirstSec;
            public double LastSec;
            /// <summary>撃った刻みの数（1刻み 0.5秒）と、倒した兵の数。</summary>
            public int Ticks;
            public int Casualties;
        }

        /// <summary>通り道の1点（30秒ごと）。</summary>
        public readonly struct PathPoint
        {
            public readonly double Sec;
            public readonly double X, Y;
            public readonly CompanyState State;

            public PathPoint(double sec, double x, double y, CompanyState state)
            {
                Sec = sec;
                X = x;
                Y = y;
                State = state;
            }
        }

        public string Scenario;
        public int Stage;
        /// <summary>作戦の名前など、記録を作った側が付ける見出し（なければ null）。</summary>
        public string Plan;
        public ulong Seed;
        /// <summary>会戦の終わりの時刻（秒。90分か、どちらかが全て盤上から消えた時）。</summary>
        public double EndSec;
        /// <summary>終わりの時点の作戦目標の達成。</summary>
        public ObjectiveResult Objectives;
        /// <summary>初めて第一目標の条件を満たした時刻（なければ null）。</summary>
        public double? FirstPrimarySec;
        /// <summary>塹壕に配置した赤の中隊。</summary>
        public readonly List<string> TrenchUnits = new List<string>();
        public int BlueInitial, BlueLost, RedInitial, RedLost;
        public readonly List<CompanyRecord> Companies = new List<CompanyRecord>();
        public readonly List<FirePair> FirePairs = new List<FirePair>();
        /// <summary>青の各中隊の通り道（30秒ごと。会戦の初めを含む。盤上にいる間）。</summary>
        public readonly Dictionary<string, List<PathPoint>> Paths = new Dictionary<string, List<PathPoint>>();

        public string ToJson()
        {
            var sb = new StringBuilder(8192);
            sb.Append('{');
            Str(sb, "scenario", Scenario, true);
            Num(sb, "stage", Stage);
            Str(sb, "plan", Plan);
            sb.Append(",\"seed\":").Append(Seed.ToString(CultureInfo.InvariantCulture));
            Num(sb, "end_sec", EndSec);
            sb.Append(",\"objectives\":{");
            Bool(sb, "primary", Objectives.Primary, true);
            Bool(sb, "secondary", Objectives.Secondary);
            Bool(sb, "red_holds", Objectives.RedHolds);
            NumOrNull(sb, "first_primary_sec", FirstPrimarySec);
            sb.Append(",\"trench_units\":[");
            for (int k = 0; k < TrenchUnits.Count; k++)
            {
                if (k > 0) sb.Append(',');
                JsonLineBuilder.AppendString(sb, TrenchUnits[k]);
            }
            sb.Append("]}");
            sb.Append(",\"losses\":{\"blue\":{");
            Num(sb, "initial", BlueInitial, true);
            Num(sb, "lost", BlueLost);
            sb.Append("},\"red\":{");
            Num(sb, "initial", RedInitial, true);
            Num(sb, "lost", RedLost);
            sb.Append("}}");

            sb.Append(",\"companies\":[");
            for (int k = 0; k < Companies.Count; k++)
            {
                var c = Companies[k];
                if (k > 0) sb.Append(',');
                sb.Append('{');
                Str(sb, "name", c.Name, true);
                Str(sb, "side", Names.Of(c.Side));
                Num(sb, "initial_strength", c.InitialStrength);
                Num(sb, "final_strength", c.FinalStrength);
                Str(sb, "final_state", Names.Of(c.FinalState));
                sb.Append(",\"rout_sec\":[");
                for (int i = 0; i < c.RoutSec.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    JsonNumber.Append(sb, c.RoutSec[i]);
                }
                sb.Append(']');
                NumOrNull(sb, "surrender_sec", c.SurrenderSec);
                NumOrNull(sb, "left_battle_sec", c.LeftBattleSec);
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"fire_pairs\":[");
            for (int k = 0; k < FirePairs.Count; k++)
            {
                var f = FirePairs[k];
                if (k > 0) sb.Append(',');
                sb.Append('{');
                Str(sb, "shooter", f.Shooter, true);
                Str(sb, "target", f.Target);
                Str(sb, "shooter_side", Names.Of(f.ShooterSide));
                Num(sb, "first_sec", f.FirstSec);
                Num(sb, "last_sec", f.LastSec);
                Num(sb, "ticks", f.Ticks);
                Num(sb, "casualties", f.Casualties);
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"paths\":{");
            bool first = true;
            foreach (var kv in Paths)
            {
                if (!first) sb.Append(',');
                first = false;
                JsonLineBuilder.AppendString(sb, kv.Key);
                sb.Append(":[");
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var p = kv.Value[i];
                    if (i > 0) sb.Append(',');
                    sb.Append('[');
                    JsonNumber.Append(sb, p.Sec);
                    sb.Append(',');
                    JsonNumber.Append(sb, p.X);
                    sb.Append(',');
                    JsonNumber.Append(sb, p.Y);
                    sb.Append(',');
                    JsonLineBuilder.AppendString(sb, Names.Of(p.State));
                    sb.Append(']');
                }
                sb.Append(']');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        static void Key(StringBuilder sb, string key, bool first)
        {
            if (!first) sb.Append(',');
            JsonLineBuilder.AppendString(sb, key);
            sb.Append(':');
        }

        static void Str(StringBuilder sb, string key, string v, bool first = false)
        {
            Key(sb, key, first);
            JsonLineBuilder.AppendString(sb, v);
        }

        static void Num(StringBuilder sb, string key, double v, bool first = false)
        {
            Key(sb, key, first);
            JsonNumber.Append(sb, v);
        }

        static void NumOrNull(StringBuilder sb, string key, double? v)
        {
            Key(sb, key, false);
            if (v.HasValue) JsonNumber.Append(sb, v.Value);
            else sb.Append("null");
        }

        static void Bool(StringBuilder sb, string key, bool v, bool first = false)
        {
            Key(sb, key, first);
            sb.Append(v ? "true" : "false");
        }
    }

    /// <summary>
    /// 会戦を見て記録を作る（V1）。会戦の状態は読むだけで、ログにも何も足さない（会戦の結果とログを変えない）。
    /// 会戦の初めに1回と、刻みを進めるたびに <see cref="Observe"/> を呼び、終わりに <see cref="Finish"/>。
    /// </summary>
    public sealed class BattleRecorder
    {
        public const double PathIntervalSec = 30.0;

        readonly Simulation _sim;
        readonly MapScenario _scenario;
        readonly BattleRecord _rec;
        readonly CompanyState[] _prev;
        readonly Dictionary<(int, int), BattleRecord.FirePair> _pairs = new Dictionary<(int, int), BattleRecord.FirePair>();
        double _nextPath;
        long _lastFireTick = -1;

        /// <param name="scenario">作戦目標を判定するシナリオ（null なら目標と村を取った時刻は判定しない）。</param>
        public BattleRecorder(Simulation sim, MapScenario scenario, ulong seed)
        {
            _sim = sim;
            _scenario = scenario;
            _rec = new BattleRecord { Scenario = scenario?.Name, Stage = scenario?.Stage ?? 0, Seed = seed };
            if (scenario != null) _rec.TrenchUnits.AddRange(scenario.TrenchUnits);
            _prev = new CompanyState[sim.Companies.Count];
            for (int k = 0; k < _prev.Length; k++)
            {
                var c = sim.Companies[k];
                _prev[k] = c.State;
                _rec.Companies.Add(new BattleRecord.CompanyRecord { Name = c.Name, Side = c.Side, InitialStrength = c.InitialStrength });
                if (c.Side == Side.Blue) _rec.Paths[c.Name] = new List<BattleRecord.PathPoint>();
            }
            _nextPath = 0;
        }

        /// <summary>今の刻み（直前に終わった刻み）を見る。会戦の初め（刻みを進める前）にも1回呼ぶ。</summary>
        public void Observe()
        {
            double t = _sim.Time;
            var cs = _sim.Companies;

            // 村を取った時刻（初めて第一目標の条件を満たした時）
            if (_scenario != null && !_rec.FirstPrimarySec.HasValue && _sim.Tick > 0 && _scenario.BlueWins(_sim)) _rec.FirstPrimarySec = t;

            // 崩れた・降伏した・離脱した
            for (int k = 0; k < cs.Count; k++)
            {
                var c = cs[k];
                var r = _rec.Companies[k];
                var was = _prev[k];
                if (c.State != was)
                {
                    if (c.State == CompanyState.Routed && (was == CompanyState.Normal || was == CompanyState.Shaken)) r.RoutSec.Add(t);
                    if (c.State == CompanyState.Surrendered) r.SurrenderSec = t;
                    if (c.State == CompanyState.LeftBattle) r.LeftBattleSec = t;
                    _prev[k] = c.State;
                }
            }

            // 撃ち合った組（同じ刻みを二度数えない）
            if (_sim.Tick != _lastFireTick)
            {
                _lastFireTick = _sim.Tick;
                foreach (var f in _sim.LastTickFire)
                {
                    if (!_pairs.TryGetValue((f.Shooter, f.Target), out var p))
                    {
                        var s = cs[f.Shooter];
                        p = new BattleRecord.FirePair { Shooter = s.Name, Target = cs[f.Target].Name, ShooterSide = s.Side, FirstSec = t };
                        _pairs[(f.Shooter, f.Target)] = p;
                        _rec.FirePairs.Add(p);
                    }
                    p.LastSec = t;
                    p.Ticks++;
                    p.Casualties += f.Casualties;
                }
            }

            // 青の通り道（30秒ごと）
            if (t >= _nextPath - 1e-9)
            {
                foreach (var c in cs)
                    if (c.Side == Side.Blue && c.OnBoard)
                        _rec.Paths[c.Name].Add(new BattleRecord.PathPoint(t, c.Position.X, c.Position.Y, c.State));
                _nextPath += PathIntervalSec;
            }
        }

        /// <summary>会戦の終わり：終わりの時点の目標の達成と損害を入れて、記録を返す。</summary>
        public BattleRecord Finish()
        {
            _rec.EndSec = _sim.Time;
            if (_scenario != null) _rec.Objectives = _scenario.EvaluateObjectives(_sim);
            var cs = _sim.Companies;
            for (int k = 0; k < cs.Count; k++)
            {
                var c = cs[k];
                var r = _rec.Companies[k];
                r.FinalStrength = c.Strength;
                r.FinalState = c.State;
                if (c.Side == Side.Blue)
                {
                    _rec.BlueInitial += c.InitialStrength;
                    _rec.BlueLost += c.InitialStrength - c.Strength;
                }
                else
                {
                    _rec.RedInitial += c.InitialStrength;
                    _rec.RedLost += c.InitialStrength - c.Strength;
                }
            }
            return _rec;
        }
    }
}
