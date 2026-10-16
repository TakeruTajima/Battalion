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

        /// <summary>刻みを進める（補間なし）。</summary>
        public void StepForCapture(int ticks)
        {
            for (int k = 0; k < ticks && _phase == Phase.Running; k++)
            {
                CapturePrevious();
                _sim.Step();
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

        /// <summary>表示の切り替えを設定して、駒と重ねる表示を描き直す。</summary>
        public void DrawForCapture(int selectedId, int exposureMode, bool trigger, bool escape, Vec2? sightFrom)
        {
            _selected = selectedId;
            _exposureMode = exposureMode;
            _showTrigger = trigger;
            _showEscape = escape;
            _sightFrom = sightFrom;
            RefreshOverlay();
            LateUpdate();
        }
    }
}
