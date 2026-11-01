using System;
using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>射撃の要請の進み（P3）：電話・準備・射撃中・終わり・取りやめ。</summary>
    public enum FireMissionState { Phone, Preparing, Firing, Done, Cancelled }

    /// <summary>要請の出どころ（P3）：プレイヤー（青）の要請、赤の本部の阻止射撃、赤の本部の陣地の前。</summary>
    public enum FireMissionKind { Request, Barrier, Front }

    /// <summary>要請の確かめの結果（P3）。</summary>
    public enum FireCheck { Ok, NoArtillery, NoRounds, NotAllowedPoint, TooEarly, Overlap, BadDuration }

    /// <summary>1回の射撃の要請（P3）。</summary>
    public sealed class FireMission
    {
        public int Id;
        public Side Side;
        /// <summary>狙った地点（要請の地点）。着弾の中心は <see cref="Impact"/>。</summary>
        public Vec2 Aim;
        /// <summary>計画射撃点への要請（準備が短く、誤差は初めから小さい）。</summary>
        public bool Planned;
        public FireMissionKind Kind;
        /// <summary>赤の陣地の前の射撃の目標にした青の中隊（なければ −1）。</summary>
        public int TargetId = -1;
        public double RequestTime;
        /// <summary>電話が砲兵に着く時刻と、初弾が落ちる時刻（秒）。</summary>
        public double PhoneDoneTime;
        public double FirstImpactTime;
        /// <summary>射撃の長さ（秒。伸ばすと増える）。</summary>
        public double DurationSec;
        /// <summary>時刻を指定した要請か。</summary>
        public bool Timed;
        public FireMissionState State;
        /// <summary>撃った秒数と撃った弾数。</summary>
        public double FiredSec;
        public double RoundsFired;
        /// <summary>狙った地点からの着弾の中心のずれ（初弾で決まり、観測の修正で直る）。</summary>
        public Vec2 Offset;
        public bool Corrected;
        /// <summary>射撃が終わった時刻（取りやめを含む。まだなら null）。</summary>
        public double? EndedTime;

        public double EndTime => FirstImpactTime + DurationSec;
        public Vec2 Impact => Aim + Offset;
        public bool Active => State == FireMissionState.Phone || State == FireMissionState.Preparing || State == FireMissionState.Firing;
        /// <summary>今の誤差（着弾の中心と狙った地点の距離）。</summary>
        public double ErrorM => Offset.Length;
    }

    /// <summary>側ごとの野砲中隊（P3）：残りの弾と計画射撃点。</summary>
    public sealed class Battery
    {
        public int InitialRounds;
        public double Rounds;
        public readonly List<Vec2> PlannedPoints = new List<Vec2>();
    }

    // P3：砲兵（仕様「P3：砲兵（確定）」）。砲は地図の外にあり、射程は地図全域。砲そのものは撃たれない（対砲兵は扱わない）。
    // 要請は電話で砲兵に届き（伝令ではない）、準備の後に撃つ。効果は損害の同時解決（Resolve）で小銃の射撃と一緒に入れる。
    public sealed partial class Simulation
    {
        SimParams.ArtilleryParams Art => _p.Artillery;

        const ulong ArtilleryStream = 0xa471e7UL;
        Pcg32 _artyRng;
        readonly Battery[] _batteries = new Battery[2];
        readonly List<FireMission> _missions = new List<FireMission>();
        /// <summary>マスごとの累計の着弾の秒数（砲撃痕）。</summary>
        double[] _craterSec;
        double _nextRedArtyEval;

        /// <summary>野砲中隊を置く（地図ファイルの artillery）。artillery.enabled が false なら置かない。</summary>
        public void SetArtillery(Side side, int rounds)
        {
            if (!Art.Enabled) return;
            _batteries[(int)side] = new Battery { InitialRounds = rounds, Rounds = rounds };
        }

        /// <summary>野砲中隊を除く（比べ用の「赤の砲兵なし」の版。会戦前に呼ぶ）。</summary>
        public void ClearArtillery(Side side) => _batteries[(int)side] = null;

        public Battery BatteryOf(Side side) => _batteries[(int)side];
        public bool HasArtillery(Side side) => _batteries[(int)side] != null;
        public IReadOnlyList<FireMission> FireMissions => _missions;

        /// <summary>残りの弾（表示は切り上げた整数）。砲兵がなければ 0。</summary>
        public double RoundsLeft(Side side) => _batteries[(int)side]?.Rounds ?? 0;

        /// <summary>
        /// 計画射撃点を登録する（会戦前だけ。artillery.planned_points_max 箇所まで）。登録できたら true。
        /// 赤の守りの計画の点は <see cref="SetDefensePlan"/> で載せる。
        /// </summary>
        public bool AddPlannedPoint(Side side, Vec2 p)
        {
            var b = BatteryOf(side);
            if (b == null || Tick > 0 || b.PlannedPoints.Count >= Art.PlannedPointsMax || !_map.Contains(p)) return false;
            b.PlannedPoints.Add(p);
            return true;
        }

        /// <summary>計画射撃点を消す（会戦前だけ）。</summary>
        public bool RemovePlannedPoint(Side side, int index)
        {
            var b = BatteryOf(side);
            if (b == null || Tick > 0 || index < 0 || index >= b.PlannedPoints.Count) return false;
            b.PlannedPoints.RemoveAt(index);
            return true;
        }

        /// <summary>地点が計画射撃点か（1m 以内。表示は計画射撃点の近くのクリックを点に寄せる）。点の番号、なければ −1。</summary>
        public int PlannedPointIndex(Side side, Vec2 p)
        {
            var b = BatteryOf(side);
            if (b == null) return -1;
            for (int k = 0; k < b.PlannedPoints.Count; k++)
                if (Vec2.Distance(b.PlannedPoints[k], p) <= 1.0) return k;
            return -1;
        }

        /// <summary>
        /// 要請できる地点か：計画射撃点か、指揮下の味方のどれかが気付いている盤上の敵の中隊から artillery.request_radius_m 以内。
        /// 霧を入れた設定（fog.enabled）の青は、敵の中隊の代わりに地図に出ている報告の駒から測る。
        /// </summary>
        public bool RequestablePoint(Side side, Vec2 p)
        {
            if (PlannedPointIndex(side, p) >= 0) return true;
            double r = Art.RequestRadiusM + 1e-9;
            if (side == Side.Blue && _p.Fog.Enabled)
            {
                foreach (var i in _intel)
                    if (LookOf(i, Time) != PieceLook.Forgotten && Vec2.Distance(i.Position, p) <= r) return true;
                return false;
            }
            foreach (var e in _companies)
                if (e.Side != side && e.OnBoard && Vec2.Distance(e.Position, p) <= r && SeenBySide(e, side)) return true;
            return false;
        }

        /// <summary>今要請したときに初弾が落ちる一番早い時刻（電話と準備）。</summary>
        public double EarliestImpact(Side side, Vec2 p) =>
            Time + Art.PhoneSec + (PlannedPointIndex(side, p) >= 0 ? Art.PrepPlannedSec : Art.PrepSec);

        /// <summary>
        /// 要請を確かめる：砲兵と弾があり、長さが選べる値で、地点が要請できる地点で、時刻の指定が準備の時間より後で、
        /// 撃つ時間がほかの要請（取りやめ・終わりを除く）と重ならない。
        /// </summary>
        public FireCheck CheckFireRequest(Side side, Vec2 p, int minutes, double? startAt = null)
        {
            var b = BatteryOf(side);
            if (b == null) return FireCheck.NoArtillery;
            if (b.Rounds <= 1e-9) return FireCheck.NoRounds;
            if (!Art.DurationsMin.Contains(minutes)) return FireCheck.BadDuration;
            if (!RequestablePoint(side, p)) return FireCheck.NotAllowedPoint;
            double earliest = EarliestImpact(side, p);
            if (startAt.HasValue && startAt.Value < earliest - 1e-9) return FireCheck.TooEarly;
            double from = startAt ?? earliest, to = from + minutes * 60.0;
            foreach (var m in _missions)
                if (m.Side == side && m.Active && from < m.EndTime - 1e-9 && m.FirstImpactTime < to - 1e-9) return FireCheck.Overlap;
            return FireCheck.Ok;
        }

        /// <summary>
        /// 射撃を要請する（電話で砲兵に届く。判断枠は使わない）。<paramref name="startAt"/> を指定すれば、その時刻に初弾が落ちるように
        /// 前もって送る。確かめに通らなければ null。
        /// </summary>
        public FireMission RequestFire(Side side, Vec2 p, int minutes, double? startAt = null) =>
            RequestFire(side, p, minutes, startAt, FireMissionKind.Request, -1);

        FireMission RequestFire(Side side, Vec2 p, int minutes, double? startAt, FireMissionKind kind, int targetId)
        {
            if (CheckFireRequest(side, p, minutes, startAt) != FireCheck.Ok) return null;
            double t = Time;
            var m = new FireMission
            {
                Id = _missions.Count, Side = side, Aim = p, Planned = PlannedPointIndex(side, p) >= 0, Kind = kind, TargetId = targetId,
                RequestTime = t, PhoneDoneTime = t + Art.PhoneSec, FirstImpactTime = startAt ?? EarliestImpact(side, p),
                DurationSec = minutes * 60.0, Timed = startAt.HasValue, State = FireMissionState.Phone,
            };
            _missions.Add(m);
            if (_log.Enabled)
            {
                var ev = ArtyEv(t, "artillery_request", m).Add("x", p.X).Add("y", p.Y).Add("minutes", minutes).Add("planned", m.Planned)
                    .Add("impact_at", m.FirstImpactTime);
                if (kind != FireMissionKind.Request) ev.Add("reason", kind == FireMissionKind.Barrier ? "barrier" : "front");
                if (targetId >= 0) ev.Add("target", _companies[targetId].Name);
                _log.Write(ev.ToString());
            }
            return m;
        }

        /// <summary>射撃を取りやめる（電話・準備中・射撃中。すぐ止む）。取りやめたら true。</summary>
        public bool CancelFire(FireMission m)
        {
            if (m == null || !m.Active) return false;
            m.State = FireMissionState.Cancelled;
            m.EndedTime = Time;
            if (_log.Enabled) _log.Write(ArtyEv(Time, "artillery_cancel", m).Add("rounds_left", Math.Round(RoundsLeft(m.Side), 3)).ToString());
            return true;
        }

        /// <summary>射撃を1分伸ばす（撃っている間。弾が残っていて、次の要請と重ならない）。伸ばせたら true。</summary>
        public bool ExtendFire(FireMission m)
        {
            if (m == null || m.State != FireMissionState.Firing || RoundsLeft(m.Side) <= 1e-9) return false;
            double to = m.EndTime + 60.0;
            foreach (var o in _missions)
                if (o != m && o.Side == m.Side && o.Active && m.EndTime < o.EndTime - 1e-9 && o.FirstImpactTime < to - 1e-9) return false;
            m.DurationSec += 60.0;
            if (_log.Enabled) _log.Write(ArtyEv(Time, "artillery_extend", m).Add("minutes", m.DurationSec / 60).ToString());
            return true;
        }

        JsonLineBuilder ArtyEv(double t, string type, FireMission m) =>
            new JsonLineBuilder().Add("t", t).Add("type", type).Add("side", SideName(m.Side)).Add("mission", m.Id);

        // ---------------------------------------------------------------- 毎刻み（損害の同時解決の中）

        /// <summary>
        /// 射撃の進みを1刻み進め、着弾の円に中心が入った中隊の損害（<paramref name="cas"/>）と、損害の分を除く士気の減り
        /// （<paramref name="morale"/>。練度の減りやすさを掛ける前）を積む。円の中の中隊は <see cref="Company.LastShelledTime"/> を今にする。
        /// 刻みの開始時点の状態で計算する（同時解決）。
        /// </summary>
        void ResolveArtillery(double t, int[] cas, double[] morale)
        {
            if (_missions.Count == 0) return;
            double dt = _p.TickSec, dtMin = dt / 60.0;
            foreach (var m in _missions)
            {
                if (!m.Active) continue;
                if (t <= m.PhoneDoneTime + 1e-9) { m.State = FireMissionState.Phone; continue; }
                if (t <= m.FirstImpactTime + 1e-9) { m.State = FireMissionState.Preparing; continue; }
                var b = BatteryOf(m.Side);
                if (m.State != FireMissionState.Firing)
                {
                    m.State = FireMissionState.Firing;
                    // 初弾の誤差：向きは一様に散らす。計画射撃点は初めから小さい
                    m.Corrected = m.Planned;
                    m.Offset = Scatter(m.Planned ? Art.ErrorObservedM : Art.ErrorInitialM);
                    if (_log.Enabled)
                        _log.Write(ArtyEv(t, "artillery_start", m).Add("x", m.Impact.X).Add("y", m.Impact.Y).Add("error_m", Math.Round(m.ErrorM, 3)).ToString());
                }
                if (b.Rounds <= 1e-9)
                {
                    EndFire(m, t, "no_rounds");
                    continue;
                }
                double r = Math.Min(b.Rounds, Art.RoundsPerMin * dtMin);
                b.Rounds -= r;
                if (b.Rounds < 1e-9) b.Rounds = 0;
                m.RoundsFired += r;
                m.FiredSec += dt;
                // 観測の修正：射撃を始めて observe_after_sec の後、狙った地点を見ている指揮下の味方がいれば小さい誤差に直る
                if (!m.Corrected && m.FiredSec >= Art.ObserveAfterSec - 1e-9 && Observed(m))
                {
                    m.Corrected = true;
                    m.Offset = Scatter(Art.ErrorObservedM);
                    if (_log.Enabled)
                        _log.Write(ArtyEv(t, "artillery_corrected", m).Add("x", m.Impact.X).Add("y", m.Impact.Y).Add("error_m", Math.Round(m.ErrorM, 3)).ToString());
                }
                Impact(m, t, dtMin, cas, morale);
                if (m.FiredSec >= m.DurationSec - 1e-9) EndFire(m, t, "done");
                else if (b.Rounds <= 1e-9) EndFire(m, t, "no_rounds");
            }
        }

        void EndFire(FireMission m, double t, string reason)
        {
            m.State = FireMissionState.Done;
            m.EndedTime = t;
            if (_log.Enabled)
                _log.Write(ArtyEv(t, "artillery_end", m).Add("reason", reason).Add("rounds_fired", Math.Round(m.RoundsFired, 3))
                    .Add("rounds_left", Math.Round(RoundsLeft(m.Side), 3)).ToString());
        }

        /// <summary>長さ <paramref name="len"/> で、向きを一様に散らしたずれ。</summary>
        Vec2 Scatter(double len)
        {
            if (_artyRng == null) _artyRng = new Pcg32(_seed, ArtilleryStream);
            return Vec2.FromAngle(_artyRng.NextDouble() * 2 * Math.PI) * len;
        }

        /// <summary>狙った地点を、要請した側の指揮下の中隊のどれかが見ている（視線が通る）か。</summary>
        bool Observed(FireMission m)
        {
            foreach (var c in _companies)
                if (c.Side == m.Side && c.OnBoard && c.UnderCommand && LineOfSight.Visible(_map, _p.Sight, c.Position, m.Aim)) return true;
            return false;
        }

        /// <summary>1刻みの着弾：円に中心が入った中隊（敵味方を問わない）の損害と士気、砲撃痕のマスの累計。</summary>
        void Impact(FireMission m, double t, double dtMin, int[] cas, double[] morale)
        {
            var at = m.Impact;
            double rad = Art.RadiusM + 1e-9;
            foreach (var c in _companies)
            {
                if (!c.OnBoard || Vec2.Distance(c.Position, at) > rad) continue;
                double mult = ArtilleryDamageMult(c);
                // 兵力の割合は初めの兵力に対して（仕様の表：平地の士気は毎分 −15 のまま、約5分で崩れる）
                double lambda = Art.LossPctPerMin / 100.0 * c.InitialStrength * mult * dtMin;
                if (_artyRng == null) _artyRng = new Pcg32(_seed, ArtilleryStream);
                cas[c.Id] += _artyRng.NextPoisson(lambda);
                morale[c.Id] += Art.MoraleExtraPerMin * mult * dtMin;
                c.LastShelledTime = t;
            }
            if (_craterSec == null) _craterSec = new double[_map.Width * _map.Height];
            Visibility.ForCellsWithin(_map, at, Art.RadiusM, (i, j, center) =>
            {
                int k = j * _map.Width + i;
                if (_craterSec[k] >= Art.CraterAfterSec - 1e-9) return;
                _craterSec[k] += _p.TickSec;
                // 平地だけを砲撃痕にする（村・森・川・橋・浅瀬・塹壕は変えない）
                if (_craterSec[k] >= Art.CraterAfterSec - 1e-9 && _map.TerrainAt(i, j) == TerrainMap.Plain && _map.SetOverlay(i, j, TerrainMap.Crater)
                    && _log.Enabled)
                    _log.Write(new JsonLineBuilder().Add("t", t).Add("type", "crater").Add("i", i).Add("j", j).ToString());
            });
        }

        /// <summary>マスの累計の着弾の秒数（砲撃痕の判定。表示・テスト用）。</summary>
        public double CraterSecAt(int i, int j) => _craterSec == null ? 0 : _craterSec[j * _map.Width + i];

        /// <summary>
        /// 砲撃の被弾倍率：地形（塹壕は止まっているときだけ）・陣地構築・浅い掩体・縦隊の倍率。上から落ちるので陣地の向きは見ず、
        /// 開けた所を動く倍率（R1）と接近戦（R4）は掛けない。
        /// </summary>
        public double ArtilleryDamageMult(Company c) => TerrainDamageMult(c, null, false, artillery: true);

        /// <summary>砲撃の制圧（P3）：着弾の円の中にいる間と、出てから suppression.release_sec の間。しきい値は見ない。</summary>
        void UpdateArtySuppression(Company c, double t)
        {
            bool on = t - c.LastShelledTime < S.ReleaseSec - 1e-9;
            if (on == c.ArtySuppressed) return;
            c.ArtySuppressed = on;
            if (_log.Enabled) _log.Write(Ev(t, on ? "arty_suppressed" : "arty_suppression_end", c).Add("side", SideName(c.Side)).ToString());
        }

        // ---------------------------------------------------------------- 赤の本部の砲撃（E1 の守りの計画に足す）

        /// <summary>
        /// 赤の本部の砲撃の判断（artillery.red_eval_sec ごと）。撃っていない時に、次の順で1つだけ要請する。
        /// 1. 阻止射撃：計画射撃点から request_radius_m 以内に、赤のどれかが気付いている指揮下の青がいる → その点へ。
        /// 2. 陣地の前：前線・守備の赤から red_front_radius_m 以内に、気付いている指揮下の青がいて、まわり red_safety_m に赤がいない
        ///    → その青の位置へ（前線・守備の赤に一番近い青）。
        /// どちらも、着弾の中心から red_safety_m 以内に赤の中隊がいる地点へは撃たない。どちらもなければ撃たない（弾を残す）。
        /// </summary>
        void UpdateRedArtillery(double t)
        {
            var side = _defenseSide;
            var b = BatteryOf(side);
            if (b == null || _defense == null || !D.Enabled) return;
            if (t < _nextRedArtyEval - 1e-9) return;
            _nextRedArtyEval = t + Art.RedEvalSec;
            if (b.Rounds <= 1e-9) return;
            foreach (var m in _missions)
                if (m.Side == side && m.Active) return;
            int minutes = (int)Art.RedFireMin;

            foreach (var p in b.PlannedPoints)
            {
                if (OwnNear(side, p)) continue;
                foreach (var e in _companies)
                {
                    if (e.Side == side || !e.OnBoard || !e.UnderCommand || Vec2.Distance(e.Position, p) > Art.RequestRadiusM + 1e-9) continue;
                    if (!SeenBySide(e, side)) continue;
                    if (RequestFire(side, p, minutes, null, FireMissionKind.Barrier, e.Id) != null) return;
                }
            }

            Company best = null;
            double bestD = double.MaxValue;
            foreach (var e in _companies)
            {
                if (e.Side == side || !e.OnBoard || !e.UnderCommand || !SeenBySide(e, side) || OwnNear(side, e.Position)) continue;
                foreach (var r in _companies)
                {
                    if (r.Side != side || !r.OnBoard || !r.UnderCommand || (r.Role != DefenseRole.Front && r.Role != DefenseRole.Garrison)) continue;
                    double d = Vec2.Distance(r.Position, e.Position);
                    if (d > Art.RedFrontRadiusM + 1e-9 || d >= bestD) continue;
                    best = e;
                    bestD = d;
                }
            }
            if (best != null) RequestFire(side, best.Position, minutes, null, FireMissionKind.Front, best.Id);
        }

        /// <summary>地点から red_safety_m 以内に、その側の盤上の中隊がいるか（誤射を避ける）。</summary>
        bool OwnNear(Side side, Vec2 p)
        {
            foreach (var c in _companies)
                if (c.Side == side && c.OnBoard && Vec2.Distance(c.Position, p) <= Art.RedSafetyM + 1e-9) return true;
            return false;
        }
    }
}
