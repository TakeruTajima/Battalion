# 大隊指揮RTS プロトタイプ

大隊規模の部隊を指揮するRTSの試作。仕様は [`docs/spec.md`](docs/spec.md)。

現在の段階：**P2b.2（壊走・動揺と命令書）まで完了**（P1 はタグ `proto-p1-map` で完了）。報告は [`docs/reports/`](docs/reports/)（P0・P0.1 は `P0_report.md`、P0.2 は `P0.2_report.md`、P1a・P1a.1 は `P1a_report.md`、P1b は `P1b_report.md`、P1c は `P1c_report.md`、P1d は `P1d_report.md`、P1e は `P1e_report.md`、P1e.1 は `P1e.1_report.md`、P1e.2 は `P1e.2_report.md`、P1f は `P1f_report.md`（画像は `P1f/`）、P1g は `P1g_report.md`、P1g.1 は `P1g.1_report.md`、P2a は `P2a_report.md`（画像は `P2a/`）、P2b は `P2b_report.md`（画像は `P2b/`）、P2b.1 は `P2b.1_report.md`（画像は `P2b.1/`）、P2b.2 は `P2b.2_report.md`。操作の早見表は P1b の報告と、変わったところは P1c・P1d・P1e・P1f・P2a・P2b・P2b.1 の報告にある）。

遊び方：Unity 6.3 LTS（6000.3.11f1）で開き、`Assets/Scenes/Battle.unity` を再生する（6.4 など別の版で開くと ProjectSettings とパッケージが書き換わるので、6.3 LTS で開く）。

## 構成

| パス | 内容 |
| --- | --- |
| `Assets/Sim/` | アセンブリ `Sim`（`noEngineReferences: true`）。Unity に依存しない純粋な C#。名前空間 `Battalion.Sim` |
| `Assets/View/` | アセンブリ `Battalion.View`（Unity の表示と操作。IMGUI）。`Editor/` にシーンを作る・画像を撮るエディタの道具 |
| `Assets/View.Tests/` | 表示側の EditMode テスト（Unity でだけ回る） |
| `Assets/Scenes/Battle.unity` | シーン（カメラと `BattleApp` だけ） |
| `Assets/Sim.Tests/` | アセンブリ `Sim.Tests`（EditMode、NUnit）。検算・再現性・振る舞いのテスト |
| `Assets/StreamingAssets/maps/` | `maps/` の写し（実行時に読む。同一であることをテストで確認） |
| `Assets/StreamingAssets/sim_params.json` | パラメータ（戦闘、地形の表、視線、地図まわりの値）。初期値は `docs/reference/sim_params.initial.json` と同一（テストで確認） |
| `docs/` | 仕様書と検算のリファレンス（`docs/reference/` 以下は変更しない）。`docs/reference/map/` は P1a の視線と経路探索のリファレンス、`docs/reference/sketch/` は概算シミュレーション |
| `maps/` | シナリオAの地図（ステージ1・2）。P1a で読み込む |
| `tools/dotnet/` | Unity の外で同じソースをビルド・テストする .NET プロジェクト |

## テストの実行

Unity 6.3 LTS：プロジェクトを開き、Window → General → Test Runner → EditMode → Run All。

.NET 8 SDK（Unity なし）：

```sh
cd tools/dotnet/Sim.Tests
dotnet test --logger "console;verbosity=normal"   # 検算の対照表と接近戦の結果も出力される
```

## 使い方（概略）

```csharp
var p = SimParams.LoadFile("Assets/StreamingAssets/sim_params.json");
var log = new FileEventSink("battle.jsonl");                  // JSON Lines
var sim = Scenarios.S3_2v1_front_flank().Build(p, seed: 1, log);
sim.Issue(Order.Advance(sim.Find("A").Id, new Vec2(100, 0)));  // 命令オブジェクト
while (sim.Time < 3600) sim.Step();                           // 1刻み 0.5 秒
```
