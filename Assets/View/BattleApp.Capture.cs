using System;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 撮影用の口（エディタのバッチモードで地図と駒を画像にして確かめるため）。再生中の画面では使わない
    public sealed partial class BattleApp
    {
        /// <summary>カメラを指定して会戦を始める（Awake を待たずに使う）。時間は止めたまま。</summary>
        public Simulation BeginCapture(Camera cam, int stage, ulong seed)
        {
            _cam = cam;
            StartBattle(stage, seed);
            if (_loadError != null) throw new InvalidOperationException(_loadError);
            return _sim;
        }

        /// <summary>刻みを進める（補間なし）。自動一時停止はしない。</summary>
        public void StepForCapture(int ticks)
        {
            for (int k = 0; k < ticks && _phase == Phase.Running; k++)
            {
                CapturePrevious();
                _sim.Step();
                OnTickStepped();
                if (CheckEnd()) break;
            }
            CapturePrevious();
            if (_feed.TerrainChanged)
            {
                _feed.TerrainChanged = false;
                _mapView.PaintTerrain();
            }
            _escapeCache.Clear();
        }

        /// <summary>たまっている損害を捨てる（撮る直前のしばらくぶんだけを数字にするため）。<paramref name="alsoTexts"/> なら出来事の文字も捨てる。</summary>
        public void ClearFeedbackForCapture(bool alsoTexts)
        {
            foreach (var fx in _fx)
            {
                Array.Clear(fx.Damage, 0, 3);
                fx.DamageWindowStart = -1;
            }
            if (alsoTexts) _floats.Clear();
        }

        /// <summary>たまっている損害を、待たずに数字にする（現実の時間が進まないバッチモードのため）。</summary>
        public void FlushDamageForCapture()
        {
            float now = Time.unscaledTime;
            foreach (var c in _sim.Companies)
            {
                var fx = _fx[c.Id];
                int total = fx.Damage[0] + fx.Damage[1] + fx.Damage[2];
                if (total > 0) _floats.Add(DamageText(c.Id, fx.Damage, total, now));
                Array.Clear(fx.Damage, 0, 3);
                fx.DamageWindowStart = -1;
            }
        }

        /// <summary>表示の切り替えを設定して、駒と重ねる表示を描き直す。</summary>
        public void DrawForCapture(int selectedId, int exposureMode, bool trigger, bool escape, Vec2? sightFrom,
            Vec2? sightTo = null, bool blockedAll = false)
        {
            _selected = selectedId;
            _sightTo = sightTo;
            _showBlockedAll = blockedAll;
            if (sightFrom.HasValue) SightPoint(sightFrom.Value, out _sightFromLabel);
            if (sightTo.HasValue) SightPoint(sightTo.Value, out _sightToLabel);
            _exposureMode = exposureMode;
            _showTrigger = trigger;
            _showEscape = escape;
            _sightFrom = sightFrom;
            RefreshOverlay();
            LateUpdate();
        }
    }
}
