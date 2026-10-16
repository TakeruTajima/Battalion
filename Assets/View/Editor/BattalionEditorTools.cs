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
    /// - 撮影：会戦を進めて、地図と駒を PNG にする（UI の文字は IMGUI なので写らない）
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
        /// 撮影。コマンドラインの -snapshotDir の下に、ステージ1・2 の画像を書く。
        /// 青は北の森から村の側面へ、南から稜線へ、正面は保持。25分進めてから撮る。
        /// </summary>
        public static void CaptureSnapshots()
        {
            string dir = ArgValue("-snapshotDir") ?? Path.Combine(Application.dataPath, "..", "Temp", "Snapshots");
            Directory.CreateDirectory(dir);
            try
            {
                foreach (int stage in new[] { 1, 2 })
                {
                    Capture(stage, 25, dir);
                }
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

        static void Capture(int stage, int minutes, string dir)
        {
            var root = new GameObject("CaptureRoot");
            try
            {
                var camGo = new GameObject("CaptureCamera");
                camGo.transform.SetParent(root.transform);
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color32(40, 44, 48, 255);
                cam.nearClipPlane = -100;
                cam.farClipPlane = 100;
                var rt = new RenderTexture(1500, 1000, 24);
                cam.targetTexture = rt;

                var appGo = new GameObject("BattleApp");
                appGo.transform.SetParent(root.transform);
                var app = appGo.AddComponent<BattleApp>();
                var sim = app.BeginCapture(cam, stage, 1);

                cam.orthographicSize = 1000;
                cam.transform.position = new Vector3(1500, 1000, -10);

                var b1 = sim.Find("B1");
                var b2 = sim.Find("B2");
                var b3 = sim.Find("B3");
                var r1 = sim.Find("R1");
                sim.Issue(Order.Advance(b1.Id, new Vec2(1975, 1300), -90 * Angles.DegToRad));
                sim.Issue(Order.Advance(b2.Id, new Vec2(stage == 1 ? 1450 : 1350, 1000), 0));
                sim.Issue(Order.Attack(b3.Id, r1.Id, AttackAspect.Flank, -1));
                app.StepForCapture(minutes * 120);

                Shoot(app, cam, rt, b1.Id, 0, true, true, null, Path.Combine(dir, $"stage{stage}_t{minutes}_overview.png"));
                Shoot(app, cam, rt, b3.Id, 1, false, false, null, Path.Combine(dir, $"stage{stage}_t{minutes}_exposure_blue.png"));
                Shoot(app, cam, rt, -1, 0, false, false, r1.Position, Path.Combine(dir, $"stage{stage}_t{minutes}_sight_from_R1.png"));

                // 寄った画像（駒の形を確かめる）
                cam.orthographicSize = 300;
                cam.transform.position = new Vector3((float)r1.Position.X, (float)r1.Position.Y, -10);
                Shoot(app, cam, rt, b3.Id, 0, false, true, null, Path.Combine(dir, $"stage{stage}_t{minutes}_zoom_R1.png"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                var world = GameObject.Find("World");
                if (world != null) UnityEngine.Object.DestroyImmediate(world);
            }
        }

        static void Shoot(BattleApp app, Camera cam, RenderTexture rt, int selected, int exposure, bool trigger, bool escape, Vec2? sight, string path)
        {
            app.DrawForCapture(selected, exposure, trigger, escape, sight);
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
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
