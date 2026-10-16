import json, sys
sys.path.insert(0, '.')
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib import font_manager
import numpy as np
import map_reference as R

for f in font_manager.findSystemFonts():
    if 'NotoSansCJK' in f and ('Regular' in f or 'Medium' in f):
        font_manager.fontManager.addfont(f)
plt.rcParams['font.family'] = ['Noto Sans CJK JP', 'Noto Sans CJK TC', 'sans-serif']

MAP = sys.argv[1] if len(sys.argv) > 1 else 'scenario_a1.json'
RES = sys.argv[2] if len(sys.argv) > 2 else 'map_reference_results.json'
OUT = sys.argv[3] if len(sys.argv) > 3 else 'scenario_a_preview.png'
m = R.Map(MAP)
res = json.load(open(RES, encoding='utf-8'))
col = {'.': (0.86, 0.88, 0.80), 'F': (0.42, 0.58, 0.40), 'V': (0.62, 0.55, 0.48), 'W': (0.45, 0.62, 0.78),
       'B': (0.35, 0.33, 0.30), 'S': (0.62, 0.75, 0.85), 'T': (0.55, 0.45, 0.30)}
img = np.zeros((m.h, m.w, 3))
for j in range(m.h):
    for i in range(m.w):
        img[j, i] = col[m.t[j][i]]
fig, ax = plt.subplots(figsize=(13, 9.2), dpi=110)
ax.imshow(img, origin='lower', extent=[0, 3000, 0, 2000])
E = np.array(m.e, dtype=float)
xs = (np.arange(m.w) + 0.5) * 50; ys = (np.arange(m.h) + 0.5) * 50
cs = ax.contour(xs, ys, E, levels=[5, 10, 15, 20, 25], colors='k', linewidths=0.5, alpha=0.5)
ax.clabel(cs, fmt='%dm', fontsize=7)
en = [(u['x'], u['y']) for u in m.d['deploy']['red']]
exp = np.array(R.exposure_map(m, en))
ax.contourf(xs, ys, exp, levels=[0.5, 1.5], colors='none', hatches=['////'], alpha=0)
ax.contour(xs, ys, exp, levels=[0.5], colors=[(0.75, 0.25, 0.1)], linewidths=1.0)
for u in m.d['deploy']['blue']:
    ax.add_patch(plt.Rectangle((u['x'] - 25, u['y'] - 100), 50, 200, color=(0.15, 0.4, 0.7)))
    ax.text(u['x'] + 40, u['y'], u['id'], color=(0.1, 0.3, 0.6), fontsize=9, va='center', weight='bold')
for u in m.d['deploy']['red']:
    ax.add_patch(plt.Rectangle((u['x'] - 25, u['y'] - 100), 50, 200, color=(0.8, 0.35, 0.1)))
    ax.text(u['x'] + 40, u['y'] - 130, u['id'], color=(0.7, 0.25, 0.05), fontsize=9, weight='bold')
pc = {'cautious': (0.1, 0.45, 0.2), 'calm': (0.2, 0.3, 0.8), 'reckless': (0.75, 0.1, 0.5)}
pj = {'cautious': '慎重', 'calm': '冷静', 'reckless': '猪突'}
for p in ['cautious', 'calm', 'reckless']:
    st = res['paths']['P_south_approach_' + p]
    pts = np.array([m.center(i, j) for i, j in st['path']])
    ax.plot(pts[:, 0], pts[:, 1], color=pc[p], lw=2, label=f"南からの接近：{pj[p]}（露出 {st['exposed_m']:.0f}m）")
st = res['paths']['P_north_flank_calm']
pts = np.array([m.center(i, j) for i, j in st['path']])
ax.plot(pts[:, 0], pts[:, 1], color='k', lw=1.5, ls='--', label=f"北の側面への経路（露出 {st['exposed_m']:.0f}m）")
for k, style in [('L1_west_field_to_village_ridge', ':'), ('L10_village_looks_north', '-.'), ('L8_R1_trench_to_slope_250m', '--')]:
    v = res['los'][k]
    c = (0.1, 0.6, 0.1) if v['visible'] else (0.8, 0.0, 0.0)
    ax.plot([v['from'][0], v['to'][0]], [v['from'][1], v['to'][1]], color=c, lw=1.2, ls=style)
ax.text(1300, 940, '視線：稜線で遮られる', color=(0.8, 0, 0), fontsize=8)
ax.text(2040, 1180, '視線：村から北は見える', color=(0.1, 0.5, 0.1), fontsize=8)
ax.text(1960, 1110, 'リーデル村', fontsize=9, weight='bold')
ax.text(600, 1700, '北の森（迂回路）', fontsize=10, color='w', weight='bold')
ax.text(2065, 290, '橋', fontsize=9); ax.text(980, 290, '浅瀬', fontsize=9)
r1 = next(u for u in m.d['deploy']['red'] if u['id'] == 'R1')
v8 = res['los']['L8_R1_trench_to_slope_250m']
ax.text(r1['x'] - 260, r1['y'] + 30, '塹壕から斜面：' + ('見える' if v8['visible'] else '死角'), color=(0.1, 0.5, 0.1) if v8['visible'] else (0.8, 0, 0), fontsize=8)
ax.set_xlim(0, 3000); ax.set_ylim(0, 2000)
ax.set_xticks(range(0, 3001, 500)); ax.set_yticks(range(0, 2001, 500))
ax.set_xlabel('東西（m）'); ax.set_ylabel('南北（m）')
ax.set_title(m.d['title'] + '　敵の射界（赤線の内側）と経路の検証', fontsize=12)
ax.legend(loc='lower right', fontsize=8, framealpha=0.9)
plt.tight_layout()
plt.savefig(OUT)
print('ok')
