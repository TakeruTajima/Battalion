"""
大隊指揮RTS プロトタイプ — 戦闘解決のリファレンス実装（検算用）

目的:
  仕様書「戦闘解決」「P0 詳細」の数値が意図どおりに働くかを、移動なし・平地・
  視界無制限の簡易条件で確かめる。P0 の EditMode 検算テストの正解値はこのスクリプトの
  出力（reference_results.json）を使う。

本実装との違い（意図的に省いたもの）:
  - 移動・地形・視界・接近戦・射程による補正（全シナリオ 300m、f_range=1.0）
  - 壊走後の逃走と再集結（壊走した時点でそのシナリオの決着とする）

実行:
  python3 combat_reference.py            # 各シナリオ 300 回、結果を表示し JSON に保存
  python3 combat_reference.py --runs 1000

依存: numpy
"""
import argparse
import json
import math

import numpy as np

# ---- パラメータ（仕様書の初期値。P0 の sim_params.json と同じ値） ----
P = {
    "dt_sec": 0.5,
    "k_per_min": 0.0133,  # 兵士1人・1分あたりの殺傷（平地・正面・通常練度）
    "aspect_cas": {"front": 1.0, "flank": 1.5, "rear": 2.0},
    "aspect_morale": {"front": 1.0, "flank": 2.0, "rear": 3.0},
    "first_hit_shock": {"flank": 10.0, "rear": 15.0},
    "fire_at_non_front_target": 0.5,  # 正面の弧の外にいる敵を撃つときの効率
    "morale_per_pct_loss": 1.5,
    "under_fire_per_min": 0.5,
    "encircled_per_min": 3.0,
    "training": {
        "green":   {"rout": 35, "fire": 0.85, "morale_sens": 1.2},
        "regular": {"rout": 25, "fire": 1.00, "morale_sens": 1.0},
        "veteran": {"rout": 15, "fire": 1.20, "morale_sens": 0.8},
    },
    "cap_min": 60.0,
}


class Unit:
    def __init__(self, name, side, n, quality, x, y, facing_deg):
        self.name, self.side, self.q = name, side, quality
        self.n0 = n
        self.n = n
        self.m = 100.0
        self.x, self.y = x, y
        self.facing = math.radians(facing_deg)
        self.routed = False
        self.shocked = set()

    def aspect_of(self, other):
        """other から撃たれたときの、自分から見た方向（正面／側面／背面）"""
        ang = math.atan2(other.y - self.y, other.x - self.x)
        d = abs((ang - self.facing + math.pi) % (2 * math.pi) - math.pi)
        deg = math.degrees(d)
        if deg <= 60:
            return "front"
        if deg <= 120:
            return "flank"
        return "rear"

    def dist(self, other):
        return math.hypot(other.x - self.x, other.y - self.y)


def pick_target(u, enemies):
    """P0 詳細の目標選択: 正面の弧(±60°)にいる最も近い敵 > それ以外で最も近い敵"""
    alive = [e for e in enemies if not e.routed and e.n > 0 and u.dist(e) <= 400]
    if not alive:
        return None
    front = [e for e in alive if u.aspect_of(e) == "front"]
    pool = front if front else alive
    return min(pool, key=lambda e: (u.dist(e), e.name))


def tick(units, rng, P):
    dt_min = P["dt_sec"] / 60.0
    # 1) 目標選択（刻みの開始時点の状態で）
    shots = []  # (shooter, target, aspect_on_target)
    for u in units:
        if u.routed or u.n <= 0:
            continue
        enemies = [e for e in units if e.side != u.side]
        t = pick_target(u, enemies)
        if t is not None:
            shots.append((u, t, t.aspect_of(u)))
    # 2) 損害と士気の計算（同時解決: 全て計算してから適用）
    cas = {u.name: 0 for u in units}
    mor = {u.name: 0.0 for u in units}
    aspects_hit = {u.name: set() for u in units}
    for u, t, asp in shots:
        tq = P["training"][u.q]
        eff = P["fire_at_non_front_target"] if u.aspect_of(t) != "front" else 1.0
        lam = P["k_per_min"] * u.n * tq["fire"] * P["aspect_cas"][asp] * eff * dt_min
        c = int(rng.poisson(lam))
        cas[t.name] += c
        mor[t.name] += c / t.n0 * 100 * P["morale_per_pct_loss"] * P["aspect_morale"][asp]
        aspects_hit[t.name].add(asp)
    # 3) 適用
    for u in units:
        if u.routed:
            continue
        hit = aspects_hit[u.name]
        m = mor[u.name]
        if hit:
            m += P["under_fire_per_min"] * dt_min
        for asp in hit:
            if asp != "front" and asp not in u.shocked:
                u.shocked.add(asp)
                m += P["first_hit_shock"][asp]
        encircled = len(hit) >= 2 or rear_corridor_blocked(u, units)
        if encircled:
            m += P["encircled_per_min"] * dt_min
        u.n = max(0, u.n - cas[u.name])
        u.m -= m * P["training"][u.q]["morale_sens"]
        if u.m <= P["training"][u.q]["rout"]:
            u.routed = True


