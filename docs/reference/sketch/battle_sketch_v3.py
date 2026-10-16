"""
シナリオA（ステージ1・2）の概算シミュレーション v3。検討用のスケッチで、仕様の正解値ではない。
規則は仕様書（参照実装 v3）に合わせる:
  - 包囲は「退路の見積もり」: 士気50以下で5秒ごとに16方位の逃げ道を見積もり、
    どの方位でも2割以上を失う見込みなら包囲（絶望 -15、以後毎分 -3、塹壕・村の中は半分）。包囲中に壊走すれば降伏
  - 壊走は見積もり損害が最も少ない方位へ逃げる
  - 味方中隊の中心どうしは200m以上離れる（重なり禁止）
  - 赤の R1・R2 は向きを変えずに保持。R3（予備）は台本で逆襲
省略: 伝令の遅延、霧、砲兵、再集結、旋回の速さ、陣地構築
"""
import json, math, sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'map'))
MAPS = os.path.join(HERE, '..', '..', '..', 'maps')
import numpy as np
import map_reference as MR

TRAIN = {"green": (35, 0.85, 1.2), "regular": (25, 1.0, 1.0), "veteran": (15, 1.2, 0.8)}
COVER = {'.': 1.0, 'F': 0.6, 'V': 0.5, 'T': 0.3, 'B': 1.0, 'S': 1.0, 'W': 1.0}
DT = 0.5
K = 0.0133 / 60
SPACING = 200.0


class U:
    def __init__(s, uid, side, x, y, fdeg, q="regular", pers="calm"):
        s.id, s.side, s.x, s.y, s.f = uid, side, float(x), float(y), math.radians(fdeg)
        s.q, s.pers = q, pers
        s.n0 = s.n = 150
        s.m = 100.0
        s.state = "ok"
        s.path, s.goal_face = [], None
        s.moving = False
        s.shocked = set()
        s.despaired = False
        s.enc, s.enc_next, s.flee = False, 0.0, None

    def alive(s):
        return s.state == "ok"


def aspect(t, sx, sy):
    ang = math.atan2(sy - t.y, sx - t.x)
    d = math.degrees(abs((ang - t.f + math.pi) % (2 * math.pi) - math.pi))
    return "front" if d <= 60 else ("flank" if d <= 120 else "rear")


_PASS = None


