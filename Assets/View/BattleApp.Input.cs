using System;
using System.Collections.Generic;
using System.Linq;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    // 入力：中隊を選ぶ → 命令を選ぶ → 地図をクリック（ドラッグで到着後の向き）
    public sealed partial class BattleApp
    {
        enum Mode { None, Advance, Retreat, HoldFacing, Pin, Attack, Sight }

        Mode _mode = Mode.None;
        int _selected = -1;
        AttackAspect _attackAspect = AttackAspect.None;

        bool _pressing;
        Vec2 _pressWorld;
        Vector2 _pressPx;
        const float DragThresholdPx = 8f;

        // デバッグ表示と視線確認
        int _exposureMode;      // 0：なし、1：青から見た赤の射界、2：赤から見た青の射界
        bool _showTrigger;
        bool _showEscape;
        Vec2? _sightFrom;
        string _sightFromLabel;
        readonly Dictionary<int, Simulation.EscapeEstimate> _escapeCache = new Dictionary<int, Simulation.EscapeEstimate>();

        void ResetInputState()
        {
            _mode = Mode.None;
            _pressing = false;
            _sightFrom = null;
            if (_mapView != null) _mapView.ClearOverlay();
        }

        Company Selected => _selected >= 0 && _selected < _sim.Companies.Count ? _sim.Companies[_selected] : null;

        bool CanCommand(Company c) => c != null && c.Side == Side.Blue && c.UnderCommand && _phase == Phase.Running;

        // ---------------------------------------------------------------- キー

        void HandleKeys()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) CancelMode();

            if (_phase == Phase.Running)
            {
                if (Input.GetKeyDown(KeyCode.Space)) _clock.Paused = !_clock.Paused;
                if (Input.GetKeyDown(KeyCode.F1)) _clock.SetSpeed(1);
                if (Input.GetKeyDown(KeyCode.F2)) _clock.SetSpeed(2);
                if (Input.GetKeyDown(KeyCode.F3)) _clock.SetSpeed(4);
            }

            var blue = BlueCompanies();
            for (int k = 0; k < 4 && k < blue.Count; k++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + k) || Input.GetKeyDown(KeyCode.Keypad1 + k)) Select(blue[k].Id);

            if (Input.GetKeyDown(KeyCode.Q)) BeginOrder(OrderType.Advance);
            if (Input.GetKeyDown(KeyCode.W)) BeginOrder(OrderType.Retreat);
            if (Input.GetKeyDown(KeyCode.E)) BeginOrder(OrderType.Hold);
            if (Input.GetKeyDown(KeyCode.R)) BeginOrder(OrderType.Pin);
            if (Input.GetKeyDown(KeyCode.T)) BeginOrder(OrderType.Attack);
            if (Input.GetKeyDown(KeyCode.Y)) BeginOrder(OrderType.Entrench);

            if (_mode == Mode.Attack)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0)) _attackAspect = AttackAspect.None;
                if (Input.GetKeyDown(KeyCode.F)) _attackAspect = AttackAspect.Front;
                if (Input.GetKeyDown(KeyCode.S)) _attackAspect = AttackAspect.Flank;
                if (Input.GetKeyDown(KeyCode.B)) _attackAspect = AttackAspect.Rear;
            }

            if (Input.GetKeyDown(KeyCode.L)) ToggleSightTool();
            if (Input.GetKeyDown(KeyCode.F5)) CycleExposure();
            if (Input.GetKeyDown(KeyCode.F6)) _showTrigger = !_showTrigger;
            if (Input.GetKeyDown(KeyCode.F7)) _showEscape = !_showEscape;
        }

        void Select(int id)
        {
            _selected = id;
            if (_mode != Mode.Sight) _mode = Mode.None;
        }

        void CancelMode()
        {
            if (_mode == Mode.Sight)
            {
                _sightFrom = null;
                RefreshOverlay();
            }
            _mode = Mode.None;
            _pressing = false;
        }

        /// <summary>命令を選ぶ。保持と陣地構築はすぐに出る（保持はその後ドラッグで向きを指定できる）。</summary>
        void BeginOrder(OrderType type)
        {
            var c = Selected;
            if (!CanCommand(c)) return;
            switch (type)
            {
                case OrderType.Advance: _mode = Mode.Advance; break;
                case OrderType.Retreat: _mode = Mode.Retreat; break;
                case OrderType.Pin: _mode = Mode.Pin; break;
                case OrderType.Attack: _mode = Mode.Attack; break;
                case OrderType.Hold:
                    _sim.Issue(Order.Hold(c.Id));
                    _mode = Mode.HoldFacing;
                    break;
                case OrderType.Entrench:
                    _sim.Issue(Order.Entrench(c.Id));
                    _mode = Mode.None;
                    break;
            }
        }

        void ToggleSightTool()
        {
            if (_mode == Mode.Sight)
            {
                CancelMode();
                return;
            }
            _mode = Mode.Sight;
        }

        void CycleExposure()
        {
            _exposureMode = (_exposureMode + 1) % 3;
            RefreshOverlay();
        }

        /// <summary>重ねる表示を作り直す。視線確認が優先で、なければ露出の地図。</summary>
        void RefreshOverlay()
        {
            if (_mapView == null) return;
            if (_sightFrom.HasValue)
                _mapView.ShowOverlay(MapRenderer.SightCells(_sim.Map, _params.Sight, _sightFrom.Value, _maxSightRange));
            else if (_exposureMode != 0)
                _mapView.ShowOverlay(MapRenderer.ExposureCells(_sim.Map, _sim.PeekExposure(_exposureMode == 1 ? Side.Blue : Side.Red)));
            else
                _mapView.ClearOverlay();
        }

        // ---------------------------------------------------------------- マウス

        void HandleMouse()
        {
            if (Input.GetMouseButtonDown(1))
            {
                CancelMode();
                return;
            }
            if (Input.GetMouseButtonDown(0) && !MouseOverUi())
            {
                _pressing = true;
                _pressWorld = MouseWorld();
                _pressPx = Input.mousePosition;
            }
            if (Input.GetMouseButtonUp(0) && _pressing)
            {
                _pressing = false;
                var up = MouseWorld();
                bool drag = ((Vector2)Input.mousePosition - _pressPx).magnitude > DragThresholdPx;
                double? facing = drag ? Math.Atan2(up.Y - _pressWorld.Y, up.X - _pressWorld.X) : (double?)null;
                OnMapClick(_pressWorld, facing);
            }
        }

        void OnMapClick(Vec2 at, double? facing)
        {
            var c = Selected;
            switch (_mode)
            {
                case Mode.None:
                {
                    var hit = UnitAt(at, null);
                    if (hit != null) _selected = hit.Id;
                    break;
                }
                case Mode.Advance:
                case Mode.Retreat:
                    if (!CanCommand(c)) break;
                    _sim.Issue(_mode == Mode.Advance ? Order.Advance(c.Id, at, facing) : Order.Retreat(c.Id, at, facing));
                    _mode = Mode.None;
                    break;
                case Mode.HoldFacing:
                    if (CanCommand(c) && facing.HasValue) _sim.Issue(Order.Hold(c.Id, facing));
                    _mode = Mode.None;
                    break;
                case Mode.Pin:
                {
                    var tgt = UnitAt(at, Side.Red);
                    if (!CanCommand(c) || tgt == null) break;
                    _sim.Issue(Order.Pin(c.Id, tgt.Id));
                    _mode = Mode.None;
                    break;
                }
                case Mode.Attack:
                {
                    var tgt = UnitAt(at, Side.Red);
                    if (!CanCommand(c) || tgt == null) break;
                    int side = _attackAspect == AttackAspect.Flank
                        ? UnitGeometry.FlankSideAt(tgt.Position, tgt.Facing, _params.Movement.FrontageM, at)
                        : 0;
                    _sim.Issue(Order.Attack(c.Id, tgt.Id, _attackAspect, side));
                    _mode = Mode.None;
                    break;
                }
                case Mode.Sight:
                {
                    // 中隊の駒をクリックしたらその中心から、そうでなければクリックした地点から
                    var unit = UnitAt(at, null);
                    _sightFrom = unit != null ? unit.Position : at;
                    _sightFromLabel = unit != null ? unit.Name + " の位置から" : $"({at.X:0}, {at.Y:0}) から";
                    RefreshOverlay();
                    break;
                }
            }
        }

        /// <summary>点にある盤上の中隊（駒の矩形の中、少し広めに判定）。重なっていれば中心に近い方。</summary>
        Company UnitAt(Vec2 p, Side? side)
        {
            double margin = PixelsToWorld(4);
            Company best = null;
            double bestD = double.MaxValue;
            foreach (var c in _sim.Companies)
            {
                if (!c.OnBoard || (side.HasValue && c.Side != side.Value)) continue;
                Pose(c, out var pos, out double f);
                if (!UnitGeometry.Contains(pos, f, _params.Movement.FrontageM, _params.Movement.DepthM, p, margin)) continue;
                double d = Vec2.Distance(pos, p);
                if (d < bestD)
                {
                    bestD = d;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>退路の見積もり（表示用。刻みごとに1回だけ計算する）。</summary>
        Simulation.EscapeEstimate EscapeOf(Company c)
        {
            if (!_escapeCache.TryGetValue(c.Id, out var e)) _escapeCache[c.Id] = e = _sim.PeekEscape(c);
            return e;
        }

        string ModeHint()
        {
            switch (_mode)
            {
                case Mode.Advance: return "前進：目的地をクリック（押したままドラッグで到着後の向き）";
                case Mode.Retreat: return "後退：目的地をクリック（背後なら後ろ向きに下がる。ドラッグで向き）";
                case Mode.HoldFacing: return "保持しました。向きを変えるなら地図をドラッグ（Esc で終わる）";
                case Mode.Pin: return "拘束：目標の敵の駒をクリック";
                case Mode.Attack:
                    return _attackAspect == AttackAspect.Flank
                        ? "攻撃（側面）：敵の駒の端をクリックでその側面、中央なら Sim が選ぶ"
                        : $"攻撃（{AspectName(_attackAspect)}）：目標の敵の駒をクリック";
                case Mode.Sight: return "視線確認：地点か中隊の駒をクリック（緑＝見える、赤＝稜線・遮蔽で見えない）。L か Esc で終わる";
                default: return null;
            }
        }
    }
}
