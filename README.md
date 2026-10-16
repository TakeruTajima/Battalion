# 大隊指揮RTS プロトタイプ

大隊規模の部隊を指揮するRTSの試作。仕様は [`docs/spec.md`](docs/spec.md)。

現在の段階：**P0（戦闘シミュレーションの核、画面なし）**。報告は [`docs/reports/P0_report.md`](docs/reports/P0_report.md)。

## 構成

| パス | 内容 |
| --- | --- |
| `Assets/Sim/` | アセンブリ `Sim`（`noEngineReferences: true`）。Unity に依存しない純粋な C#。名前空間 `Battalion.Sim` |
| `Assets/Sim.Tests/` | アセンブリ `Sim.Tests`（EditMode、NUnit）。検算・再現性・振る舞いのテスト |
| `Assets/StreamingAssets/sim_params.json` | パラメータ。初期値は `docs/reference/sim_params.initial.json` と同一（テストで確認） |
| `docs/` | 仕様書と検算のリファレンス（`combat_reference.py` は変更しない） |
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
