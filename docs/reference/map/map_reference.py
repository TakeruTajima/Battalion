"""
P1a の地図ロジックのリファレンス実装（視線と経路探索）。
P1a の EditMode テストの正解値は、このスクリプトの出力 map_reference_results.json を使う。

視線（仕様書「P1 詳細仕様」）:
  - 観測者と目標の中心を結ぶ線分を 10m 刻みでサンプルする（両端を除く）
  - 目の高さは地面+2m。線の高さは両端の (標高+2m) の線形補間
  - 標高はマス中心の値を双線形補間して求める（両端もサンプル点も。マス単位の階段で斜面が視線を遮らないように）
  - サンプル点の標高が線の高さを超えたら遮られる（稜線）
  - 森林・村落のマス（観測者と目標がいるマスを除く）を通る距離を重み付きで足す。
    森林 1.0、村落 0.5。合計が 100m を超えたら遮られる
  - 視界距離 = 800m × (1 + 0.1 × max(0, 観測者の標高 − 目標の標高) / 10)
経路探索:
  - 8近傍の A*。マスの中心から中心へ。川は通れない。斜め移動で通れないマスの角を切らない
  - 1歩のコスト = 距離(m) ÷ 移動倍率 ＋ 脅威の重み × 露出 × 50
  - 露出 = そのマスの中心が、400m 以内の敵のいずれかから見えていれば 1、そうでなければ 0
  - 脅威の重み：慎重 3.0、冷静 1.0、機敏 1.0、猪突 0.3
"""
import heapq
import json
import math
import sys

MOVE = {".": 1.0, "F": 0.5, "V": 0.7, "W": 0.0, "B": 1.0, "S": 0.3, "T": 0.8, "C": 0.6}
OBSCURE = {"F": 1.0, "V": 0.5}
THREAT_W = {"cautious": 3.0, "calm": 1.0, "agile": 1.0, "reckless": 0.3}


