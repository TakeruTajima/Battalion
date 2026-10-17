using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Battalion.Sim;
using UnityEngine;

namespace Battalion.View
{
    /// <summary>
    /// P1b の入口。ステージ選択 → 読み込み → 毎フレームの時間の進め方・入力・描画。
    /// 画面の部品（地図・駒・UI）は起動時にスクリプトで作る。Sim の状態は読むだけで、命令は <see cref="Simulation.Issue"/> で渡す。
    /// </summary>
    public sealed partial class BattleApp : MonoBehaviour
    {
        enum Phase { SelectStage, Running, Ended }

        Phase _phase = Phase.SelectStage;
        string _loadError;

        SimParams _params;
        MapScenario _scenario;
        Simulation _sim;
        EventFeed _feed;
        SimClock _clock;
        ulong _seed;
        double _maxSightRange;

        Transform _world;
        MapRenderer _mapView;
        ShapeMesh _shapes;
        Camera _cam;

        // 補間用：直前の刻みの位置と向き（添字は中隊の ID）
        Vec2[] _prevPos;
        double[] _prevFacing;

        BattleResult _result;

        sealed class BattleResult
        {
            public bool BlueWins;
            public double EndSec;
            public string Reason;
            public SideResult Blue, Red;
        }

        sealed class SideResult
        {
            public int Lost, Initial, Broken, Surrendered, Left, UnderCommand;
        }

        void Awake()
        {
            Application.targetFrameRate = 60;
            if (_cam != null) return; // 撮影用に先に設定済み
            _cam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                _cam = go.AddComponent<Camera>();
            }
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color32(40, 44, 48, 255);
            _cam.nearClipPlane = -100;
            _cam.farClipPlane = 100;
            _cam.transform.position = new Vector3(1500, 1000, -10);
            _cam.orthographicSize = 1100;
        }

        // ---------------------------------------------------------------- 読み込み

        static string StreamingPath(string rel) => Path.Combine(Application.streamingAssetsPath, rel);

        void StartBattle(int stage) => StartBattle(stage, (ulong)(DateTime.Now.Ticks & 0x7fffffff));

        void StartBattle(int stage, ulong seed)
        {
            try
            {
                _params = SimParams.LoadFile(StreamingPath("sim_params.json"));
                _scenario = MapScenario.LoadFile(StreamingPath($"maps/scenario_a{stage}.json"));
            }
            catch (Exception e)
            {
                _loadError = "読み込みに失敗しました：" + e.Message;
                Debug.LogException(e);
                return;
            }
            _loadError = null;
            if (_world != null) Destroy(_world.gameObject);
            _world = new GameObject("World").transform;
            _texts = null; // 地図の上の文字は World の下に作るので、作り直す

            _seed = seed;
            _feed = new EventFeed();
            _sim = _scenario.Build(_params, _seed, _feed);
            _clock = new SimClock(_params.TickSec) { Paused = true };
            _maxSightRange = MapRenderer.MaxSightRange(_sim.Map, _params.Sight);

            _mapView = new MapRenderer(_sim.Map, _world);
            _shapes = new ShapeMesh("Shapes", 10, _world);
            CapturePrevious();
            ResetFeedback();

            ResetInputState();
            _exposureMode = 0;
            _showTrigger = false;
            _showEscape = false;
            _escapeCache.Clear();
            _result = null;
            _hideEnd = false;
            _commandRefusal = null;
            _phase = Phase.Running;
            FitCamera();
        }

        // ---------------------------------------------------------------- 時間

