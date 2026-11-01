using System;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // P3：砲兵の表示と操作（仕様「P3：砲兵（確定）」の表示）。
    // 要請：砲撃の印（G・上部バー）→ 地点をクリック（計画射撃点の近くは点に寄せる）→ 長さと時刻を選ぶ → 確定（信号弾と同じ2回押し）。
    // 会戦前は計画射撃点を登録できる（H）。着弾は誰にでも見える（赤の準備中の円は出さない）。
    public sealed partial class BattleApp
    {
        /// <summary>要請の起案：選んだ地点（null はまだ選んでいない）、射撃の長さ（分）、始める時刻（null はすぐ）。</summary>
        Vec2? _artyPoint;
        int _artyMinutes = 3;
        double? _artyAt;
        /// <summary>確定の2回押し（1回目の時刻）と、取りやめの2回押し（要請と時刻）。</summary>
        float _artyArmedAt = -100f;
        FireMission _cancelArmed;
        float _cancelArmedAt;
        /// <summary>要請できなかった理由（次の操作まで出す）。</summary>
        string _artyMessage;
        const float ArtyConfirmSec = 3f;
        /// <summary>この距離以内のクリックは計画射撃点に寄せる。登録の操作では、この距離以内の点をクリックすると消す。</summary>
        const double PlannedSnapM = 100;

        bool BlueArtillery => _sim != null && _sim.HasArtillery(Side.Blue);

        /// <summary>会戦が始まる前か（計画射撃点を登録できる）。</summary>
        bool BeforeBattle => _sim != null && _sim.Tick == 0;

        Rect ArtilleryRect => new Rect(LeftW + 8, TopH + 8, 400, ArtilleryWindowHeight());

        bool ArtilleryWindowShown =>
            BlueArtillery && _phase == Phase.Running && (_mode == Mode.Artillery || _mode == Mode.PlannedPoint || ActiveBlueMissions() > 0);

        int ActiveBlueMissions()
        {
            int n = 0;
            foreach (var m in _sim.FireMissions)
                if (m.Side == Side.Blue && m.Active) n++;
            return n;
        }

        void ResetArtilleryInput()
        {
            _artyPoint = null;
            _artyAt = null;
            _artyArmedAt = -100f;
            _cancelArmed = null;
            _artyMessage = null;
        }

        /// <summary>砲撃の印（G）：地点を選ぶ操作に入る。もう一度押すとやめる。</summary>
        void BeginArtillery()
        {
            if (!BlueArtillery || _phase != Phase.Running) return;
            if (_mode == Mode.Artillery)
            {
                _mode = Mode.None;
                ResetArtilleryInput();
                return;
            }
            _mode = Mode.Artillery;
            ResetArtilleryInput();
        }

        /// <summary>計画射撃点の登録（H。会戦前だけ）。もう一度押すとやめる。</summary>
        void BeginPlannedPoints()
        {
            if (!BlueArtillery || !BeforeBattle) return;
            _mode = _mode == Mode.PlannedPoint ? Mode.None : Mode.PlannedPoint;
            ResetArtilleryInput();
        }

        /// <summary>計画射撃点の近く（100m 以内）なら、その点に寄せる。</summary>
        Vec2 SnapToPlanned(Vec2 p)
        {
            var b = _sim.BatteryOf(Side.Blue);
            Vec2? best = null;
            double bestD = PlannedSnapM;
            foreach (var q in b.PlannedPoints)
            {
                double d = Vec2.Distance(q, p);
                if (d > bestD) continue;
                best = q;
                bestD = d;
            }
            return best ?? p;
        }

        /// <summary>地図のクリック（砲撃の地点を選ぶ・計画射撃点を置く／消す）。</summary>
        void ArtilleryClick(Vec2 at)
        {
            if (_mode == Mode.PlannedPoint)
            {
                var b = _sim.BatteryOf(Side.Blue);
                for (int k = 0; k < b.PlannedPoints.Count; k++)
                    if (Vec2.Distance(b.PlannedPoints[k], at) <= PlannedSnapM)
                    {
                        _sim.RemovePlannedPoint(Side.Blue, k);
                        return;
                    }
                if (!_sim.AddPlannedPoint(Side.Blue, at)) _artyMessage = $"計画射撃点は {_params.Artillery.PlannedPointsMax} 箇所まで";
                return;
            }
            _artyPoint = SnapToPlanned(at);
            _artyAt = null;
            _artyArmedAt = -100f;
            _artyMessage = null;
        }

        /// <summary>要請の確定（2回押し）。2回目で要請し、要請できなければ理由を出す。</summary>
        void ConfirmArtillery()
        {
            if (!_artyPoint.HasValue) return;
            float now = Time.unscaledTime;
            var check = _sim.CheckFireRequest(Side.Blue, _artyPoint.Value, _artyMinutes, _artyAt);
            if (check != FireCheck.Ok)
            {
                _artyMessage = CheckText(check);
                _artyArmedAt = -100f;
                return;
            }
            if (now - _artyArmedAt > ArtyConfirmSec)
            {
                _artyArmedAt = now;
                return;
            }
            _sim.RequestFire(Side.Blue, _artyPoint.Value, _artyMinutes, _artyAt);
            _mode = Mode.None;
            ResetArtilleryInput();
        }

        bool ArtyArmed => Time.unscaledTime - _artyArmedAt <= ArtyConfirmSec;

        static string CheckText(FireCheck c)
        {
            switch (c)
            {
                case FireCheck.NoArtillery: return "砲兵がない";
                case FireCheck.NoRounds: return "弾がない";
                case FireCheck.NotAllowedPoint: return "要請できない地点（気付いている敵から 200m より遠く、計画射撃点でもない）";
                case FireCheck.TooEarly: return "その時刻には間に合わない（電話と準備の時間）";
                case FireCheck.Overlap: return "ほかの要請と撃つ時間が重なる";
                case FireCheck.BadDuration: return "長さは 1／3／5分";
                default: return "";
            }
        }

        /// <summary>地点の呼び名：計画射撃点なら「計画射撃点1」、近く（200m）の敵か味方の中隊があれば「R1 の付近」、なければ座標。</summary>
        string ArtyPlace(Side side, Vec2 p)
        {
            int k = _sim.PlannedPointIndex(side, p);
            if (side == Side.Blue && k >= 0) return $"計画射撃点{k + 1}";
            Company best = null;
            double bestD = side == Side.Blue ? _params.Artillery.RequestRadiusM : 300;
            foreach (var c in _sim.Companies)
            {
                if (!c.OnBoard || (side == Side.Blue && (c.Side == Side.Blue || !ShowsActual(c)))) continue;
                if (side == Side.Red && c.Side != Side.Blue) continue;
                double d = Vec2.Distance(c.Position, p);
                if (d > bestD) continue;
                best = c;
                bestD = d;
            }
            if (best != null) return side == Side.Blue ? $"{best.Name} の付近" : $"{best.Name} の位置";
            return $"({p.X:0}, {p.Y:0})";
        }

        static string Clock(double sec)
        {
            sec = Math.Max(0, sec);
            return $"{(int)sec / 60}:{(int)sec % 60:00}";
        }

        /// <summary>要請の今の様子：「電話 0:12」「準備 1:05」「射撃中 残り 2:30」。</summary>
        string MissionStateText(FireMission m)
        {
            double t = _sim.Time;
            switch (m.State)
            {
                case FireMissionState.Phone: return $"電話 {Clock(m.PhoneDoneTime - t)}（初弾 {EventFeed.FormatTime(m.FirstImpactTime)}）";
                case FireMissionState.Preparing: return $"準備 {Clock(m.FirstImpactTime - t)}（初弾 {EventFeed.FormatTime(m.FirstImpactTime)}）";
                case FireMissionState.Firing: return $"射撃中 残り {Clock(m.EndTime - t)}" + (m.Corrected ? "" : "（観測の修正待ち）");
                default: return "";
            }
        }

        // ---------------------------------------------------------------- 上部バー・窓

        /// <summary>上部バーの砲兵の欄：残りの弾、撃っている・準備中の要請の数、砲撃・計画射撃点のボタン。</summary>
        void DrawArtilleryBar()
        {
            if (!BlueArtillery) return;
            var b = _sim.BatteryOf(Side.Blue);
            int active = ActiveBlueMissions();
            GUILayout.Label($"砲兵 {Math.Ceiling(b.Rounds - 1e-9):0}／{b.InitialRounds}発" + (active > 0 ? $"（要請 {active}）" : ""), _label, GUILayout.Width(150));
            if (_phase != Phase.Running) return;
            if (GUILayout.Button("砲撃 (G)", _mode == Mode.Artillery ? _toggleOn : _button, GUILayout.Width(72))) BeginArtillery();
            if (BeforeBattle && GUILayout.Button($"計画射撃点 {b.PlannedPoints.Count}/{_params.Artillery.PlannedPointsMax} (H)",
                    _mode == Mode.PlannedPoint ? _toggleOn : _button, GUILayout.Width(140)))
                BeginPlannedPoints();
        }

        int ArtilleryWindowHeight()
        {
            int h = 12;
            if (_mode == Mode.PlannedPoint) h += 50;
            if (_mode == Mode.Artillery) h += _artyPoint.HasValue ? 132 : 40;
            if (_artyMessage != null) h += 22;
            h += 24 * ActiveBlueMissions();
            return h;
        }

        /// <summary>砲兵の窓：計画射撃点の登録、要請の起案（長さ・時刻・確定）、撃っている・準備中の要請（伸ばす・取りやめ）。</summary>
        void DrawArtilleryWindow()
        {
            if (!ArtilleryWindowShown) return;
            var r = ArtilleryRect;
            Fill(r, new Color(0.10f, 0.11f, 0.13f, 0.95f));
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 8));
            var bat = _sim.BatteryOf(Side.Blue);
            if (_mode == Mode.PlannedPoint)
            {
                GUILayout.Label($"<b>計画射撃点の登録</b>（会戦前。{_params.Artillery.PlannedPointsMax}箇所まで、今 {bat.PlannedPoints.Count}）", _label);
                GUILayout.Label("地図をクリックで置く。点の近くをクリックで消す。準備 30秒・初めから誤差 30m", _small);
            }
            if (_mode == Mode.Artillery)
            {
                if (!_artyPoint.HasValue)
                {
                    GUILayout.Label("<b>砲撃の要請</b>：地点をクリック（赤い円は要請できない地点）", _label);
                    GUILayout.Label("気付いている敵から 200m 以内か、計画射撃点（近くのクリックは点に寄せる）", _small);
                }
                else
                {
                    var p = _artyPoint.Value;
                    bool planned = _sim.PlannedPointIndex(Side.Blue, p) >= 0;
                    GUILayout.Label($"<b>砲撃の要請</b>：{ArtyPlace(Side.Blue, p)}（{(planned ? "準備 30秒・誤差 30m" : "準備 90秒・初弾の誤差 100m")}）", _label);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("長さ", _small, GUILayout.Width(36));
                    foreach (int min in _params.Artillery.DurationsMin)
                        if (GUILayout.Button($"{min}分", _artyMinutes == min ? _toggleOn : _button, GUILayout.Width(48)))
                        {
                            _artyMinutes = min;
                            _artyArmedAt = -100f;
                        }
                    GUILayout.EndHorizontal();
                    double earliest = _sim.EarliestImpact(Side.Blue, p);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("始める", _small, GUILayout.Width(36));
                    if (GUILayout.Button("すぐ", !_artyAt.HasValue ? _toggleOn : _button, GUILayout.Width(48)))
                    {
                        _artyAt = null;
                        _artyArmedAt = -100f;
                    }
                    if (GUILayout.Button("時刻", _artyAt.HasValue ? _toggleOn : _button, GUILayout.Width(48)) && !_artyAt.HasValue)
                    {
                        _artyAt = Math.Ceiling(earliest / 60.0) * 60.0;
                        _artyArmedAt = -100f;
                    }
                    if (_artyAt.HasValue)
                    {
                        if (GUILayout.Button("−1分", _button, GUILayout.Width(44)) && _artyAt.Value - 60 >= earliest - 1e-9)
                        {
                            _artyAt -= 60;
                            _artyArmedAt = -100f;
                        }
                        GUILayout.Label($"<b>{EventFeed.FormatTime(_artyAt.Value)}</b>", _label, GUILayout.Width(70));
                        if (GUILayout.Button("+1分", _button, GUILayout.Width(44)))
                        {
                            _artyAt += 60;
                            _artyArmedAt = -100f;
                        }
                    }
                    GUILayout.EndHorizontal();
                    double first = _artyAt ?? earliest;
                    GUILayout.Label($"初弾 {EventFeed.FormatTime(first)}・止む {EventFeed.FormatTime(first + _artyMinutes * 60)}・" +
                                    $"{_artyMinutes * _params.Artillery.RoundsPerMin:0}発（残り {Math.Ceiling(bat.Rounds - 1e-9):0}発）", _small);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(ArtyArmed ? "もう一度で要請" : "要請", ArtyArmed ? _toggleOn : _button, GUILayout.Width(130))) ConfirmArtillery();
                    if (GUILayout.Button("地点を選び直す", _button, GUILayout.Width(110))) ResetArtilleryInput();
                    if (GUILayout.Button("やめる (Esc)", _button, GUILayout.Width(100)))
                    {
                        _mode = Mode.None;
                        ResetArtilleryInput();
                    }
                    GUILayout.EndHorizontal();
                }
            }
            if (_artyMessage != null)
            {
                var st = new GUIStyle(_small);
                st.normal.textColor = new Color(1f, 0.5f, 0.4f);
                GUILayout.Label(_artyMessage, st);
            }
            foreach (var m in _sim.FireMissions)
            {
                if (m.Side != Side.Blue || !m.Active) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"砲撃{m.Id + 1} {ArtyPlace(Side.Blue, m.Aim)}（{m.DurationSec / 60:0}分） {MissionStateText(m)}", _small, GUILayout.Width(250));
                if (m.State == FireMissionState.Firing && GUILayout.Button("+1分", _button, GUILayout.Width(44))) _sim.ExtendFire(m);
                bool armed = _cancelArmed == m && Time.unscaledTime - _cancelArmedAt <= ArtyConfirmSec;
                if (GUILayout.Button(armed ? "もう一度" : "取りやめ", armed ? _toggleOn : _button, GUILayout.Width(70)))
                {
                    if (armed)
                    {
                        _sim.CancelFire(m);
                        _cancelArmed = null;
                    }
                    else
                    {
                        _cancelArmed = m;
                        _cancelArmedAt = Time.unscaledTime;
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------------- 地図

        static readonly Color32 BlueFireColor = new Color32(255, 170, 60, 255);
        static readonly Color32 RedFireColor = new Color32(255, 80, 60, 255);

        /// <summary>塗った円（扇形の三角形で）。</summary>
        void Disc(Vector2 c, float r, Color32 col, int segments = 40)
        {
            for (int k = 0; k < segments; k++)
            {
                float a0 = Mathf.PI * 2 * k / segments, a1 = Mathf.PI * 2 * (k + 1) / segments;
                _shapes.Triangle(c, c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, col);
            }
        }

        static void DashedCircle(ShapeMesh s, Vector2 c, float r, float width, float dash, Color32 col, int segments = 48)
        {
            for (int k = 0; k < segments; k += 2)
            {
                float a0 = Mathf.PI * 2 * k / segments, a1 = Mathf.PI * 2 * (k + 1) / segments;
                s.Line(c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, width, col);
            }
        }

        /// <summary>
        /// 地図の砲兵：青の計画射撃点（菱形と 100m の点線の円）、青の準備中の円（点線）、着弾中の円（塗り。赤の砲撃も）、
        /// 要請の操作中のカーソルの円（要請できない地点は赤）と選んだ地点。
        /// </summary>
        void DrawArtillery(float px)
        {
            if (_sim.BatteryOf(Side.Blue) == null && _sim.BatteryOf(Side.Red) == null) return;
            float rad = (float)_params.Artillery.RadiusM;
            var bat = _sim.BatteryOf(Side.Blue);
            if (bat != null)
                foreach (var q in bat.PlannedPoints)
                {
                    var c = V(q);
                    float d = 9 * px;
                    _shapes.Quad(c + new Vector2(0, d), c + new Vector2(d, 0), c + new Vector2(0, -d), c + new Vector2(-d, 0), Palette.WithAlpha(BlueFireColor, 200));
                    DashedCircle(_shapes, c, rad, 1.2f * px, 0, Palette.WithAlpha(BlueFireColor, 110));
                }
            foreach (var m in _sim.FireMissions)
            {
                if (!m.Active) continue;
                var col = m.Side == Side.Blue ? BlueFireColor : RedFireColor;
                if (m.State == FireMissionState.Firing)
                {
                    var c = V(m.Impact);
                    Disc(c, rad, Palette.WithAlpha(col, 70));
                    _shapes.Circle(c, rad, 2.5f * px, Palette.WithAlpha(col, 230), 48);
                }
                else if (m.Side == Side.Blue)
                    DashedCircle(_shapes, V(m.Aim), rad, 2f * px, 0, Palette.WithAlpha(col, 210));
            }
            if (_mode == Mode.Artillery && _phase == Phase.Running)
            {
                if (_artyPoint.HasValue)
                {
                    _shapes.Circle(V(_artyPoint.Value), rad, 3f * px, Palette.Selected, 48);
                }
                else if (!MouseOverUi())
                {
                    var p = SnapToPlanned(MouseWorld());
                    bool ok = _sim.RequestablePoint(Side.Blue, p);
                    _shapes.Circle(V(p), rad, 2f * px, ok ? new Color32(255, 255, 255, 200) : new Color32(255, 70, 60, 220), 48);
                }
            }
        }

        /// <summary>地図の砲兵の文字：計画射撃点の番号、青の要請の残りの秒、赤の砲撃。</summary>
        void DrawArtilleryLabels()
        {
            if (_sim.BatteryOf(Side.Blue) == null && _sim.BatteryOf(Side.Red) == null) return;
            var area = MapArea();
            void Tag(Vec2 at, string text, Color bg)
            {
                var g = WorldToGui(at);
                if (!area.Contains(g)) return;
                var size = _small.CalcSize(new GUIContent(text));
                var rect = new Rect(g.x - size.x / 2, g.y - size.y / 2, size.x + 4, size.y);
                Fill(rect, bg);
                GUI.Label(rect, text, _small);
            }
            var bat = _sim.BatteryOf(Side.Blue);
            if (bat != null)
                for (int k = 0; k < bat.PlannedPoints.Count; k++)
                    Tag(bat.PlannedPoints[k] + new Vec2(0, -40), $"計{k + 1}", new Color(0.35f, 0.2f, 0f, 0.6f));
            foreach (var m in _sim.FireMissions)
            {
                if (!m.Active) continue;
                if (m.Side == Side.Blue)
                    Tag(m.State == FireMissionState.Firing ? m.Impact : m.Aim, $"砲撃{m.Id + 1} {MissionStateText(m)}", new Color(0.35f, 0.2f, 0f, 0.7f));
                else if (m.State == FireMissionState.Firing)
                    Tag(m.Impact, "赤の砲撃", new Color(0.4f, 0.05f, 0f, 0.7f));
            }
        }
    }
}