class Map:
    def __init__(self, path):
        d = json.load(open(path, encoding="utf-8"))
        self.w, self.h, self.cell = d["width"], d["height"], d["cell_m"]
        rows = d["terrain"]
        erows = d["elevation"]
        # 内部は [j][i]、j=0 が南
        self.t = [list(rows[self.h - 1 - j]) for j in range(self.h)]
        self.e = [erows[self.h - 1 - j] for j in range(self.h)]
        self.d = d

    def cell_of(self, x, y):
        i = min(self.w - 1, max(0, int(x // self.cell)))
        j = min(self.h - 1, max(0, int(y // self.cell)))
        return i, j

    def ter(self, x, y):
        i, j = self.cell_of(x, y)
        return self.t[j][i]

    def elev(self, x, y):
        i, j = self.cell_of(x, y)
        return self.e[j][i]

    def elev_interp(self, x, y):
        """マス中心の標高を双線形補間した値（視線用）"""
        fx = x / self.cell - 0.5
        fy = y / self.cell - 0.5
        i0 = min(self.w - 1, max(0, int(math.floor(fx))))
        j0 = min(self.h - 1, max(0, int(math.floor(fy))))
        i1, j1 = min(self.w - 1, i0 + 1), min(self.h - 1, j0 + 1)
        tx = min(1.0, max(0.0, fx - i0))
        ty = min(1.0, max(0.0, fy - j0))
        e = self.e
        return (e[j0][i0] * (1 - tx) * (1 - ty) + e[j0][i1] * tx * (1 - ty)
                + e[j1][i0] * (1 - tx) * ty + e[j1][i1] * tx * ty)

    def center(self, i, j):
        return (i + 0.5) * self.cell, (j + 0.5) * self.cell


def los(m, ox, oy, tx, ty):
    """戻り値: (見えるか, 理由)"""
    dist = math.hypot(tx - ox, ty - oy)
    eo, et = m.elev_interp(ox, oy), m.elev_interp(tx, ty)
    vis = 800.0 * (1 + 0.1 * max(0.0, eo - et) / 10.0)
    if dist > vis:
        return False, "range"
    oc, tc = m.cell_of(ox, oy), m.cell_of(tx, ty)
    n = int(dist // 10)
    obsc = 0.0
    for k in range(1, n):
        s = k * 10.0 / dist
        x, y = ox + (tx - ox) * s, oy + (ty - oy) * s
        c = m.cell_of(x, y)
        z = (eo + 2.0) * (1 - s) + (et + 2.0) * s
        if m.elev_interp(x, y) > z:
            return False, "ridge"
        if c != oc and c != tc:
            obsc += OBSCURE.get(m.t[c[1]][c[0]], 0.0) * 10.0
            if obsc > 100.0:
                return False, "cover"
    return True, "clear"


def exposure_map(m, enemies):
    exp = [[0] * m.w for _ in range(m.h)]
    for j in range(m.h):
        for i in range(m.w):
            cx, cy = m.center(i, j)
            for (ex, ey) in enemies:
                if math.hypot(cx - ex, cy - ey) <= 400 and los(m, ex, ey, cx, cy)[0]:
                    exp[j][i] = 1
                    break
    return exp


def astar(m, start, goal, exp, threat_w):
    si, sj = m.cell_of(*start)
    gi, gj = m.cell_of(*goal)
    openh = [(0.0, 0.0, si, sj)]
    came, g = {}, {(si, sj): 0.0}
    while openh:
        f, gc, i, j = heapq.heappop(openh)
        if (i, j) == (gi, gj):
            break
        if gc > g.get((i, j), 1e18):
            continue
        for di in (-1, 0, 1):
            for dj in (-1, 0, 1):
                if di == 0 and dj == 0:
                    continue
                ni, nj = i + di, j + dj
                if not (0 <= ni < m.w and 0 <= nj < m.h):
                    continue
                mv = MOVE[m.t[nj][ni]]
                if mv <= 0:
                    continue
                if di != 0 and dj != 0:
                    if MOVE[m.t[j][ni]] <= 0 or MOVE[m.t[nj][i]] <= 0:
                        continue
                step = m.cell * (math.sqrt(2) if di and dj else 1.0)
                cost = step / mv + threat_w * exp[nj][ni] * m.cell
                ng = gc + cost
                if ng < g.get((ni, nj), 1e18):
                    g[(ni, nj)] = ng
                    came[(ni, nj)] = (i, j)
                    h = math.hypot(gi - ni, gj - nj) * m.cell  # 移動倍率1.0が上限なので許容的
                    heapq.heappush(openh, (ng + h, ng, ni, nj))
    path = [(gi, gj)]
    while path[-1] != (si, sj):
        path.append(came[path[-1]])
    path.reverse()
    return path, g[(gi, gj)]


def path_stats(m, path, exp):
    length = 0.0
    exposed = 0.0
    forest = 0.0
    for a, b in zip(path, path[1:]):
        step = m.cell * (math.sqrt(2) if a[0] != b[0] and a[1] != b[1] else 1.0)
        length += step
        exposed += step * exp[b[1]][b[0]]
        if m.t[b[1]][b[0]] == "F":
            forest += step
    return {"cells": len(path), "length_m": round(length, 1), "exposed_m": round(exposed, 1),
            "forest_m": round(forest, 1)}


if __name__ == "__main__":
    mp = sys.argv[1] if len(sys.argv) > 1 else "scenario_a1.json"
    m = Map(mp)
    r1 = next(u for u in m.d["deploy"]["red"] if u["id"] == "R1")
    los_cases = {
        "L1_west_field_to_village_ridge": (1300, 1000, 2000, 1000),
        "L2_west_field_to_R1_trench": (r1["x"] - 375, r1["y"], r1["x"], r1["y"]),
        "L3_forest_exit_to_village": (2050, 1475, 1975, 1000),
        "L4_inside_forest_75m": (1000, 1625, 1000, 1700),
        "L5_inside_forest_200m": (1000, 1525, 1000, 1725),
        "L6_field_to_forest_edge": (1000, 1200, 1025, 1525),
        "L7_field_to_forest_deep": (1000, 1200, 1025, 1775),
        "L8_R1_trench_to_slope_250m": (r1["x"], r1["y"], r1["x"] - 250, r1["y"]),
        "L9_beyond_range": (100, 100, 1000, 100),
        "L10_village_looks_north": (1975, 1000, 2025, 1300),
    }
    res = {"los": {}, "paths": {}}
    for k, (a, b, c, d) in los_cases.items():
        v, why = los(m, a, b, c, d)
        res["los"][k] = {"from": [a, b], "to": [c, d], "visible": v, "reason": why}
    enemies = [(u["x"], u["y"]) for u in m.d["deploy"]["red"]]
    exp = exposure_map(m, enemies)
    res["exposed_cells"] = sum(map(sum, exp))
    path_cases = {
        "P_north_flank": ((300, 1450), (2025, 1275)),     # 第1中隊 → 村の北 250m（側面攻撃の発起点）
        "P_south_approach": ((300, 650), (2125, 825)),    # 第3中隊 → 村の南東。性格で経路が分かれる
    }
    for name, (start, goal) in path_cases.items():
        for p in ["cautious", "calm", "reckless"]:
            path, cost = astar(m, start, goal, exp, THREAT_W[p])
            st = path_stats(m, path, exp)
            st["cost"] = round(cost, 1)
            st["from"], st["to"] = list(start), list(goal)
            st["path"] = path
            res["paths"][name + "_" + p] = st
    out = sys.argv[2] if len(sys.argv) > 2 else "map_reference_results.json"
    res["map"] = mp
    json.dump(res, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    for k, v in res["los"].items():
        print(k, v["visible"], v["reason"])
    print("exposed cells", res["exposed_cells"])
    for k, v in res["paths"].items():
        print(k, {kk: vv for kk, vv in v.items() if kk != "path"})