        void Update()
        {
            if (_phase == Phase.SelectStage) return;

            HandleKeys();
            HandleCamera();
            HandleMouse();

            if (_phase != Phase.Running) return;
            if (!_clock.Paused) _pauseReason = null;
            int ticks = _clock.Advance(Time.unscaledDeltaTime);
            for (int k = 0; k < ticks; k++)
            {
                CapturePrevious();
                _sim.Step();
                bool stop = OnTickStepped();
                if (CheckEnd()) break;
                if (stop)
                {
                    // 自動一時停止：何が起きたかを上部バーに出し、このフレームの残りの刻みは進めない。
                    // 補間の起点を今の刻みにそろえ、止めた状態がそのまま見えるようにする
                    _clock.Paused = true;
                    _clock.Reset();
                    CapturePrevious();
                    break;
                }
            }
            UpdateFeedback();
            if (ticks > 0)
            {
                _escapeCache.Clear();
                if (_feed.TerrainChanged)
                {
                    _feed.TerrainChanged = false;
                    _mapView.PaintTerrain();
                }
                if (_exposureMode != 0 && _sightFrom == null) RefreshOverlay();
            }
        }

        void CapturePrevious()
        {
            int n = _sim.Companies.Count;
            if (_prevPos == null || _prevPos.Length != n)
            {
                _prevPos = new Vec2[n];
                _prevFacing = new double[n];
            }
            foreach (var c in _sim.Companies)
            {
                _prevPos[c.Id] = c.Position;
                _prevFacing[c.Id] = c.Facing;
            }
        }

        /// <summary>直前の刻みと今の刻みの間を補間した位置と向き。</summary>
        void Pose(Company c, out Vec2 pos, out double facing)
        {
            float a = _phase == Phase.Running ? _clock.Alpha : 1f;
            var p0 = _prevPos[c.Id];
            pos = new Vec2(p0.X + (c.Position.X - p0.X) * a, p0.Y + (c.Position.Y - p0.Y) * a);
            facing = _prevFacing[c.Id] + Angles.Wrap(c.Facing - _prevFacing[c.Id]) * a;
        }

        // ---------------------------------------------------------------- 会戦の終わり

        /// <summary>90分で止めて判定する。それより前に止めるのは、赤か青が全て盤上から消えたときだけ。</summary>
        bool CheckEnd()
        {
            bool redOn = _sim.Companies.Any(c => c.Side == Side.Red && c.OnBoard);
            bool blueOn = _sim.Companies.Any(c => c.Side == Side.Blue && c.OnBoard);
            bool timeUp = _sim.Time >= _scenario.TimeLimitMin * 60 - 1e-9;
            if (!timeUp && redOn && blueOn) return false;

            _result = new BattleResult
            {
                BlueWins = _scenario.BlueWins(_sim),
                EndSec = _sim.Time,
                Reason = timeUp ? $"{_scenario.TimeLimitMin:0}分が経過" : (!redOn ? "赤が全て盤上から消えた" : "青が全て盤上から消えた"),
                Blue = Summarize(Side.Blue),
                Red = Summarize(Side.Red),
            };
            _phase = Phase.Ended;
            _clock.Paused = true;
            ResetInputState();
            return true;
        }

        SideResult Summarize(Side side)
        {
            var cs = _sim.Companies.Where(c => c.Side == side).ToList();
            return new SideResult
            {
                Initial = cs.Sum(c => c.InitialStrength),
                Lost = cs.Sum(c => c.InitialStrength - c.Strength),
                Broken = cs.Count(c => _feed.EverRouted.Contains(c.Name)),
                Surrendered = cs.Count(c => c.State == CompanyState.Surrendered),
                Left = cs.Count(c => c.State == CompanyState.LeftBattle),
                UnderCommand = cs.Count(c => c.UnderCommand),
            };
        }

        // ---------------------------------------------------------------- カメラ

        const float MinViewHeightM = 300f;
        const float MaxViewHeightM = 2600f;

        /// <summary>地図全体が、左右のパネルと上下の帯の間に収まるようにする。</summary>
        void FitCamera()
        {
            var area = MapArea();
            float unitsPerPixel = Mathf.Max(3000f / area.width, 2000f / area.height) * 1.05f;
            _cam.orthographicSize = unitsPerPixel * Screen.height / 2;
            // 画面上の地図の領域の中心が、地図の中心に来るようにずらす
            float dxPx = area.center.x - Screen.width / 2f;
            float dyPx = (Screen.height - area.center.y) - Screen.height / 2f;
            _cam.transform.position = new Vector3(1500 - dxPx * unitsPerPixel, 1000 - dyPx * unitsPerPixel, -10);
        }

