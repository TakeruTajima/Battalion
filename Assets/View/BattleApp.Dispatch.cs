using System;
using System.Collections.Generic;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // P2c.2：意見具申の表示。届いた具申を出来事の欄に出し、要請を具申の箱に並べる。中隊の伝令の点と点線。
    public sealed partial class BattleApp
    {
        /// <summary>出来事の欄に出し終えた、届いた具申の数。</summary>
        int _inboxSeen;
        /// <summary>具申の箱から［消す］で消した要請。</summary>
        readonly HashSet<Dispatch> _dismissed = new HashSet<Dispatch>();
        /// <summary>［許可］の2回押し（起案を置き換えるとき）：1回目を押した要請と時刻。</summary>
        Dispatch _approveArmed;
        float _approveArmedAt;
        const float ApproveConfirmSec = 3f;
        /// <summary>自動一時停止の選択肢「要請が届いた」（初めは切）。</summary>
        bool _pauseOnRequest;
        Vector2 _requestScroll;

        static readonly Color RequestColor = new Color(0.45f, 0.9f, 1f);
        static readonly Color32 CourierColor = new Color32(90, 220, 110, 255);

        /// <summary>
        /// 新しく届いた具申を出来事の欄に出す（「[T+12:30 発] B2 目標崩壊（R1）」）。要請は目立つ色、宣言は独断の色。
        /// 要請が届いて「要請で止める」が入なら、自動一時停止の理由に足す。
        /// </summary>
        void ProcessInbox(List<string> reasons)
        {
            var inbox = _sim.Inbox;
            for (; _inboxSeen < inbox.Count; _inboxSeen++)
            {
                var d = inbox[_inboxSeen];
                var kind = d.Category == DispatchCategory.Request ? EventFeed.EntryKind.Request
                    : d.Category == DispatchCategory.Declaration ? EventFeed.EntryKind.Declaration
                    : EventFeed.EntryKind.Normal;
                _feed.Entries.Add(new EventFeed.Entry(d.ArrivedTime ?? _sim.Time,
                    $"[{EventFeed.FormatTime(d.SentState.Time)} 発] {DispatchText(d)}　（{StateText(d.SentState)}）", false, kind));
                if (d.Category == DispatchCategory.Request && _pauseOnRequest)
                    reasons.Add($"{_sim.Companies[d.CompanyId].Name} {RequestName(d.Kind)}");
            }
        }

        string Other(Dispatch d) => d.OtherId >= 0 ? _sim.Companies[d.OtherId].Name : "?";

        /// <summary>具申の文（中隊の名前から）。</summary>
        string DispatchText(Dispatch d)
        {
            var c = _sim.Companies[d.CompanyId];
            string who = c.Name;
            switch (d.Kind)
            {
                case DispatchKind.OrdersDone: return $"{who} 命令を終えた（命令待ち）";
                case DispatchKind.TargetBroken: return $"{who} 目標崩壊（{Other(d)}）";
                case DispatchKind.EnemySighted: return $"{who} 敵発見（{Other(d)}）";
                case DispatchKind.HeavyLoss: return $"{who} 損害大";
                case DispatchKind.AttackStalled: return $"{who} 攻撃頓挫（動揺で停止）";
                case DispatchKind.NeighborRouted: return $"{who} 隣の {Other(d)} が崩壊";
                case DispatchKind.EntrenchDone: return $"{who} 陣地完成";
                case DispatchKind.AskOrders: return $"{who} 命令を請う";
                case DispatchKind.AskPursuit: return $"{who} 追撃の可否を問う（{Other(d)}）";
                case DispatchKind.AskRetreat: return $"{who} 後退の許可を求む";
                default:
                {
                    string p = $"{who}（{PersonalityName(c.Personality)}）";
                    switch (d.DeclarationKind)
                    {
                        case "pursue": return $"{p}追撃に移る（{Other(d)}）";
                        case "attack": return $"{p}攻めかかる（{Other(d)}）";
                        case "flank": return $"{p}側面へ回る（{Other(d)}）";
                        case "cover": return $"{p}物陰へ下がる";
                        case "no_pursuit": return $"{p}追撃せず確保（損害を嫌う）";
                        default: return $"{p}独断";
                    }
                }
            }
        }

        static string RequestName(DispatchKind k) =>
            k == DispatchKind.AskOrders ? "命令を請う" : k == DispatchKind.AskPursuit ? "追撃の可否" : k == DispatchKind.AskRetreat ? "後退の許可" : "";

        /// <summary>伝令が出た時点の中隊の様子：「兵力 82%・士気 55・動揺・第2段 攻撃」。</summary>
        string StateText(DispatchState s)
        {
            string t = $"兵力 {s.StrengthRatio * 100:0}%・士気 {s.Morale:0}・{StateName(s.State)}";
            if (s.StageIndex >= 0 && s.StageCount > 1) t += $"・第{s.StageIndex + 1}段";
            t += "・" + OrderName(s.OrderType);
            return t;
        }

        /// <summary>要請が古くなったか：その要請の伝令が出た後に、その中隊へ新しい命令書を出した。</summary>
        bool Stale(Dispatch d)
        {
            foreach (var r in _sim.Runners)
                if (r.CompanyId == d.CompanyId && r.State != RunnerState.Cancelled && r.SentTime > d.SentState.Time) return true;
            return false;
        }

        bool ApproveArmed(Dispatch d) => _approveArmed == d && Time.unscaledTime - _approveArmedAt <= ApproveConfirmSec;

        /// <summary>
        /// ［許可］：その中隊を選び、起案の欄に命令書を作る（発令はプレイヤー）。書きかけの起案があれば2回押し
        /// （1回目で「起案を置き換える：もう一度」を3秒出し、3秒以内の2回目で置き換える）。
        /// 後退の許可は命令の種類を「後退」にして、行き先の地図のクリックを待つ。
        /// </summary>
        void Approve(Dispatch d)
        {
            var c = _sim.Companies[d.CompanyId];
            if (!CanCommand(c)) return;
            var existing = DraftOf(c);
            if (existing != null && existing.Count > 0 && !ApproveArmed(d))
            {
                _approveArmed = d;
                _approveArmedAt = Time.unscaledTime;
                return;
            }
            _approveArmed = null;
            Select(c.Id);
            _drafts[c.Id] = Simulation.DraftForRequest(d, _sim.MaxStages);
            _draftVersion++;
            if (d.Kind == DispatchKind.AskRetreat) BeginOrder(OrderType.Retreat);
        }

        /// <summary>具申の箱：届いた要請の一覧（新しいものを上に）。各行に［許可］［消す］。古くなった要請は薄く。</summary>
        void DrawRequestBox(Rect area)
        {
            var list = new List<Dispatch>();
            foreach (var d in _sim.Inbox)
                if (d.Category == DispatchCategory.Request && !_dismissed.Contains(d)) list.Add(d);
            GUI.Label(new Rect(area.x + 4, area.y, area.width - 8, 20), $"具申（{list.Count}）", _bold);
            var view = new Rect(area.x + 4, area.y + 20, area.width - 8, area.height - 20);
            const float rowH = 40;
            var content = new Rect(0, 0, view.width - 16, Math.Max(view.height, list.Count * rowH));
            _requestScroll = GUI.BeginScrollView(view, _requestScroll, content);
            for (int k = 0; k < list.Count; k++)
            {
                var d = list[list.Count - 1 - k];
                float y = k * rowH;
                bool stale = Stale(d);
                var prev = GUI.contentColor;
                GUI.contentColor = stale ? new Color(1, 1, 1, 0.4f) : RequestColor;
                GUI.Label(new Rect(0, y, content.width, 18), $"[{EventFeed.FormatTime(d.SentState.Time)} 発] {DispatchText(d)}", _small);
                GUI.contentColor = prev;
                bool armed = ApproveArmed(d);
                GUI.enabled = CanCommand(_sim.Companies[d.CompanyId]);
                if (GUI.Button(new Rect(0, y + 18, armed ? 170 : 56, 20), armed ? "起案を置き換える：もう一度" : "許可", armed ? _toggleOn : _button)) Approve(d);
                GUI.enabled = true;
                if (GUI.Button(new Rect(armed ? 176 : 62, y + 18, 56, 20), "消す", _button)) _dismissed.Add(d);
            }
            GUI.EndScrollView();
        }

        /// <summary>中隊の伝令：中隊から本部への緑の点線と、動く緑の点（本部の伝令は黄・水色）。倒れた・届いた伝令は描かない。</summary>
        void DrawCouriers(float px)
        {
            var hq = _sim.HeadquartersOf(Side.Blue);
            if (!hq.HasValue) return;
            foreach (var r in _sim.Couriers)
            {
                if (r.State != CourierState.Preparing && r.State != CourierState.Outbound) continue;
                _shapes.DashedLine(V(r.Position), V(hq.Value), 1.2f * px, 3 * px, Palette.WithAlpha(CourierColor, 150));
                _shapes.Circle(V(r.Position), 3.5f * px, 3.5f * px, CourierColor, 10);
                _shapes.Circle(V(r.Position), 4.5f * px, 1.2f * px, new Color32(20, 20, 20, 220), 12);
            }
        }

        /// <summary>左パネルの「最後の報告 T+08:10」（その中隊の伝令が最後に本部に届いた時刻）。</summary>
        string LastReportText(Company c)
        {
            var t = _sim.LastCourierArrival(c.Id);
            return t.HasValue ? "最後の報告 " + EventFeed.FormatTime(t.Value) : "最後の報告 —";
        }
    }
}
