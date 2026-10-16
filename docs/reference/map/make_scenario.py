"""
シナリオA「リーデル村」の地図データを生成する。

出力: scenario_a.json
  座標系: x が東、y が北。原点は南西の角。1マス 50m、60×40 マス（3000m×2000m）。
  terrain: 行ごとの文字列。rows[0] が最も北の行（y=39）。読みやすさのため北が上。
    '.' 平地  'F' 森林  'V' 村落  'W' 川  'B' 橋  'S' 浅瀬  'T' 塹壕（平地の上に構築済み）
  elevation: 各マスの標高（m、整数）。terrain と同じ並び（北が上）。
"""
import json
import math
import sys

# ステージ1: 頂上の塹壕・孤立した村（敵の配置に死角がある。地形を読めば正面から肉薄できる）
# ステージ2: 斜面の塹壕・相互支援（死角はない。機動が要る）
STAGE = int(sys.argv[1]) if len(sys.argv) > 1 else 2

W, H, CELL = 60, 40, 50

terrain = [["." for _ in range(W)] for _ in range(H)]  # terrain[j][i], j=0 が南
elev = [[0.0 for _ in range(W)] for _ in range(H)]

# 標高：西から東へゆるく上がる地面 + 南北に走る稜線（x=36 付近、最大 +25m）
for j in range(H):
    for i in range(W):
        base = 2.0 + 4.0 * i / (W - 1)
        # 稜線は j=6〜35 の範囲。端は低くなる
        along = 1.0
        if j < 10:
            along = max(0.0, (j - 4) / 6)
        elif j > 31:
            along = max(0.0, (36 - j) / 5)
        sigma = 2.2 if STAGE == 1 else 4.0  # ステージ1は急で丸い稜線（頂上から斜面が見えない）
        ridge = 25.0 * along * math.exp(-((i - 36) ** 2) / (2 * sigma ** 2))
        elev[j][i] = base + ridge

# 北の森：j=30〜36、i=12〜46（側面への迂回路）
for j in range(30, 37):
    for i in range(12, 47):
        terrain[j][i] = "F"
# 森の縁を少し不規則に
for i in range(12, 47):
    if i % 5 == 0:
        terrain[30][i] = "."
    if i % 7 == 3:
        terrain[36][i] = "."

# 小さな雑木林（中央西寄り、遮蔽物）
for j in range(17, 20):
    for i in range(18, 21):
        terrain[j][i] = "F"

# リーデル村：i=38〜41、j=18〜21（200m四方＝中隊1個分。稜線の東側で、西からは稜線で見えない）
for j in range(18, 22):
    for i in range(38, 42):
        terrain[j][i] = "V"

# 川：南側を東西に。j=5〜6。橋は i=40、浅瀬は i=20
for i in range(W):
    terrain[5][i] = "W"
    terrain[6][i] = "W"
for j in (5, 6):
    terrain[j][40] = "B"
    terrain[j][20] = "S"

# 敵の塹壕。ステージ1は頂上 i=36（斜面が死角）、ステージ2は西斜面 i=34（軍事的稜線）
trench_i = 36 if STAGE == 1 else 34
for j in range(14, 26):
    if terrain[j][trench_i] == ".":
        terrain[j][trench_i] = "T"

objects = {
    "village": {"name": "リーデル村", "cells": [[i, j] for j in range(18, 22) for i in range(38, 42)]},
    "bridge": {"cells": [[40, 5], [40, 6]]},
    "ford": {"cells": [[20, 5], [20, 6]]},
}

# 初期配置（m、向きは度。0=東、90=北）
deploy_a = {
    "blue": [
        {"id": "B1", "x": 300, "y": 1450, "facing": 0, "note": "第1中隊 慎重・通常"},
        {"id": "B2", "x": 300, "y": 1050, "facing": 0, "note": "第2中隊 冷静・通常"},
        {"id": "B3", "x": 300, "y": 650, "facing": 0, "note": "第3中隊 猪突・古参"},
        {"id": "B4", "x": 100, "y": 1050, "facing": 0, "note": "第4中隊 機敏・通常（予備）"},
    ],
    "red": ([
        {"id": "R1", "x": 1825, "y": 1000, "facing": 180, "note": "稜線の頂上の塹壕"},
        {"id": "R2", "x": 2025, "y": 1000, "facing": 180, "note": "村落"},
        {"id": "R3", "x": 2450, "y": 1000, "facing": 180, "note": "予備（村の後方）"},
    ] if STAGE == 1 else [
        {"id": "R1", "x": 1725, "y": 1125, "facing": 180, "note": "斜面の塹壕（北）"},
        {"id": "R2", "x": 1725, "y": 875, "facing": 180, "note": "斜面の塹壕（南）"},
        {"id": "R3", "x": 1975, "y": 1000, "facing": 180, "note": "予備（村）"},
    ]),
}

rows_t = ["".join(terrain[j]) for j in reversed(range(H))]
rows_e = [[int(round(elev[j][i])) for i in range(W)] for j in reversed(range(H))]

out = {
    "name": f"scenario_a{STAGE}",
    "title": f"シナリオA ステージ{STAGE}：リーデル村の奪取" + ("（頂上の塹壕）" if STAGE == 1 else "（相互支援の陣地）"),
    "stage": STAGE,
    "reserve_script": {"unit": "R3", "trigger": "攻撃側の中隊が、守備の中心から trigger_radius_m 以内に入った",
                        "trigger_center": [1975, 1000] if STAGE == 1 else [1725, 1000],
                        "trigger_radius_m": 400 if STAGE == 1 else 450,
                        "action": "引き金になった中隊のうち R3 に最も近いものの側面（R3 側）250m へ攻撃"},
    "cell_m": CELL, "width": W, "height": H,
    "origin": "southwest", "rows_order": "north_first",
    "legend": {".": "plain", "F": "forest", "V": "village", "W": "river", "B": "bridge", "S": "ford", "T": "trench"},
    "terrain": rows_t,
    "elevation": rows_e,
    "objects": objects,
    "deploy": deploy_a,
    "time_limit_min": 90,
}
fn = f"scenario_a{STAGE}.json"
with open(fn, "w", encoding="utf-8") as f:
    json.dump(out, f, ensure_ascii=False, indent=1)
print("written", fn)