        void HandleCamera()
        {
            float unitsPerPixel = _cam.orthographicSize * 2 / _cam.pixelHeight;
            var move = Vector2.zero;
            if (Input.GetKey(KeyCode.LeftArrow)) move.x -= 1;
            if (Input.GetKey(KeyCode.RightArrow)) move.x += 1;
            if (Input.GetKey(KeyCode.DownArrow)) move.y -= 1;
            if (Input.GetKey(KeyCode.UpArrow)) move.y += 1;
            _cam.transform.position += (Vector3)(move * 600f * unitsPerPixel * Time.unscaledDeltaTime);

            if (Input.GetMouseButton(2))
            {
                // 中ボタンのドラッグ：前のフレームとのマウス位置の差（画素）だけ動かす
                var d = (Vector2)Input.mousePosition - _lastMousePx;
                _cam.transform.position -= (Vector3)(d * unitsPerPixel);
            }
            _lastMousePx = Input.mousePosition;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f && !MouseOverUi())
            {
                var before = MouseWorld();
                float h = Mathf.Clamp(_cam.orthographicSize * 2 * Mathf.Pow(0.85f, scroll), MinViewHeightM, MaxViewHeightM);
                _cam.orthographicSize = h / 2;
                var after = MouseWorld();
                _cam.transform.position += new Vector3((float)(before.X - after.X), (float)(before.Y - after.Y), 0);
            }
        }

        Vector2 _lastMousePx;

        Vec2 MouseWorld()
        {
            var w = _cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, 10));
            return new Vec2(w.x, w.y);
        }

        Vector2 WorldToGui(Vec2 p)
        {
            var s = _cam.WorldToScreenPoint(new Vector3((float)p.X, (float)p.Y, 0));
            return new Vector2(s.x, Screen.height - s.y);
        }

        float PixelsToWorld(float px) => px * _cam.orthographicSize * 2 / _cam.pixelHeight;

        // ---------------------------------------------------------------- 名前

        public static string PersonalityName(Personality p)
        {
            switch (p)
            {
                case Personality.Cautious: return "慎重";
                case Personality.Reckless: return "猪突";
                case Personality.Agile: return "機敏";
                default: return "冷静";
            }
        }

        public static string TrainingName(Training t)
        {
            switch (t)
            {
                case Training.Green: return "新兵";
                case Training.Veteran: return "古参";
                default: return "通常";
            }
        }

        public static string StateName(CompanyState s)
        {
            switch (s)
            {
                case CompanyState.Normal: return "正常";
                case CompanyState.Shaken: return "動揺";
                case CompanyState.Routed: return "壊走";
                case CompanyState.Rallying: return "再集結";
                case CompanyState.Surrendered: return "降伏";
                default: return "離脱";
            }
        }

        public static string OrderName(OrderType t)
        {
            switch (t)
            {
                case OrderType.Advance: return "前進";
                case OrderType.Retreat: return "後退";
                case OrderType.Attack: return "攻撃";
                case OrderType.Pin: return "拘束";
                case OrderType.Entrench: return "陣地構築";
                default: return "保持";
            }
        }

        public static string AspectName(AttackAspect a)
        {
            switch (a)
            {
                case AttackAspect.Front: return "正面";
                case AttackAspect.Flank: return "側面";
                case AttackAspect.Rear: return "背面";
                default: return "指定なし";
            }
        }

        string PinRangeName(PinRange r) =>
            r == PinRange.Near ? $"近い {_params.Behavior.PinDistanceM:0}m" : $"遠い {_params.Behavior.PinDistanceFarM:0}m";

        public static string MoveStyleName(MoveStyle s)
        {
            switch (s)
            {
                case MoveStyle.Sideways: return "横歩き";
                case MoveStyle.Backward: return "後ろ向き";
                case MoveStyle.Turning: return "旋回中";
                default: return null;
            }
        }

        List<Company> BlueCompanies() => _sim.Companies.Where(c => c.Side == Side.Blue).ToList();
    }
}
