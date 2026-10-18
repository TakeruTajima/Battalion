using System;
using System.Collections.Generic;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 段階つき命令（P2b）：命令書を組む画面（起案中・送付中・実行中）、発令、準備中の直し・取り消し、信号弾、段ごとの下見
    public sealed partial class BattleApp
    {
        /// <summary>起案中の命令書（本部の机の上。中隊ごとに1枚。選ぶ中隊を替えても残る）。</summary>
        readonly Dictionary<int, OrderSheetDraft> _drafts = new Dictionary<int, OrderSheetDraft>();
        /// <summary>起案を直すたびに増える（下見の計算し直しの合図）。</summary>
        int _draftVersion;
        Vector2 _sheetScroll;

        /// <summary>信号弾の確認（P2b）：1回目で構え、3秒以内の2回目で打ち上げる。</summary>
        FlareColor? _flareArmed;
        float _flareArmedAt;
        const float FlareConfirmSec = 3f;

        static readonly FlareColor[] FlareColors = { FlareColor.Red, FlareColor.Green, FlareColor.White };

        public static string FlareName(FlareColor c) => c == FlareColor.Red ? "赤" : c == FlareColor.Green ? "緑" : "白";

        static Color FlareUiColor(FlareColor c) =>
            c == FlareColor.Red ? new Color(1f, 0.45f, 0.4f) : c == FlareColor.Green ? new Color(0.5f, 1f, 0.5f) : Color.white;

        OrderSheetDraft DraftOf(Company c) => c != null && _drafts.TryGetValue(c.Id, out var d) ? d : null;

        /// <summary>
        /// 命令を起案中の命令書に書き込む（P2b。選んだだけでは出さない。「発令」で出す）。
        /// 起案がなければ白紙から第1段を作る。あれば書き込む段の命令を置き換える。
        /// 最後の段に書き込んだら、その下に空の段を1つ自動で足す（P2b.1。3段まで）。
        /// </summary>
        bool SendCommand(Order order)
        {
            var c = _sim.Companies[order.CompanyId];
            if (!_drafts.TryGetValue(c.Id, out var d))
            {
                d = new OrderSheetDraft(c.Id);
                _drafts[c.Id] = d;
            }
            d.Write(order, _sim.MaxStages);
            _draftVersion++;
            _commandRefusal = null;
            return true;
        }

        /// <summary>
        /// 発令：起案中の命令書を出す（会戦開始前は伝令なしで即座に届く。それ以降は伝令で、判断枠を1つ使う）。
        /// 準備中の命令書を直していたなら、その伝令の命令書を差し替える（判断枠は増えない）。
        /// </summary>
        void DispatchDraft(Company c)
        {
            var d = DraftOf(c);
            if (d == null) return;
            var sheet = d.ToSheet(_sim.MaxStages, _sim.FlareUsedBy(Side.Blue));
            if (sheet == null)
            {
                _commandRefusal = "発令できない段がある（赤い印を直す）";
                return;
            }
            if (d.ReviseRunnerId.HasValue)
            {
                if (!_sim.ReviseSheet(d.ReviseRunnerId.Value, sheet))
                {
                    _commandRefusal = "伝令はもう出発した（直すなら新しい命令書を送る）";
                    d.ReviseRunnerId = null;
                    return;
                }
            }
            else
            {
                var res = _sim.SendSheet(sheet);
                if (res == SendResult.NoSlot)
                {
                    _commandRefusal = NoSlotReason();
                    return;
                }
                if (res == SendResult.Invalid)
                {
                    _commandRefusal = "発令できない段がある";
                    return;
                }
            }
            _drafts.Remove(c.Id);
            _draftVersion++;
            _commandRefusal = null;
            _mode = Mode.None;
        }

        void DiscardDraft(Company c)
        {
            _drafts.Remove(c.Id);
            _draftVersion++;
            _commandRefusal = null;
        }

        /// <summary>準備中の命令書を起案の欄に写して直す（書きかけの起案がある間は押せない）。</summary>
        void ReviseRunner(Runner r)
        {
            var d = OrderSheetDraft.CopyOf(r.Sheet);
            d.ReviseRunnerId = r.Id;
            _drafts[r.CompanyId] = d;
            _draftVersion++;
        }

        /// <summary>今の時刻から 5分後を分で切り上げた時刻（時刻の条件の初めの値）。</summary>
        double DefaultTriggerTime() => Math.Ceiling((_sim.Time + 300) / 60.0) * 60.0;

        // ---------------------------------------------------------------- 信号弾

        /// <summary>信号弾のボタン・キー：1回目で構え（3秒）、構えている色をもう一度押すと打ち上げる。</summary>
        void PressFlare(FlareColor color)
        {
            if (_phase != Phase.Running || _sim.FlareTime(Side.Blue, color).HasValue) return;
            float now = Time.unscaledTime;
            if (_flareArmed == color && now - _flareArmedAt <= FlareConfirmSec)
            {
                _sim.FireFlare(Side.Blue, color);
                _flareArmed = null;
                return;
            }
            _flareArmed = color;
            _flareArmedAt = now;
        }

        bool FlareArmed(FlareColor color) => _flareArmed == color && Time.unscaledTime - _flareArmedAt <= FlareConfirmSec;

        /// <summary>上部バーの信号弾のボタン（使ったものは消える）。</summary>
        void DrawFlareButtons()
        {
            GUILayout.Label("信号弾", _small, GUILayout.Width(40));
            foreach (var (color, key) in new[] { (FlareColor.Red, "F9"), (FlareColor.Green, "F10"), (FlareColor.White, "F11") })
            {
                if (_sim.FlareTime(Side.Blue, color).HasValue) continue;
                var prev = GUI.contentColor;
                GUI.contentColor = FlareUiColor(color);
                string label = FlareArmed(color) ? $"もう一度で打ち上げ（{FlareName(color)}）" : $"{FlareName(color)} ({key})";
                if (GUILayout.Button(label, FlareArmed(color) ? _toggleOn : _button, GUILayout.Width(FlareArmed(color) ? 170 : 62)))
                    PressFlare(color);
                GUI.contentColor = prev;
            }
        }

        // ---------------------------------------------------------------- 文字

        static string TriggerText(StageTrigger t)
        {
            switch (t.Kind)
            {
                case TriggerKind.Complete: return "完了したら";
                case TriggerKind.Time: return EventFeed.FormatTime(t.TimeSec);
                case TriggerKind.Flare: return "信号弾 " + FlareName(t.Color);
                default: return "—";
            }
        }

        string StageOrderText(Order o)
        {
            if (o == null) return "（命令を選ぶ）";
            string s = OrderName(o.Type);
            if (o.Destination.HasValue) s += $" ({o.Destination.Value.X:0}, {o.Destination.Value.Y:0})";
            if (o.TargetId >= 0) s += " → " + _sim.Companies[o.TargetId].Name;
            if (o.Type == OrderType.Attack)
                s += o.BearingRad.HasValue ? $"（{Compass(o.BearingRad.Value * Angles.RadToDeg)}から）" : $"（{AspectName(o.Aspect)}）";
            if (o.Type == OrderType.Pin) s += $"（{PinRangeName(o.PinRange)}）";
            if (o.FacingRad.HasValue) s += $" {Compass(o.FacingRad.Value * Angles.RadToDeg)}向き";
            return s;
        }

        /// <summary>駒と左パネルの段の印「2/3　次：信号弾 緑」（命令書が2段以上のときだけ）。</summary>
        string StageTag(Company c)
        {
            // 預かり中・動揺で停止中（P2b.2）
            if (c.HeldSheet != null) return "命令書を預かり中（再集結後に開始）";
            if (c.ShakenHalt) return HaltText(c) + (c.ActiveSheet != null && c.ActiveSheet.Stages.Count > 1 ? $"　{c.StageIndex + 1}/{c.ActiveSheet.Stages.Count}" : "");
            var sh = c.ActiveSheet;
            if (sh == null || sh.Stages.Count < 2) return null;
            string s = $"{c.StageIndex + 1}/{sh.Stages.Count}";
            if (c.StageIndex + 1 < sh.Stages.Count) s += "　次：" + TriggerText(sh.Stages[c.StageIndex + 1].Trigger);
            return s;
        }

        string EtaText(SheetStagePreview p)
        {
            switch (p.Kind)
            {
                case EtaKind.Absolute: return "約 " + EventFeed.FormatTime(p.Arrive);
                case EtaKind.AfterFlare: return $"信号弾 {FlareName(p.Color)}から 約 {Math.Max(1, Math.Round(p.Arrive / 60)):0}分";
                default: return "完了しだい";
            }
        }

        // ---------------------------------------------------------------- 右パネル：命令書

        /// <summary>右パネルの命令書の欄（起案中・送付中・実行中）。</summary>
        void DrawSheetPanel(Company c)
        {
            if (c.Side != Side.Blue) return;
            GUILayout.Space(6);
            GUILayout.Label("命令書", _bold);

            // ---- 起案中
            var d = DraftOf(c);
            GUILayout.Label(d != null && d.ReviseRunnerId.HasValue ? "起案中（準備中の命令書を直している）" : "起案中", _small);
            bool can = CanCommand(c);
            GUI.enabled = can;
            if (d == null || d.Count == 0)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("白紙から", _button))
                {
                    _drafts[c.Id] = new OrderSheetDraft(c.Id);
                    _draftVersion++;
                }
                GUI.enabled = can && c.ActiveSheet != null;
                if (GUILayout.Button("実行中の写しから", _button))
                {
                    _drafts[c.Id] = OrderSheetDraft.CopyOf(c.ActiveSheet, c.StageIndex);
                    _draftVersion++;
                }
                GUI.enabled = can;
                GUILayout.EndHorizontal();
                if (d == null) GUILayout.Label("命令を選んで地図をクリックすると、第1段に書き込む", _small);
            }
            if (d != null)
            {
                var problems = d.Problems(_sim.MaxStages, _sim.FlareUsedBy(Side.Blue));
                int remove = -1, up = -1, down = -1;
                for (int k = 0; k < d.Count; k++)
                {
                    var st = d.Stages[k];
                    var box = new GUIStyle(_button) { alignment = TextAnchor.MiddleLeft, wordWrap = true };
                    string head = (d.Selected == k ? "▶" : "　") + $"第{k + 1}段　{StageOrderText(st.Order)}";
                    if (GUILayout.Button(head, d.Selected == k ? _toggleOn : box)) d.Selected = k;
                    // 裁量（P2c）：任せる（初めの値）／厳命
                    if (st.Order != null)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("裁量", _small, GUILayout.Width(30));
                        if (GUILayout.Button("任せる", st.Discretion == Discretion.Delegate ? _toggleOn : _button)) SetDiscretion(d, k, Discretion.Delegate);
                        if (GUILayout.Button("厳命", st.Discretion == Discretion.Strict ? _toggleOn : _button)) SetDiscretion(d, k, Discretion.Strict);
                        GUILayout.Label(st.Discretion == Discretion.Strict ? "独断しない（字面どおり）" : $"{PersonalityName(c.Personality)}の独断を許す", _small);
                        GUILayout.EndHorizontal();
                    }
                    if (k > 0)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("条件", _small, GUILayout.Width(30));
                        if (GUILayout.Button("完了したら", st.Trigger.Kind == TriggerKind.Complete ? _toggleOn : _button))
                            SetTrigger(d, k, StageTrigger.Complete);
                        if (GUILayout.Button("時刻", st.Trigger.Kind == TriggerKind.Time ? _toggleOn : _button))
                            SetTrigger(d, k, StageTrigger.At(st.Trigger.Kind == TriggerKind.Time ? st.Trigger.TimeSec : DefaultTriggerTime()));
                        foreach (var col in FlareColors)
                        {
                            // 上がった色は新しく選べない（P2b.1。上がったことが残るので、届いた瞬間に満たしてしまう）
                            bool used = _sim.FlareTime(Side.Blue, col).HasValue;
                            GUI.enabled = can && !used;
                            if (GUILayout.Button(used ? FlareName(col) + "済" : FlareName(col),
                                    st.Trigger.Kind == TriggerKind.Flare && st.Trigger.Color == col ? _toggleOn : _button, GUILayout.Width(used ? 40 : 28)))
                                SetTrigger(d, k, StageTrigger.OnFlare(col));
                            GUI.enabled = can;
                        }
                        GUILayout.EndHorizontal();
                        if (st.Trigger.Kind == TriggerKind.Time)
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("　" + EventFeed.FormatTime(st.Trigger.TimeSec), _label, GUILayout.Width(80));
                            if (GUILayout.Button("−1分", _button)) SetTrigger(d, k, StageTrigger.At(Math.Max(0, st.Trigger.TimeSec - 60)));
                            if (GUILayout.Button("+1分", _button)) SetTrigger(d, k, StageTrigger.At(st.Trigger.TimeSec + 60));
                            GUILayout.EndHorizontal();
                        }
                    }
                    GUILayout.BeginHorizontal();
                    if (problems[k] != null)
                    {
                        var ps = new GUIStyle(_small) { wordWrap = true };
                        ps.normal.textColor = new Color(1f, 0.4f, 0.35f);
                        GUILayout.Label("× " + problems[k], ps);
                    }
                    GUILayout.FlexibleSpace();
                    GUI.enabled = can && k > 0;
                    if (GUILayout.Button("↑", _button, GUILayout.Width(26))) up = k;
                    GUI.enabled = can && k + 1 < d.Count;
                    if (GUILayout.Button("↓", _button, GUILayout.Width(26))) down = k;
                    GUI.enabled = can;
                    if (GUILayout.Button("×", _button, GUILayout.Width(26))) remove = k;
                    GUILayout.EndHorizontal();
                }
                if (up >= 0) { d.MoveUp(up); _draftVersion++; }
                if (down >= 0) { d.MoveDown(down); _draftVersion++; }
                if (remove >= 0) { d.Remove(remove); _draftVersion++; }

                GUILayout.BeginHorizontal();
                GUI.enabled = can && d.Count < _sim.MaxStages && (d.Count == 0 || d.Stages[d.Count - 1].Order != null);
                if (GUILayout.Button("段を足す", _button))
                {
                    d.Add(null, _sim.MaxStages);
                    _draftVersion++;
                }
                bool slot = SlotAvailable || d.ReviseRunnerId.HasValue;
                GUI.enabled = can && d.IsValid(_sim.MaxStages, _sim.FlareUsedBy(Side.Blue)) && slot;
                if (GUILayout.Button(d.ReviseRunnerId.HasValue ? "差し替え (Enter)" : "発令 (Enter)", _button)) DispatchDraft(c);
                GUI.enabled = can;
                if (GUILayout.Button("起案の破棄", _button)) DiscardDraft(c);
                GUILayout.EndHorizontal();
                if (!slot) GUILayout.Label(NoSlotReason(), _small);
            }
            GUI.enabled = true;

            // ---- 送付中（その中隊へ向かっている伝令。新しいものを下に）
            bool anyTransit = false;
            foreach (var r in _sim.Runners)
            {
                if (r.CompanyId != c.Id || r.Delivered) continue;
                bool preparing = r.State == RunnerState.Preparing;
                bool presumed = r.State == RunnerState.Lost && !r.SlotReleased;
                if (!preparing && r.State != RunnerState.Outbound && !presumed) continue;
                if (!anyTransit) GUILayout.Label("送付中", _small);
                anyTransit = true;
                if (preparing)
                {
                    double left = Math.Max(0, r.SentTime + _params.Runners.PrepSec - _sim.Time);
                    GUILayout.Label($"準備中 あと {left:0}秒", _label);
                }
                else GUILayout.Label($"未着 あと {RunnerTagEta(r):0}秒", _label);
                var transitProblems = preparing ? OrderSheet.StageProblems(r.Sheet.Stages, _sim.MaxStages, _sim.FlareUsedBy(Side.Blue)) : null;
                for (int k = 0; k < r.Sheet.Stages.Count; k++)
                {
                    var st = r.Sheet.Stages[k];
                    GUILayout.Label($"　第{k + 1}段　{StageOrderText(st.Order)}" + (k > 0 ? $"［{TriggerText(st.Trigger)}］" : "") + StrictMark(st), _small);
                    if (transitProblems != null && transitProblems[k] != null)
                    {
                        // 準備中の命令書に使用済みの色の条件が残っている（P2b.1）：［直す］で直すか［取り消す］
                        var ps = new GUIStyle(_small) { wordWrap = true };
                        ps.normal.textColor = new Color(1f, 0.4f, 0.35f);
                        GUILayout.Label("　× " + transitProblems[k], ps);
                    }
                }
                if (preparing)
                {
                    GUILayout.BeginHorizontal();
                    bool hasDraft = d != null && d.Count > 0 && d.ReviseRunnerId != r.Id;
                    GUI.enabled = can && !hasDraft;
                    if (GUILayout.Button("直す", _button)) ReviseRunner(r);
                    GUI.enabled = can;
                    if (GUILayout.Button("取り消す", _button))
                    {
                        _sim.CancelRunner(r.Id);
                        if (d != null && d.ReviseRunnerId == r.Id) DiscardDraft(c);
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    if (hasDraft) GUILayout.Label("書きかけの起案がある間は［直す］を押せない", _small);
                }
            }

            // ---- 預かり中（壊走中・再集結中に届いた命令書。P2b.2）
            if (c.HeldSheet != null)
            {
                GUILayout.Label("預かり中（再集結後に開始）", _small);
                for (int k = 0; k < c.HeldSheet.Stages.Count; k++)
                {
                    var st = c.HeldSheet.Stages[k];
                    GUILayout.Label($"　第{k + 1}段　{StageOrderText(st.Order)}" + (k > 0 ? $"［{TriggerText(st.Trigger)}］" : "") + StrictMark(st), _small);
                }
            }

            // ---- 実行中
            var sh = c.ActiveSheet;
            if (sh != null)
            {
                GUILayout.Label("実行中", _small);
                for (int k = 0; k < sh.Stages.Count; k++)
                {
                    var st = sh.Stages[k];
                    string line = $"{(k == c.StageIndex ? "▶" : "　")}第{k + 1}段　{StageOrderText(st.Order)}" + (k > 0 ? $"［{TriggerText(st.Trigger)}］" : "") + StrictMark(st);
                    if (k == c.StageIndex && c.StartWaitOrder != null) line += $"　開始まで {Math.Max(0, c.StartWaitAt - _sim.Time):0}秒";
                    else if (k == c.StageIndex && c.ShakenHalt) line += $"　<color=#ffb060>{HaltText(c)}</color>";
                    var style = k == c.StageIndex ? _bold : _small;
                    var prev = GUI.contentColor;
                    if (k < c.StageIndex) GUI.contentColor = new Color(1, 1, 1, 0.45f);
                    GUILayout.Label(line, style);
                    GUI.contentColor = prev;
                }
            }
        }

        double RunnerTagEta(Runner r) => _sim.RunnerEta(r);

        void SetDiscretion(OrderSheetDraft d, int k, Discretion v)
        {
            d.SetDiscretion(k, v);
            _draftVersion++;
        }

        /// <summary>厳命の段の印（P2c）。</summary>
        static string StrictMark(Stage st) => st.Discretion == Discretion.Strict ? "<color=#ff8080>［厳命］</color>" : "";

        void SetTrigger(OrderSheetDraft d, int k, StageTrigger t)
        {
            d.SetTrigger(k, t);
            _draftVersion++;
        }

        // ---------------------------------------------------------------- 地図：段ごとの下見

        string _sheetPreviewKey;
        enum SheetKind { Active, Transit, Draft }
        readonly List<(List<SheetStagePreview> stages, int firstNumber, SheetKind kind)> _sheetPreviews =
            new List<(List<SheetStagePreview>, int, SheetKind)>();

        /// <summary>
        /// 選んだ中隊の命令書の段ごとの経路と行き先に、段の番号と到着予定を出す。実行中は今の段を濃く・後の段を薄く、
        /// 送付中と起案中は薄く（起案中は破線）。刻みか起案が変わったときだけ計算し直す。
        /// </summary>
        void DrawSheetPreviews(float px)
        {
            var c = Selected;
            if (c == null || c.Side != Side.Blue || !c.OnBoard) return;
            string key = $"{_sim.Tick}|{c.Id}|{_draftVersion}";
            if (key != _sheetPreviewKey)
            {
                _sheetPreviewKey = key;
                _sheetPreviews.Clear();
                double delay = _params.Of(c.Training).OrderStartSec;
                var sh = c.ActiveSheet;
                if (sh != null && sh.Stages.Count > 1)
                {
                    // 実行中：今の段から後（今の段は始まった時刻、または開始待ちの時刻から）
                    var rest = new List<Stage>();
                    for (int k = c.StageIndex; k < sh.Stages.Count; k++) rest.Add(k == c.StageIndex ? sh.Stages[k].With(StageTrigger.None) : sh.Stages[k]);
                    double start = c.StartWaitOrder != null ? c.StartWaitAt : _sim.Time;
                    _sheetPreviews.Add((_sim.PreviewSheet(rest, start), c.StageIndex + 1, SheetKind.Active));
                }
                var inflight = _sim.InFlightTo(c.Id, out double eta);
                if (inflight != null)
                    _sheetPreviews.Add((_sim.PreviewSheet(inflight.Sheet.Stages, _sim.Time + eta + delay), 1, SheetKind.Transit));
                var d = DraftOf(c);
                if (d != null && d.Count > 0 && d.Stages[0].Order != null)
                {
                    double start = _sim.BeforeStart ? _sim.Time : _sim.Time + _sim.EstimateRunnerEta(c.Id) + delay;
                    _sheetPreviews.Add((_sim.PreviewSheet(d.Stages, start), 1, SheetKind.Draft));
                }
            }
            foreach (var (stages, firstNumber, kind) in _sheetPreviews)
            {
                bool draft = kind == SheetKind.Draft;
                Vec2? lastGoal = null;
                int stack = 0;
                for (int k = 0; k < stages.Count; k++)
                {
                    var sp = stages[k];
                    bool now = kind == SheetKind.Active && k == 0;
                    byte alpha = now ? (byte)220 : (byte)110;
                    var col = new Color32(255, 255, 255, alpha);
                    var pv = sp.Preview;
                    if (!pv.Stays && pv.Path.Count >= 2)
                    {
                        for (int i = 0; i + 1 < pv.Path.Count; i++)
                        {
                            if (draft) _shapes.DashedLine(V(pv.Path[i]), V(pv.Path[i + 1]), 2 * px, 8 * px, col);
                            else _shapes.Line(V(pv.Path[i]), V(pv.Path[i + 1]), (now ? 2.5f : 1.8f) * px, col);
                        }
                    }
                    var g = V(pv.Goal);
                    int number = firstNumber + k;
                    _shapes.Circle(g, 5 * px, 5 * px, new Color32(20, 20, 20, (byte)(alpha * 0.8f)), 12);
                    _shapes.Circle(g, 6 * px, 1.5f * px, col, 12);
                    // 段の番号と到着予定（保持など動かない第1段は番号だけ）
                    string eta = $"第{number}段" + (stages[k].Preview.Stays ? "" : "　" + EtaText(sp));
                    // 同じ行き先に重なる段（保持など動かない段）の文字は、下へずらす
                    stack = lastGoal.HasValue && Vec2.Distance(lastGoal.Value, pv.Goal) < 1.0 ? stack + 1 : 0;
                    lastGoal = pv.Goal;
                    g += new Vector2(0, -14 * px * stack);
                    _texts.Draw(eta, g + new Vector2(12 * px, -1.2f * px), 12, px, new Color(0, 0, 0, 0.85f), TextAnchor.MiddleLeft);
                    _texts.Draw(eta, g + new Vector2(11 * px, 0), 12, px, new Color(1f, 0.95f, 0.75f, alpha / 255f), TextAnchor.MiddleLeft);
                }
            }
        }
    }
}
