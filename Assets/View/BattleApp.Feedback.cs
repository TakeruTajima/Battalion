using System;
using System.Collections.Generic;
using System.Linq;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 手応えの見える化：刻みごとに射撃の記録と出来事を読み、損害の数字・被弾の明滅・士気の残像・浮かぶ文字・自動一時停止を作る。
    // 見た目の乱数は Sim の乱数を使わず、ここで別に持つ（会戦の結果を変えない）。時間は現実の時間（Time.unscaledTime）で数える。
    public sealed partial class BattleApp
    {
        /// <summary>損害の数字をまとめる長さ（現実の秒）。</summary>
        const float DamageWindowSec = 1.5f;
        const float DamageShowSec = 1.6f;
        const float BigTextSec = 2.8f;
        const float HitFlashSec = 0.45f;
        /// <summary>士気が減ってから、残像が縮み始めるまで（現実の秒）と、縮む速さ（士気／秒）。</summary>
        const float GhostHoldSec = 1.2f;
        const float GhostShrinkPerSec = 12f;

        sealed class UnitFx
        {
            public float DamageWindowStart = -1;
            public readonly int[] Damage = new int[3];
            public float HitTime = -100;
            public Aspect HitAspect;
            public float GhostMorale = -1;
            public float GhostHoldUntil;
            public float LastMorale = -1;
        }

        sealed class FloatText
        {
            public int UnitId = -1;
            public Vec2 World;
            public string Text;
            public Color Color;
            public float Start;
            public float Duration;
            public bool Big;
        }

        UnitFx[] _fx;
        readonly List<FloatText> _floats = new List<FloatText>();
        readonly List<Simulation.FireRecord> _lastFire = new List<Simulation.FireRecord>();
        readonly System.Random _fxRng = new System.Random(12345);

        bool _autoPause = true;
        string _pauseReason;
        VillageState _villageState;
        ObjectiveStatus _objective;

        void ResetFeedback()
        {
            _fx = new UnitFx[_sim.Companies.Count];
            for (int k = 0; k < _fx.Length; k++) _fx[k] = new UnitFx();
            _floats.Clear();
            _lastFire.Clear();
            _pauseReason = null;
            _objective = ObjectiveStatus.Compute(_sim, _scenario);
            _villageState = _objective.State;
        }

        /// <summary>
        /// 1刻み進めた直後に呼ぶ。射撃の記録を写し、損害と被弾をためる。出来事から浮かぶ文字と自動一時停止の理由を作る。
        /// 自動一時停止すべきなら true（呼び出し側はそのフレームの残りの刻みを進めない）。
        /// </summary>
        bool OnTickStepped()
        {
            float now = Time.unscaledTime;
            _lastFire.Clear();
            var hitThisTick = new Dictionary<int, Aspect>();
            foreach (var f in _sim.LastTickFire)
            {
                _lastFire.Add(f);
                var t = _fx[f.Target];
                if (f.Casualties > 0)
                {
                    if (t.DamageWindowStart < 0) t.DamageWindowStart = now;
                    t.Damage[(int)f.Aspect] += f.Casualties;
                }
                // 被弾の明滅：この刻みに撃たれた中で最も強い向き（背面＞側面＞正面）で光らせる
                if (!hitThisTick.TryGetValue(f.Target, out var a) || f.Aspect > a) hitThisTick[f.Target] = f.Aspect;
            }
            foreach (var kv in hitThisTick)
            {
                _fx[kv.Key].HitAspect = kv.Value;
                _fx[kv.Key].HitTime = now;
            }

            var reasons = new List<string>();
            foreach (var h in _feed.Recent) HandleHappening(h, now, reasons);
            _feed.Recent.Clear();

            _objective = ObjectiveStatus.Compute(_sim, _scenario);
            var vs = _objective.State;
            if (vs != _villageState)
            {
                _villageState = vs;
                reasons.Add("村：" + ObjectiveStatus.Name(vs));
                _floats.Add(new FloatText
                {
                    World = MapScenario.VillageCenter, Text = "村：" + ObjectiveStatus.Name(vs), Color = VillageColor(vs),
                    Start = now, Duration = BigTextSec, Big = true,
                });
            }

            if (_autoPause && reasons.Count > 0)
            {
                _pauseReason = $"{EventFeed.FormatTime(_sim.Time)} " + string.Join("／", reasons);
                return true;
            }
            return false;
        }

        void HandleHappening(EventFeed.Happening h, float now, List<string> reasons)
        {
            var c = _sim.Find(h.Unit);
            if (c == null) return;
            bool blue = c.Side == Side.Blue;
            string text = null;
            Color col = Color.white;
            switch (h.Kind)
            {
                case EventFeed.Kind.FlankHit:
                    text = "側面を突かれた";
                    col = new Color(1f, 0.6f, 0.15f);
                    if (blue) reasons.Add($"{c.Name} 側面を突かれた");
                    break;
                case EventFeed.Kind.RearHit:
                    text = "背面を突かれた";
                    col = new Color(1f, 0.3f, 0.25f);
                    if (blue) reasons.Add($"{c.Name} 背面を突かれた");
                    break;
                case EventFeed.Kind.Encircled:
                    text = "包囲された";
                    col = new Color(1f, 0.3f, 0.25f);
                    reasons.Add($"{c.Name} 包囲された");
                    break;
                case EventFeed.Kind.Routed:
                    text = "壊走";
                    col = new Color(1f, 0.85f, 0.3f);
                    if (blue) reasons.Add($"{c.Name} 壊走");
                    break;
                case EventFeed.Kind.Surrendered:
                    text = "降伏";
                    col = Color.white;
                    if (blue) reasons.Add($"{c.Name} 降伏");
                    break;
                case EventFeed.Kind.RallyStart:
                    text = "再集結";
                    col = new Color(0.75f, 0.85f, 1f);
                    break;
                case EventFeed.Kind.RallyComplete:
                    text = "復帰";
                    col = new Color(0.6f, 1f, 0.6f);
                    break;
                case EventFeed.Kind.Counterattack:
                    text = $"逆襲 → {h.Other}";
                    col = new Color(1f, 0.45f, 0.9f);
                    reasons.Add($"{c.Name}（予備）が逆襲");
                    break;
                case EventFeed.Kind.Entrenched:
                    text = "陣地完成";
                    col = new Color(0.85f, 0.75f, 0.55f);
                    break;
                case EventFeed.Kind.TargetBroken:
                    text = h.Other == "pursue" ? "追撃" : "確保";
                    col = h.Other == "pursue" ? new Color(1f, 0.45f, 0.35f) : new Color(0.75f, 0.9f, 1f);
                    if (blue) reasons.Add($"{c.Name} の攻撃の目標が崩れた（{text}）");
                    break;
                case EventFeed.Kind.PursuitEnd:
                    text = "追撃やめ";
                    col = new Color(0.75f, 0.9f, 1f);
                    break;
                case EventFeed.Kind.Turn:
                    text = "向き直る";
                    col = new Color(1f, 0.95f, 0.6f);
                    break;
                case EventFeed.Kind.Disarray:
                    text = "隊形の乱れ";
                    col = new Color(1f, 0.6f, 0.2f);
                    break;
                case EventFeed.Kind.Receipt:
                    text = "受領";
                    col = new Color(0.6f, 0.85f, 1f);
                    break;
                case EventFeed.Kind.ReceiptOverdue:
                    text = "受領報告なし";
                    col = new Color(1f, 0.55f, 0.4f);
                    reasons.Add($"{c.Name} 受領報告なし");
                    break;
                case EventFeed.Kind.SeekSight:
                    // 目標が見えないため見える位置を探す（P1g）。自動一時停止の理由には足さない
                    text = "見える位置へ";
                    col = new Color(0.85f, 0.95f, 1f);
                    break;
            }
            if (text != null)
                _floats.Add(new FloatText { UnitId = c.Id, Text = text, Color = col, Start = now, Duration = BigTextSec, Big = true });
        }

        /// <summary>毎フレーム：損害の数字を出す・士気の残像を縮める・消えた文字を捨てる。</summary>
        void UpdateFeedback()
        {
            if (_fx == null) return;
            float now = Time.unscaledTime;
            foreach (var c in _sim.Companies)
            {
                var fx = _fx[c.Id];
                if (fx.DamageWindowStart >= 0 && now - fx.DamageWindowStart >= DamageWindowSec)
                {
                    int total = fx.Damage.Sum();
                    if (total > 0) _floats.Add(DamageText(c.Id, fx.Damage, total, now));
                    Array.Clear(fx.Damage, 0, 3);
                    fx.DamageWindowStart = -1;
                }

                float m = (float)c.Morale;
                if (fx.GhostMorale < 0 || m > fx.GhostMorale) fx.GhostMorale = m;
                if (fx.LastMorale >= 0 && m < fx.LastMorale - 1e-4f) fx.GhostHoldUntil = now + GhostHoldSec;
                fx.LastMorale = m;
                if (now > fx.GhostHoldUntil && fx.GhostMorale > m)
                    fx.GhostMorale = Mathf.Max(m, fx.GhostMorale - GhostShrinkPerSec * Time.unscaledDeltaTime);
            }
            _floats.RemoveAll(f => now - f.Start > f.Duration);
        }

        /// <summary>「−4」の数字。主な向き（損害が最も多い向き）が側面・背面なら色を変え、文字を添える。</summary>
        FloatText DamageText(int unitId, int[] dmg, int total, float now)
        {
            int main = 0;
            for (int a = 1; a < 3; a++)
                if (dmg[a] > dmg[main]) main = a;
            string tag = main == (int)Aspect.Flank ? " 側面" : main == (int)Aspect.Rear ? " 背面" : "";
            return new FloatText
            {
                UnitId = unitId, Text = $"−{total}{tag}", Color = AspectColor((Aspect)main), Start = now, Duration = DamageShowSec,
            };
        }

        static Color AspectColor(Aspect a)
        {
            switch (a)
            {
                case Aspect.Flank: return new Color(1f, 0.62f, 0.15f);
                case Aspect.Rear: return new Color(1f, 0.25f, 0.2f);
                default: return Color.white;
            }
        }

        static Color VillageColor(VillageState s)
        {
            switch (s)
            {
                case VillageState.Blue: return new Color(0.45f, 0.65f, 1f);
                case VillageState.Red: return new Color(1f, 0.55f, 0.25f);
                case VillageState.Contested: return new Color(1f, 0.9f, 0.3f);
                default: return new Color(0.85f, 0.85f, 0.85f);
            }
        }
    }
}