def escape_estimate(M, u, units):
    """仕様（参照実装 v3）と同じ見積もり。加えて、川に突き当たる方位は逃げられない、地図の外へ出られれば逃げ切りとする"""
    global _PASS
    if _PASS is None:
        _PASS = np.array([[MR.MOVE.get(M.t[j][i], 1.0) > 0 for i in range(M.w)] for j in range(M.h)])
    foes = [e for e in units if e.side != u.side and e.alive()]
    s = np.arange(1, int(500 / 1.8) + 1) * 1.8
    ex = np.array([e.x for e in foes]) if foes else np.zeros(0)
    ey = np.array([e.y for e in foes]) if foes else np.zeros(0)
    rate = np.array([K * e.n * TRAIN[e.q][1] for e in foes]) if foes else np.zeros(0)
    best = (float("inf"), u.f + math.pi)
    for k in range(16):
        a = 2 * math.pi * k / 16
        px = u.x + math.cos(a) * s
        py = u.y + math.sin(a) * s
        inside = (px >= 0) & (px < 3000) & (py >= 0) & (py < 2000)
        if not inside.all():
            cut = int(np.argmin(inside))
            px, py = px[:cut], py[:cut]
        ii = np.clip((px // 50).astype(int), 0, M.w - 1)
        jj = np.clip((py // 50).astype(int), 0, M.h - 1)
        if len(px) and not _PASS[jj, ii].all():
            continue
        if len(foes) and len(px):
            d = np.hypot(px[:, None] - ex[None, :], py[:, None] - ey[None, :])
            lost = float(((np.where(d <= 150, 1.3, 1.0) * (d <= 400)) * rate[None, :]).sum()) * 2.0
        else:
            lost = 0.0
        frac = lost / max(1, u.n)
        if frac < best[0]:
            best = (frac, a)
    if best[0] == float("inf"):
        best = (9.9, u.f + math.pi)
    return best


def run(M, plan, seed, tmax_min=90, trace=None):
    rng = np.random.default_rng(seed)
    d = M.d["deploy"]
    pers = {"B1": "cautious", "B2": "calm", "B3": "reckless", "B4": "agile"}
    units = [U(b["id"], "blue", b["x"], b["y"], b["facing"], "veteran" if b["id"] == "B3" else "regular", pers[b["id"]]) for b in d["blue"]]
    units += [U(r["id"], "red", r["x"], r["y"], r["facing"]) for r in d["red"]]
    by = {u.id: u for u in units}
    exp = MR.exposure_map(M, [(u.x, u.y) for u in units if u.side == "red"])
    zero = [[0] * M.w for _ in range(M.h)]
    rs = M.d["reserve_script"]
    orders = sorted(plan(by), key=lambda o: o[0])
    oi, t, los_cache, triggered, events = 0, 0.0, {}, False, []

    def go(u, gx, gy, face, expmap):
        path, _ = MR.astar(M, (u.x, u.y), (gx, gy), expmap, MR.THREAT_W.get(u.pers, 1.0))
        u.path = [M.center(i, j) for i, j in path[1:]] + [(gx, gy)]
        u.goal_face = face

    while t < tmax_min * 60:
        while oi < len(orders) and orders[oi][0] <= t:
            _, uid, gx, gy, face = orders[oi]; oi += 1
            if by[uid].alive():
                go(by[uid], gx, gy, face, exp)
        if not triggered:
            cx, cy = rs["trigger_center"]
            near = [b for b in units if b.side == "blue" and b.alive() and math.hypot(b.x - cx, b.y - cy) <= rs["trigger_radius_m"]]
            r3 = by[rs["unit"]]
            if near and r3.alive():
                triggered = True
                tgt = min(near, key=lambda b: math.hypot(b.x - r3.x, b.y - r3.y))
                lx, ly = -math.sin(tgt.f), math.cos(tgt.f)
                s = 1 if (r3.x - tgt.x) * lx + (r3.y - tgt.y) * ly > 0 else -1
                go(r3, tgt.x + s * lx * 250, tgt.y + s * ly * 250, None, zero)
                events.append((t, f"{r3.id} 逆襲 → {tgt.id}"))
        # 移動
        for u in units:
            u.moving = False
            if u.state == "routed":
                a = u.flee if u.flee is not None else u.f + math.pi
                u.f = a
                nx, ny = u.x + math.cos(a) * 1.8 * DT, u.y + math.sin(a) * 1.8 * DT
                if MR.MOVE.get(M.ter(nx, ny), 1.0) > 0:
                    u.x, u.y = nx, ny
                if not (0 <= u.x < 3000 and 0 <= u.y < 2000):
                    u.state = "left"; events.append((t, u.id + " 離脱"))
                continue
            if not u.alive() or not u.path or u.m <= 50:
                if u.m <= 50:
                    u.path = []
                continue
            pinned = any(e.side != u.side and e.alive() and math.hypot(e.x - u.x, e.y - u.y) <= 150 for e in units)
            spd = 1.2 * (MR.MOVE.get(M.ter(u.x, u.y), 1.0) or 0.3) * (0.5 if pinned else 1.0)
            wx, wy = u.path[0]
            dx, dy = wx - u.x, wy - u.y
            dist = math.hypot(dx, dy)
            u.f = math.atan2(dy, dx)
            if dist <= spd * DT + 1e-6:
                u.x, u.y = wx, wy; u.path.pop(0)
                if not u.path and u.goal_face is not None:
                    u.f = math.radians(u.goal_face)
            else:
                u.x += dx / dist * spd * DT; u.y += dy / dist * spd * DT
            u.moving = True
        # 重なり禁止（味方どうし200m）：近すぎる組を互いに押し離す
        for a in units:
            for b in units:
                if a is b or a.side != b.side or not a.alive() or not b.alive() or a.id > b.id:
                    continue
                dd = math.hypot(b.x - a.x, b.y - a.y)
                if dd < SPACING:
                    if dd < 1e-6:
                        ux, uy = 0.0, 1.0
                    else:
                        ux, uy = (b.x - a.x) / dd, (b.y - a.y) / dd
                    push = (SPACING - dd) / 2
                    a.x -= ux * push; a.y -= uy * push
                    b.x += ux * push; b.y += uy * push
        # 目標選択
        if int(t * 2) % 4 == 0:
            los_cache = {}
        shots = []
        for u in units:
            if not u.alive():
                continue
            cands = []
            for e in units:
                if e.side == u.side or e.state not in ("ok", "routed"):
                    continue
                dd = math.hypot(e.x - u.x, e.y - u.y)
                if dd > 400:
                    continue
                key = (u.id, e.id)
                if key not in los_cache:
                    los_cache[key] = MR.los(M, u.x, u.y, e.x, e.y)[0]
                if los_cache[key]:
                    cands.append((dd, e))
            if cands:
                front = [c for c in cands if aspect(u, c[1].x, c[1].y) == "front"]
                dd, e = min(front or cands, key=lambda c: (c[0], c[1].id))
                shots.append((u, e, dd, bool(front)))
        cas = {u.id: 0 for u in units}; mor = {u.id: 0.0 for u in units}; hit = {u.id: set() for u in units}
        for u, e, dd, infront in shots:
            asp = aspect(e, u.x, u.y)
            ter = M.ter(e.x, e.y)
            cover = 1.0 if (ter == 'T' and e.moving) else COVER.get(ter, 1.0)
            lam = K * u.n * TRAIN[u.q][1] * {"front": 1, "flank": 1.5, "rear": 2}[asp] \
                * (2.0 if dd <= 50 else (1.3 if dd <= 150 else 1.0)) * cover \
                * (1.0 if infront else 0.5) * (0.7 if u.m <= 50 else 1.0) \
                * (2.0 if (e.state == "routed" and dd <= 150) else 1.0) * DT
            c = int(rng.poisson(lam))
            cas[e.id] += c
            mor[e.id] += c / e.n0 * 100 * 1.5 * {"front": 1, "flank": 2, "rear": 3}[asp] * (1.5 if dd <= 50 else 1.0)
            hit[e.id].add(asp)
        for u in units:
            u.n = max(0, u.n - cas[u.id])
            if not u.alive():
                continue
            m = mor[u.id] + (0.5 / 60 * DT if hit[u.id] else 0.0)
            for a in hit[u.id]:
                if a != "front" and a not in u.shocked:
                    u.shocked.add(a); m += 10 if a == "flank" else 15
            if u.m <= 50 and t >= u.enc_next:
                frac, bearing = escape_estimate(M, u, units)
                u.enc, u.flee, u.enc_next = frac >= 0.20, bearing, t + 5.0
            if u.enc:
                if not u.despaired:
                    u.despaired = True; m += 15
                    events.append((t, u.id + " 退路なし"))
                m += 3 / 60 * DT * (0.5 if M.ter(u.x, u.y) in ('T', 'V') else 1.0)
            u.m -= m * TRAIN[u.q][2]
            if u.m <= TRAIN[u.q][0] or u.n <= 0:
                if u.enc:
                    u.state = "surrendered"; events.append((t, u.id + " 降伏"))
                else:
                    u.state = "routed"; events.append((t, u.id + " 壊走"))
                    if u.flee is None:
                        u.flee = escape_estimate(M, u, units)[1]
                for v in units:
                    if v is not u and v.side == u.side and v.alive() and math.hypot(v.x - u.x, v.y - u.y) <= 300:
                        v.m -= 10 * TRAIN[v.q][2]
        if trace is not None and abs(t % 60) < 1e-9:
            trace.append((t / 60, [(u.id, int(u.x), int(u.y), u.n, round(u.m, 1), u.state) for u in units]))
        t += DT
        if not any(u.side == "red" and u.alive() for u in units) or not any(u.side == "blue" and u.alive() for u in units):
            break
    vill = set(map(tuple, M.d["objects"]["village"]["cells"]))
    blue_in = any(u.side == "blue" and u.alive() and M.cell_of(u.x, u.y) in vill for u in units)
    red_in = any(u.side == "red" and u.alive() and math.hypot(u.x - 1975, u.y - 1000) <= 250 for u in units)
    red_alive = any(u.side == "red" and u.alive() for u in units)
    blue_alive = any(u.side == "blue" and u.alive() for u in units)
    win = (blue_in and not red_in) or (not red_alive and blue_alive)
    return {"win": win, "t_end": round(t / 60, 1),
            "blue_loss": sum(150 - u.n for u in units if u.side == "blue") / 600,
            "blue_out": sum(1 for u in units if u.side == "blue" and not u.alive()),
            "red_out": sum(1 for u in units if u.side == "red" and not u.alive()),
            "red_surr": sum(1 for u in units if u.side == "red" and u.state == "surrendered"),
            "events": [(round(a / 60, 1), b) for a, b in events]}


def plans(stage):
    V = (1975, 1000)
    common = {
        "総員突撃": lambda by: [(0, b, 1975, y, 0) for b, y in (("B1", 1150), ("B2", 1000), ("B3", 850), ("B4", 1000))],
    }
    fx = 1450 if stage == 1 else 1350  # 稜線の塹壕から約375m
    common["正面火力戦"] = lambda by: [(0, "B1", fx, 1200, 0), (0, "B2", fx, 1000, 0), (0, "B3", fx, 800, 0), (0, "B4", 900, 1000, 0)]
    if stage == 1:
        common.update({
            "側面攻撃（1個中隊）": lambda by: [(0, "B2", 1450, 1000, 0), (0, "B1", 1975, 1250, -90), (0, "B3", 900, 800, 0), (0, "B4", 900, 1150, 0)],
            "側面攻撃（2個中隊）": lambda by: [(0, "B2", 1450, 1050, 0), (0, "B3", 1450, 850, 0), (0, "B1", 2075, 1300, -90), (0, "B4", 1875, 1300, -90)],
            "両翼包囲（鶴翼）": lambda by: [(0, "B2", 1450, 1000, 0), (0, "B1", 1975, 1250, -90), (0, "B3", 1975, 750, 90), (1200, "B4", 1450, 1200, 0)],
        })
    else:
        common.update({
            "側面攻撃（1個中隊）": lambda by: [(0, "B2", 1350, 1100, 0), (0, "B3", 900, 900, 0), (0, "B4", 900, 1100, 0), (0, "B1", 1725, 1400, -90)],
            "側面攻撃（2個中隊）": lambda by: [(0, "B2", 1350, 1100, 0), (0, "B3", 1350, 900, 0), (0, "B1", 1825, 1400, -90), (0, "B4", 1625, 1400, -90)],
            "両翼包囲（鶴翼）": lambda by: [(0, "B2", 1350, 1000, 0), (0, "B1", 1725, 1400, -90), (0, "B3", 1725, 600, 90), (1200, "B4", 1350, 1200, 0)],
        })
    return common


if __name__ == "__main__":
    N = int(sys.argv[1]) if len(sys.argv) > 1 else 20
    out = {}
    for stage in (1, 2):
        M = MR.Map(os.path.join(MAPS, f"scenario_a{stage}.json"))
        for name, p in plans(stage).items():
            rs = [run(M, p, s) for s in range(1, N + 1)]
            r = {"win": sum(x["win"] for x in rs) / N,
                 "blue_loss": round(float(np.mean([x["blue_loss"] for x in rs])), 3),
                 "blue_out": round(float(np.mean([x["blue_out"] for x in rs])), 2),
                 "red_out": round(float(np.mean([x["red_out"] for x in rs])), 2),
                 "red_surr": round(float(np.mean([x["red_surr"] for x in rs])), 2),
                 "t_end": float(np.median([x["t_end"] for x in rs])),
                 "sample": rs[0]["events"][:8]}
            out[f"stage{stage}:{name}"] = r
            print(stage, name, {k: v for k, v in r.items() if k != "sample"}, flush=True)
    json.dump(out, open(os.path.join(HERE, "battle_sketch_v3_results.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
