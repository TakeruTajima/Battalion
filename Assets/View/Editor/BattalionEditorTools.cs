using System;
using System.IO;
using Battalion.Sim;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Battalion.View.Editor
{
    /// <summary>
    /// エディタの道具。バッチモードから -executeMethod でも呼べる。
    /// - シーンを作る（カメラと BattleApp だけ）。ビルド設定に登録し、エディタの既定の動作モードを 2D にする
    /// - 撮影：会戦を進めて、地図と駒を PNG にする（地図の上の文字は写るが、パネルなど IMGUI の UI は写らない）
    /// </summary>
    public static class BattalionEditorTools
    {
        public const string ScenePath = "Assets/Scenes/Battle.unity";

        [MenuItem("Battalion/シーンを作り直す")]
        public static void BuildScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 1100;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color32(40, 44, 48, 255);
            cam.nearClipPlane = -100;
            cam.farClipPlane = 100;
            camGo.transform.position = new Vector3(1500, 1000, -10);

            new GameObject("BattleApp").AddComponent<BattleApp>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            EditorSettings.defaultBehaviorMode = EditorBehaviorMode.Mode2D;
            AssetDatabase.SaveAssets();
            Debug.Log("[Battalion] シーンを作った：" + ScenePath);
        }

        /// <summary>
        /// 撮影。コマンドラインの -snapshotDir の下に画像を書く。
        /// - 両ステージの全体と寄り：青は北の森から村の側面へ、南から稜線の塹壕の側面へ攻撃、正面は保持、第4は移動して陣地構築。25分進めてから撮る
        /// - 射撃の見本（ステージ1）：R1 の正面・側面・背面に青を置き、射撃線の3色、損害の数字、行動の印、出来事の文字を撮る
        /// - 視線確認（ステージ1）と露出の地図（ステージ2）
        /// </summary>
        public static void CaptureSnapshots()
        {
            string dir = ArgValue("-snapshotDir") ?? Path.Combine(Application.dataPath, "..", "Temp", "Snapshots");
            Directory.CreateDirectory(dir);
            try
            {
                foreach (int stage in new[] { 1, 2 }) CaptureBattle(stage, 25, dir);
                CaptureFireDemo(dir);
                Debug.Log("[Battalion] 撮影した：" + dir);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        sealed class Rig : IDisposable
        {
            public readonly GameObject Root;
            public readonly Camera Cam;
            public readonly RenderTexture Rt;
            public readonly BattleApp App;

            public Rig(int width, int height)
            {
                Root = new GameObject("CaptureRoot");
                var camGo = new GameObject("CaptureCamera");
                camGo.transform.SetParent(Root.transform);
                Cam = camGo.AddComponent<Camera>();
                Cam.orthographic = true;
                Cam.clearFlags = CameraClearFlags.SolidColor;
                Cam.backgroundColor = new Color32(40, 44, 48, 255);
                Cam.nearClipPlane = -100;
                Cam.farClipPlane = 100;
                Rt = new RenderTexture(width, height, 24);
                Cam.targetTexture = Rt;
                var appGo = new GameObject("BattleApp");
                appGo.transform.SetParent(Root.transform);
                App = appGo.AddComponent<BattleApp>();
            }

            public void Look(Vec2 center, float halfHeight)
            {
                Cam.orthographicSize = halfHeight;
                Cam.transform.position = new Vector3((float)center.X, (float)center.Y, -10);
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Root);
                var world = GameObject.Find("World");
                if (world != null) UnityEngine.Object.DestroyImmediate(world);
            }
        }

        static void CaptureBattle(int stage, int minutes, string dir)
        {
            using (var rig = new Rig(1500, 1000))
            {
                var sim = rig.App.BeginCapture(rig.Cam, stage, 1);
                var b1 = sim.Find("B1");
                var b2 = sim.Find("B2");
                var b3 = sim.Find("B3");
                var b4 = sim.Find("B4");
                var r1 = sim.Find("R1");
                sim.Issue(Order.Advance(b1.Id, new Vec2(1975, 1300), -90 * Angles.DegToRad));
                sim.Issue(Order.Advance(b2.Id, new Vec2(stage == 1 ? 1450 : 1350, 1000), 0));
                sim.Issue(Order.Attack(b3.Id, r1.Id, AttackAspect.Flank, -1));
                sim.Issue(Order.Entrench(b4.Id, new Vec2(900, 1300), 0));
                // 最後の1分半ぶんの損害と出来事だけを出す（×16 で現実の約1.5秒にあたる長さ）
                rig.App.StepForCapture(minutes * 120 - 180);
                rig.App.ClearFeedbackForCapture(true);
                rig.App.StepForCapture(180);
                rig.App.FlushDamageForCapture();

                rig.Look(new Vec2(1500, 1000), 1000);
                Shoot(rig, b1.Id, 0, false, false, null, Path.Combine(dir, $"stage{stage}_t{minutes}_overview.png"));
                rig.Look(new Vec2(1850, 1080), 420);
                Shoot(rig, b3.Id, 0, false, false, null, Path.Combine(dir, $"stage{stage}_t{minutes}_zoom.png"));
                rig.Look(new Vec2(1500, 1000), 1000);
                if (stage == 1)
                    Shoot(rig, -1, 0, false, false, r1.Position, Path.Combine(dir, $"stage{stage}_t{minutes}_sight_from_R1.png"));
                else
                    Shoot(rig, b3.Id, 1, false, false, null, Path.Combine(dir, $"stage{stage}_t{minutes}_exposure_blue.png"));
            }
        }

        /// <summary>
        /// 射撃の見本：ステージ1 の R1（頂上の塹壕、西向き）の正面（西）・側面（北）・背面寄り（東）に青を置く。
        /// B4 はその場で陣地構築、R2 は B3 を拘束、B2 は前進中。2分進めて、最後の1分半ぶんの損害の数字と、出来事の文字を出す。
        /// </summary>
        static void CaptureFireDemo(string dir)
        {
            using (var rig = new Rig(1500, 1000))
            {
                var sim = rig.App.BeginCapture(rig.Cam, 1, 7);
                var r1 = sim.Find("R1");
                var r2 = sim.Find("R2");
                var b1 = sim.Find("B1");
                var b2 = sim.Find("B2");
                var b3 = sim.Find("B3");
                var b4 = sim.Find("B4");
                b2.Position = new Vec2(1525, 1000);
                b2.Facing = 0;                       // 正面（西から東向き）
                b1.Position = new Vec2(1825, 1250);
                b1.Facing = -Math.PI / 2;            // 側面（北から南向き）
                b3.Position = new Vec2(2150, 830);
                b3.Facing = Math.PI;                 // 背面寄り（東から西向き）
                b4.Position = new Vec2(1400, 1380);
                b4.Facing = 0;
                sim.Issue(Order.Entrench(b4.Id));
                sim.Issue(Order.Pin(r2.Id, b3.Id));
                sim.Issue(Order.Advance(b2.Id, new Vec2(1600, 1000), 0));
                // 出来事の文字（側面・背面を突かれた等）は残し、最後の1分半ぶんの損害を数字にする
                rig.App.StepForCapture(60);
                rig.App.ClearFeedbackForCapture(false);
                rig.App.StepForCapture(180);
                rig.App.FlushDamageForCapture();
                rig.Look(new Vec2(1800, 1080), 360);
                Shoot(rig, r1.Id, 0, false, false, null, Path.Combine(dir, "fire_demo_stage1.png"));
            }
        }

        static void Shoot(Rig rig, int selected, int exposure, bool trigger, bool escape, Vec2? sight, string path)
        {
            rig.App.DrawForCapture(selected, exposure, trigger, escape, sight);
            rig.Cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rig.Rt;
            var tex = new Texture2D(rig.Rt.width, rig.Rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rig.Rt.width, rig.Rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int k = 0; k < args.Length - 1; k++)
                if (args[k] == name) return args[k + 1];
            return null;
        }
    }
}
