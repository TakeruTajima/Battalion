using System;
using System.Linq;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 画面の UI（IMGUI）：ステージ選択、上部バー、左パネル（中隊の一覧）、右パネル（詳細と命令）、下部のログ、駒の文字、終了画面
    public sealed partial class BattleApp
    {
        const float TopH = 56, LeftW = 270, RightW = 380, BottomH = 150;

        Font _font;
        GUIStyle _label, _small, _bold, _box, _button, _toggleOn, _logImportant, _title;
        Texture2D _white;
        Vector2 _logScroll;
        int _lastLogCount;

        Rect TopRect => new Rect(0, 0, Screen.width, TopH);
        Rect LeftRect => new Rect(0, TopH, LeftW, Screen.height - TopH - BottomH);
        Rect RightRect => new Rect(Screen.width - RightW, TopH, RightW, Screen.height - TopH - BottomH);
        Rect BottomRect => new Rect(0, Screen.height - BottomH, Screen.width, BottomH);
        Rect EndRect => new Rect(Screen.width / 2f - 230, Screen.height / 2f - 170, 460, 340);

        /// <summary>地図を見る領域（パネルの間）。GUI の座標（左上が原点）。</summary>
        Rect MapArea() => new Rect(LeftW, TopH, Screen.width - LeftW - RightW, Screen.height - TopH - BottomH);

        bool MouseOverUi()
        {
            var m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (_phase == Phase.SelectStage) return true;
            if (TopRect.Contains(m) || LeftRect.Contains(m) || RightRect.Contains(m) || BottomRect.Contains(m)) return true;
            return _phase == Phase.Ended && !_hideEnd && EndRect.Contains(m);
        }

        static Font _sharedFont;

        /// <summary>日本語を出せる OS のフォント（IMGUI と地図の上の文字で共用）。</summary>
        static Font UiFont() =>
            _sharedFont != null ? _sharedFont
                : _sharedFont = Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS Gothic", "Arial" }, 14);

        void InitStyles()
        {
            if (_label != null) return;
            _font = UiFont();
            GUI.skin.font = _font;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false, richText = true };
            _label.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            _small = new GUIStyle(_label) { fontSize = 11 };
            _bold = new GUIStyle(_label) { fontStyle = FontStyle.Bold, fontSize = 14 };
            _title = new GUIStyle(_label) { fontStyle = FontStyle.Bold, fontSize = 20, alignment = TextAnchor.MiddleCenter };
            _box = new GUIStyle(GUI.skin.box);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            _toggleOn = new GUIStyle(_button) { fontStyle = FontStyle.Bold };
            _toggleOn.normal.textColor = new Color(1f, 0.85f, 0.1f);
            _logImportant = new GUIStyle(_small);
            _logImportant.normal.textColor = new Color(1f, 0.75f, 0.35f);
            _white = Texture2D.whiteTexture;
        }

        void OnGUI()
        {
            InitStyles();
            GUI.skin.font = _font;
            if (_phase == Phase.SelectStage)
            {
                DrawStageSelect();
                return;
            }
            DrawUnitLabels();
            DrawPanel(TopRect);
            DrawTopBar();
            DrawPanel(LeftRect);
            DrawLeftPanel();
            DrawPanel(RightRect);
            DrawRightPanel();
            DrawPanel(BottomRect);
            DrawLog();
            if (_phase == Phase.Ended && !_hideEnd) DrawEndWindow();
        }

        void DrawPanel(Rect r) => Fill(r, new Color(0.11f, 0.12f, 0.14f, 0.92f));

        void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        // ---------------------------------------------------------------- ステージ選択

        void DrawStageSelect()
        {
            var r = new Rect(Screen.width / 2f - 260, Screen.height / 2f - 170, 520, 340);
            Fill(r, new Color(0.11f, 0.12f, 0.14f, 0.96f));
            GUILayout.BeginArea(new Rect(r.x + 20, r.y + 16, r.width - 40, r.height - 32));
            GUILayout.Label("シナリオA：リーデル村の奪取", _title);
            GUILayout.Space(12);
            if (GUILayout.Button("ステージ1：頂上の塹壕（地形を読めば死角を通って肉薄できる）", _button, GUILayout.Height(40))) StartBattle(1);
            GUILayout.Space(6);
            if (GUILayout.Button("ステージ2：相互支援の陣地（死角がなく、機動が要る）", _button, GUILayout.Height(40))) StartBattle(2);
            GUILayout.Space(12);
            GUILayout.Label("任務：90分の時点でリーデル村を確保していること。", _label);
            GUILayout.Label("始まると一時停止しています。スペースで開始。", _label);
            if (_loadError != null)
            {
                GUILayout.Space(8);
                var err = new GUIStyle(_label) { wordWrap = true };
                err.normal.textColor = new Color(1f, 0.4f, 0.4f);
                GUILayout.Label(_loadError, err);
            }
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------------- 上部バー

        void DrawTopBar()
        {
            // 1段目：シナリオ、時刻と残り、一時停止と倍速、自動一時停止
            GUILayout.BeginArea(new Rect(8, 3, Screen.width - 16, 26));
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>シナリオA ステージ{_scenario.Stage}</b>", _bold, GUILayout.Width(140));
            double left = Math.Max(0, _scenario.TimeLimitMin * 60 - _sim.Time);
            GUILayout.Label($"<b>{EventFeed.FormatTime(_sim.Time)}</b>　残り {(int)left / 60:00}:{(int)left % 60:00}", _bold, GUILayout.Width(190));

            if (_phase == Phase.Running)
            {
                if (GUILayout.Button(_clock.Paused ? "再開 (Space)" : "一時停止 (Space)", _clock.Paused ? _toggleOn : _button, GUILayout.Width(130)))
                    _clock.Paused = !_clock.Paused;
                GUILayout.Space(6);
                foreach (var sp in SimClock.Speeds)
                    if (GUILayout.Button($"×{sp}", _clock.Speed == sp ? _toggleOn : _button, GUILayout.Width(44)))
                        _clock.SetSpeed(sp);
                GUILayout.Label("Z 遅く／X 速く", _small, GUILayout.Width(96));
            }
            else
            {
                GUILayout.Label("会戦終了", _bold, GUILayout.Width(80));
                if (_hideEnd && GUILayout.Button("結果を見る", _button, GUILayout.Width(100))) _hideEnd = false;
                if (GUILayout.Button("ステージ選択へ", _button, GUILayout.Width(120))) BackToSelect();
            }
            GUILayout.Space(10);
            if (GUILayout.Button(_autoPause ? "自動一時停止：入 (P)" : "自動一時停止：切 (P)", _autoPause ? _toggleOn : _button, GUILayout.Width(160)))
                _autoPause = !_autoPause;
            GUILayout.Space(10);
            if (_phase == Phase.Running) DrawFlareButtons();
            GUILayout.FlexibleSpace();
            GUILayout.Label(SlotText(), _label, GUILayout.Width(200));
            GUILayout.Label($"移動中の被弾×{_params.Terrain.MovingOpenDamageMult:0.0#}　シード {_seed}", _small);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // 2段目：勝利条件（今この瞬間に終わったら勝ちか）、止めた理由、デバッグ表示の状態
            GUILayout.BeginArea(new Rect(8, 30, Screen.width - 16, 24));
            GUILayout.BeginHorizontal();
            var o = _objective;
            string mark(bool b) => b ? "○" : "×";
            string win = o.WinIfEndedNow ? "<color=#7fb2ff><b>今終われば：勝ち</b></color>" : "<color=#ff9a6a><b>今終われば：負け</b></color>";
            GUILayout.Label($"勝利条件　村に青の中隊：{mark(o.BlueInVillage)}　250m以内の赤：{mark(o.RedNearVillage)}　→ {win}　（村：{ObjectiveStatus.Name(o.State)}）", _label, GUILayout.Width(560));
            if (_pauseReason != null && _clock.Paused && _phase == Phase.Running)
            {
                var ps = new GUIStyle(_bold);
                ps.normal.textColor = new Color(1f, 0.85f, 0.2f);
                GUILayout.Label("一時停止：" + _pauseReason, ps);
            }
            GUILayout.FlexibleSpace();
            string exp = _exposureMode == 0 ? "なし" : (_exposureMode == 1 ? "青から見た赤の射界" : "赤から見た青の射界");
            var ce = CursorElevation();
            GUILayout.Label(ce.HasValue ? $"<b>標高 {ce.Value:0.0}m</b>" : "標高 —", _label, GUILayout.Width(100));
            GUILayout.Label($"露出(F5)：{exp}　引き金(F6)：{(_showTrigger ? "表示" : "なし")}　退路(F7)：{(_showEscape ? "表示" : "なし")}　" +
                            $"撃たない理由(F8)：{(_showBlockedAll ? "全中隊" : "選んだ中隊")}　視線(L)：{(_mode == Mode.Sight ? "使用中" : "なし")}", _small);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------------- 左パネル：中隊の一覧

        void DrawLeftPanel()
        {
            var r = LeftRect;
            float y = r.y + 6;
            GUI.Label(new Rect(r.x + 8, y, r.width - 16, 20), "中隊（1〜4 で選ぶ）", _bold);
            y += 24;
            int blueIndex = 0;
            foreach (var c in _sim.Companies)
            {
                bool blue = c.Side == Side.Blue;
                if (blue) blueIndex++;
                var row = new Rect(r.x + 4, y, r.width - 8, 56);
                if (c.Id == _selected) Fill(row, new Color(0.35f, 0.3f, 0.05f, 0.6f));
                if (GUI.Button(row, GUIContent.none, GUIStyle.none)) Select(c.Id);

                Color side = blue ? new Color32(90, 150, 240, 255) : new Color32(240, 140, 60, 255);
                Fill(new Rect(row.x, row.y + 2, 4, row.height - 4), side);
                string key = blue ? $"[{blueIndex}] " : "";
                string pers = blue ? PersonalityName(c.Personality) + "・" : "";
                GUI.Label(new Rect(row.x + 8, row.y, row.width - 10, 18),
                    $"{key}<b>{c.Name}</b>  {pers}{TrainingName(c.Training)}　{StateText(c)}", _label);
                Bar(new Rect(row.x + 8, row.y + 20, 120, 8), c.Strength / (float)c.InitialStrength, new Color(0.75f, 0.75f, 0.75f));
                GUI.Label(new Rect(row.x + 132, row.y + 14, 60, 18), $"{c.Strength}名", _small);
                Bar(new Rect(row.x + 8, row.y + 31, 120, 8), (float)(c.Morale / 100.0), MoraleColor(c));
                GUI.Label(new Rect(row.x + 132, row.y + 25, 60, 18), $"士気{c.Morale:0}", _small);
                GUI.Label(new Rect(row.x + 8, row.y + 38, row.width - 10, 18), $"命令：{OrderText(c)}", _small);
                y += 60;
                if (y > r.yMax - 56) break;
            }
        }

        void Bar(Rect r, float v, Color col)
        {
            Fill(r, new Color(0, 0, 0, 0.6f));
            Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), col);
        }

        Color MoraleColor(Company c)
        {
            double rout = _params.Of(c.Training).RoutThreshold;
            if (c.Morale <= rout) return new Color(0.85f, 0.2f, 0.2f);
            if (c.Morale <= _params.Morale.ShakenThreshold) return new Color(0.95f, 0.75f, 0.2f);
            return new Color(0.35f, 0.8f, 0.4f);
        }

        string StateText(Company c)
        {
            string s = StateName(c.State);
            if (c.UnderCommand)
            {
                string ms = MoveStyleName(c.MoveStyle);
                if (ms != null) s += "・" + ms;
                if (c.BlockedByEnemy) s += "・敵に阻まれて停止";
                if (c.SeekingSight) s += "・見える位置を探して移動";
                if (c.TurnWaitStart.HasValue) s += "・向き直り待ち";
                else if (c.FacingOverride.HasValue && Math.Abs(Angles.Wrap(c.FacingOverride.Value - c.Facing)) > 1e-6) s += "・向き直り中";
                if (c.DisarrayUntil >= _sim.Time) s += "・<color=#ffa040>隊形の乱れ</color>";
                if (c.Pinned) s += "・拘束";
                if (c.Encircled) s += "・<color=#ff6060>包囲</color>";
                if (c.Fortified) s += "・籠城";
                if (c.Side == Side.Blue && _sim.AwaitingOrders(c)) s += "・<color=#c8c8c8>命令待ち</color>";
            }
            return s;
        }

        string OrderText(Company c)
        {
            if (!c.UnderCommand) return "—";
            var o = c.CurrentOrder;
            string s = OrderName(o.Type);
            if (c.Pursuing)
            {
                var pt = c.PursuitTargetId >= 0 ? _sim.Companies[c.PursuitTargetId].Name : "?";
                return $"追撃中 → {pt}（始めた地点から {Vec2.Distance(c.Position, c.PursuitStart):0}／{c.PursuitLimitM:0}m）";
            }
            if (o.Type == OrderType.Attack || o.Type == OrderType.Pin)
            {
                var t = o.TargetId >= 0 ? _sim.Companies[o.TargetId].Name : "?";
                s += $" → {t}";
                if (o.Type == OrderType.Attack) s += o.OnBreak == AttackOnBreak.Pursue ? "［崩れたら追撃可］" : "［崩れたら確保］";
                if (o.Type == OrderType.Attack)
                    s += $"（{AspectName(c.AttackChosenAspect)}・{Compass(c.AttackBearing * Angles.RadToDeg)}から" +
                         (o.BearingRad.HasValue ? "・方角を指定）" : "）");
                if (o.Type == OrderType.Pin) s += $"（{PinRangeName(o.PinRange)}）";
            }
            if (o.Moves) s += c.Arrived ? "（到着）" : "（移動中）";
            string stageTag = StageTag(c);
            if (stageTag != null) s += $"　［{stageTag}］";
            if (_sim.PendingOrderOf(c.Id) != null) s += "　→ 新しい命令を次の刻みで受け付ける";
            else if (c.Side == Side.Blue)
            {
                string tag = RunnerTag(c);
                if (tag != null) s += $"　<color=#ffe070>［{tag}］</color>";
            }
            if (o.Type == OrderType.Entrench && (!o.Moves || c.Arrived))
                s += $" {Math.Min(100, c.EntrenchProgress / _params.MapRules.EntrenchSec * 100):0}%";
            return s;
        }

        // ---------------------------------------------------------------- 右パネル：詳細と命令

        void DrawRightPanel()
        {
            var r = RightRect;
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 12));
            var c = Selected;
            if (c == null)
            {
                GUILayout.Label("中隊を選んでください（クリックか 1〜4）", _label);
                GUILayout.EndArea();
                return;
            }
            _sheetScroll = GUILayout.BeginScrollView(_sheetScroll);

            string who = c.Side == Side.Blue ? $"{c.Name}（{PersonalityName(c.Personality)}・{TrainingName(c.Training)}）" : $"{c.Name}（敵・{TrainingName(c.Training)}）";
            GUILayout.Label($"<b>{who}</b>", _bold);
            GUILayout.Label($"兵力 {c.Strength}/{c.InitialStrength}　士気 {c.Morale:0.0}（壊走 {_params.Of(c.Training).RoutThreshold:0}以下）", _label);
            GUILayout.Label($"状態：{StateText(c)}", _label);
            GUILayout.Label($"命令：{OrderText(c)}", _label);
            double deg = c.Facing * Angles.RadToDeg;
            GUILayout.Label($"向き {Compass(deg)}（{deg:0}°）　位置 ({c.Position.X:0}, {c.Position.Y:0})", _small);
            var l = c.Ledger;
            GUILayout.Label($"士気の要因：正面{l.Front:0} 側面{l.Flank:0} 背面{l.Rear:0} 包囲{l.Encircled:0}\n　隣の壊走{l.NeighborRout:0} 被弾中{l.UnderFire:0} 離脱{l.Disengage:0} 回復+{l.Recovery:0}", _small);

            GUILayout.Space(8);
            bool can = CanCommand(c);
            GUI.enabled = can;
            GUILayout.Label("命令", _bold);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("前進 (Q)", _mode == Mode.Advance ? _toggleOn : _button)) BeginOrder(OrderType.Advance);
            if (GUILayout.Button("後退 (W)", _mode == Mode.Retreat ? _toggleOn : _button)) BeginOrder(OrderType.Retreat);
            if (GUILayout.Button("保持 (E)", _mode == Mode.HoldFacing ? _toggleOn : _button)) BeginOrder(OrderType.Hold);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("拘束 (R)", _mode == Mode.Pin ? _toggleOn : _button)) BeginOrder(OrderType.Pin);
            if (GUILayout.Button("攻撃 (T)", _mode == Mode.Attack ? _toggleOn : _button)) BeginOrder(OrderType.Attack);
            if (GUILayout.Button("陣地構築 (Y)", _mode == Mode.Entrench ? _toggleOn : _button)) BeginOrder(OrderType.Entrench);
            GUILayout.EndHorizontal();

            GUILayout.Label("拘束の距離（拘束を選んでいる間は D で切り替え）", _small);
            GUILayout.BeginHorizontal();
            foreach (var pr in new[] { PinRange.Far, PinRange.Near })
                if (GUILayout.Button(PinRangeName(pr), _pinRange == pr ? _toggleOn : _button)) _pinRange = pr;
            GUILayout.EndHorizontal();
            GUILayout.Label("攻撃の範囲（敵の駒から外へドラッグすれば方角で指定）", _small);
            GUILayout.BeginHorizontal();
            foreach (var (a, key) in new[] { (AttackAspect.None, "0"), (AttackAspect.Front, "F"), (AttackAspect.Flank, "S"), (AttackAspect.Rear, "B") })
                if (GUILayout.Button($"{AspectName(a)} ({key})", _attackAspect == a ? _toggleOn : _button)) _attackAspect = a;
            GUILayout.EndHorizontal();
            GUILayout.Label("攻撃の目標が崩れたら（C で切り替え）", _small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("確保", _onBreak == AttackOnBreak.Secure ? _toggleOn : _button)) _onBreak = AttackOnBreak.Secure;
            if (GUILayout.Button("追撃可", _onBreak == AttackOnBreak.Pursue ? _toggleOn : _button)) _onBreak = AttackOnBreak.Pursue;
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            if (!can)
                GUILayout.Label(c.Side == Side.Red ? "敵の中隊には命令できません"
                    : (_phase == Phase.Running ? "この状態では命令を受け付けません" : ""), _small);
            else if (_commandRefusal != null)
                GUILayout.Label(_commandRefusal, _small);

            var hint = ModeHint();
            if (hint != null)
            {
                GUILayout.Space(6);
                var hs = new GUIStyle(_label) { wordWrap = true };
                hs.normal.textColor = new Color(1f, 0.85f, 0.2f);
                GUILayout.Label(hint, hs);
            }
            DrawSheetPanel(c);
            if (_sightFrom.HasValue)
                GUILayout.Label("視線確認：" + _sightFromLabel + " から" + (_sightTo.HasValue ? "　断面：" + _sightToLabel + " まで" : ""), _small);
            if (_showEscape && c.OnBoard && (c.UnderCommand || c.State == CompanyState.Routed))
            {
                var e = EscapeOf(c);
                GUILayout.Label($"退路：最小の見積もり {e.MinLoss * 100:0.0}%（{Compass(_sim.EscapeDirection(e.Best) * Angles.RadToDeg)}）" +
                                (e.MinLoss >= _params.Encirclement.EscapeLossThreshold ? "　<color=#ff6060>逃げ道なし</color>" : ""), _small);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static string Compass(double deg)
        {
            string[] names = { "東", "北東", "北", "北西", "西", "南西", "南", "南東" };
            int k = (int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8;
            return names[k];
        }

        // ---------------------------------------------------------------- 下部：ログ

        void DrawLog()
        {
            var r = BottomRect;
            GUI.Label(new Rect(r.x + 8, r.y + 4, 400, 20), "ログ", _bold);
            var view = new Rect(r.x + 8, r.y + 24, r.width - 16, r.height - 28);
            float lineH = 17;
            var entries = _feed.Entries;
            var content = new Rect(0, 0, view.width - 20, Math.Max(view.height, entries.Count * lineH));
            if (entries.Count != _lastLogCount)
            {
                _lastLogCount = entries.Count;
                _logScroll.y = content.height; // 新しい出来事が来たら一番下へ
            }
            _logScroll = GUI.BeginScrollView(view, _logScroll, content);
            for (int k = 0; k < entries.Count; k++)
            {
                var e = entries[k];
                GUI.Label(new Rect(0, k * lineH, content.width, lineH), $"{EventFeed.FormatTime(e.TimeSec)}　{e.Text}", e.Important ? _logImportant : _small);
            }
            GUI.EndScrollView();
        }

        // ---------------------------------------------------------------- 駒の文字

        void DrawUnitLabels()
        {
            var area = MapArea();
            foreach (var c in _sim.Companies)
            {
                if (c.State == CompanyState.LeftBattle) continue;
                Pose(c, out var pos, out double facing);
                if (!area.Contains(WorldToGui(pos))) continue;
                // 駒の下の兵力・士気のバー（駒の下端から 10〜20 画素）のさらに下に置く
                Extent(facing, out _, out float ey);
                var g = WorldToGui(new Vec2(pos.X, pos.Y - ey));
                string state = c.State == CompanyState.Normal ? "" : StateName(c.State);
                string text = c.OnBoard ? $"{c.Name} {c.Strength} 士{c.Morale:0} {state}" : $"{c.Name} 降伏";
                var size = _small.CalcSize(new GUIContent(text));
                var rect = new Rect(g.x - size.x / 2, g.y + 22, size.x + 4, size.y);
                Fill(rect, new Color(0, 0, 0, 0.55f));
                GUI.Label(rect, text, _small);
            }
        }

        // ---------------------------------------------------------------- 終了画面

        void DrawEndWindow()
        {
            var r = EndRect;
            Fill(r, new Color(0.08f, 0.09f, 0.1f, 0.96f));
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 24));
            GUILayout.Label(_result.BlueWins ? "勝利：リーデル村を確保" : "敗北：村を確保できなかった", _title);
            GUILayout.Label($"{_result.Reason}（{EventFeed.FormatTime(_result.EndSec)}）", _label);
            GUILayout.Space(8);
            SideLine("青（自軍）", _result.Blue);
            SideLine("赤（敵）", _result.Red);
            GUILayout.Space(8);
            GUILayout.Label("崩れた中隊：会戦中に一度でも壊走した数（再集結して戻った中隊を含む）", _small);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("地図を見る（閉じる）", _button, GUILayout.Height(30))) _hideEnd = true;
            if (GUILayout.Button("もう一度（ステージ選択へ）", _button, GUILayout.Height(30))) BackToSelect();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        bool _hideEnd;

        void SideLine(string name, SideResult s)
        {
            GUILayout.Label($"<b>{name}</b>　損害 {s.Lost}/{s.Initial}名（{s.Lost * 100.0 / Math.Max(1, s.Initial):0}%）　崩れた中隊 {s.Broken}　降伏 {s.Surrendered}　離脱 {s.Left}　指揮下 {s.UnderCommand}", _label);
        }

        void BackToSelect()
        {
            if (_world != null) Destroy(_world.gameObject);
            _world = null;
            _shapes = null;
            _texts = null;
            _mapView = null;
            _sim = null;
            _selected = -1;
            _hideEnd = false;
            _phase = Phase.SelectStage;
        }
    }
}