def rear_corridor_blocked(u, units):
    """P0 詳細の包囲判定その2: 背後 500m・幅 ±200m の帯に敵中隊の中心がある"""
    fx, fy = math.cos(u.facing), math.sin(u.facing)
    for e in units:
        if e.side == u.side or e.routed or e.n <= 0:
            continue
        dx, dy = e.x - u.x, e.y - u.y
        along = dx * fx + dy * fy  # 正なら前方
        side = -dx * fy + dy * fx
        if -500 <= along < 0 and abs(side) <= 200:
            return True
    return False


# ---- シナリオ（P0 の検算テストと同じ配置。座標は m、向きは度、x 正が東） ----
def scenario(name):
    if name == "S1_1v1_front":
        return [Unit("A", "blue", 150, "regular", 0, 0, 0), Unit("R", "red", 150, "regular", 300, 0, 180)], "R_or_A"
    if name == "S2_2v1_front_front":
        return [Unit("A", "blue", 150, "regular", 0, 0, 0), Unit("B", "blue", 150, "regular", 0, 220, -36),
                Unit("R", "red", 150, "regular", 300, 0, 180)], "R"
    if name == "S3_2v1_front_flank":
        return [Unit("A", "blue", 150, "regular", 0, 0, 0), Unit("B", "blue", 150, "regular", 300, 300, -90),
                Unit("R", "red", 150, "regular", 300, 0, 180)], "R"
    if name == "S4_2v1_front_rear":
        return [Unit("A", "blue", 150, "regular", 0, 0, 0), Unit("B", "blue", 150, "regular", 600, 0, 180),
                Unit("R", "red", 150, "regular", 300, 0, 180)], "R"
    if name == "S5_vet_vs_reg":
        return [Unit("V", "blue", 150, "veteran", 0, 0, 0), Unit("R", "red", 150, "regular", 300, 0, 180)], "R_or_V"
    raise ValueError(name)


SCENARIOS = ["S1_1v1_front", "S2_2v1_front_front", "S3_2v1_front_flank", "S4_2v1_front_rear", "S5_vet_vs_reg"]


def run_one(name, seed):
    units, _ = scenario(name)
    rng = np.random.default_rng(seed)
    t = 0.0
    while t < P["cap_min"]:
        tick(units, rng, P)
        t += P["dt_sec"] / 60.0
        if any(u.routed for u in units):
            break
    by = {u.name: u for u in units}
    routed = [u.name for u in units if u.routed]
    return t, by, routed


def summarize(name, runs):
    times, routed_first, loss = [], {}, {}
    for s in range(1, runs + 1):
        t, by, routed = run_one(name, s)
        times.append(t)
        for r in routed:
            routed_first[r] = routed_first.get(r, 0) + 1
        for k, u in by.items():
            loss.setdefault(k, []).append(1 - u.n / u.n0)
    times = np.array(times)
    return {
        "runs": runs,
        "rout_time_min": {"p05": round(float(np.percentile(times, 5)), 2),
                          "median": round(float(np.median(times)), 2),
                          "p95": round(float(np.percentile(times, 95)), 2)},
        "routed_count": routed_first,
        "loss_mean": {k: round(float(np.mean(v)), 3) for k, v in loss.items()},
    }


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", type=int, default=300)
    ap.add_argument("--out", default="reference_results.json")
    a = ap.parse_args()
    res = {n: summarize(n, a.runs) for n in SCENARIOS}
    res["_params"] = P
    print(json.dumps({k: v for k, v in res.items() if k != "_params"}, ensure_ascii=False, indent=1))
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=1)
