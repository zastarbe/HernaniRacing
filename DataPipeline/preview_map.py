#!/usr/bin/env python3
"""Dibuja un mapa de control con los datos generados (calles, edificios, ríos, cruces y circuitos predefinidos).

Uso:  python preview_map.py  [--data ../Assets/HernaniRacing/Resources/HernaniData] [--out ../Docs/mapa_circuitos.png]
"""
import argparse
import heapq
import json
import os

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np


def load(d, n):
    with open(os.path.join(d, n), encoding="utf-8") as f:
        return json.load(f)


def loop_path(nodes, edges, adj, ids, penalty=4.0):
    used = set()
    out = []
    for i in range(4):
        a, b = ids[i], ids[(i + 1) % 4]
        dist = {a: 0.0}
        prev = {}
        pq = [(0.0, a)]
        while pq:
            d, u = heapq.heappop(pq)
            if u == b:
                break
            if d > dist.get(u, 1e18):
                continue
            for ei in adj[u]:
                e = edges[ei]
                v = e["b"] if e["a"] == u else e["a"]
                w = e["length"] * (penalty if ei in used else 1.0)
                if d + w < dist.get(v, 1e18):
                    dist[v] = d + w
                    prev[v] = (u, ei)
                    heapq.heappush(pq, (d + w, v))
        seq = []
        v = b
        while v != a:
            u, ei = prev[v]
            seq.append((ei, u))
            v = u
        for ei, u in reversed(seq):
            e = edges[ei]
            p = np.array(e["p"]).reshape(-1, 3)[:, [0, 2]]
            out.append(p if e["a"] == u else p[::-1])
            used.add(ei)
            for sib in adj[u]:
                o = edges[sib]
                if (o["b"] if o["a"] == u else o["a"]) == (e["b"] if e["a"] == u else e["a"]):
                    used.add(sib)
    return np.vstack(out)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=os.path.join(here, "..", "Assets", "HernaniRacing", "Resources", "HernaniData"))
    ap.add_argument("--out", default=os.path.join(here, "..", "Docs", "mapa_circuitos.png"))
    ap.add_argument("--view", nargs=4, type=float, metavar=("X0", "Z0", "X1", "Z1"), help="ventana (m locales)")
    ap.add_argument("--only", type=int, nargs="*", help="índices de presets a dibujar")
    a = ap.parse_args()

    meta, roads = load(a.data, "meta.json"), load(a.data, "roads.json")
    bld, scen = load(a.data, "buildings.json"), load(a.data, "scenery.json")
    nodes, edges = roads["nodes"], roads["edges"]
    adj = [[] for _ in nodes]
    ROAD = ("Calle", "Carretera")
    for i, e in enumerate(edges):
        if e["a"] != e["b"] and e["type"] in ROAD:   # los circuitos sólo van por calles y carreteras
            adj[e["a"]].append(i)
            adj[e["b"]].append(i)
    xy = np.array([[n["x"], n["z"]] for n in nodes])

    sx = meta.get("sizeX", meta["size"]); sz = meta.get("sizeZ", meta["size"])
    vx0, vz0, vx1, vz1 = a.view if a.view else (0, 0, sx, sz)
    fw = 16.0
    fig, ax = plt.subplots(figsize=(fw, fw * (vz1 - vz0) / (vx1 - vx0)), dpi=100)
    ax.set_facecolor("#e9efe4")
    for r in scen.get("rivers", []):
        p = np.array(r["p"]).reshape(-1, 3) if "p" in r else None
        if p is not None and len(p) > 1:
            ax.plot(p[:, 0], p[:, 2], color="#6fa8dc", lw=max(1.0, r.get("width", 4) / 4), zorder=1)
    for b in bld.get("buildings", bld if isinstance(bld, list) else []):
        v = np.array(b["v"]).reshape(-1, 2)
        if len(v) > 2:
            ax.fill(v[:, 0], v[:, 1], color="#c9b8a6", lw=0, zorder=2)
    # v16: caminos (marrón), bidegorris (rojo), tren (gris oscuro discontinuo)
    sty = {"Calle": dict(color="#ffffff", lw=1.0, zorder=3), "Carretera": dict(color="#f6c86b", lw=1.8, zorder=3),
           "Camino": dict(color="#9a6a3a", lw=0.6, zorder=2.5), "Bidegorri": dict(color="#e0303a", lw=1.3, zorder=3.2),
           "Tren": dict(color="#303030", lw=1.8, zorder=3.4, linestyle=(0, (4, 2)))}
    for e in edges:
        p = np.array(e["p"]).reshape(-1, 3)
        ax.plot(p[:, 0], p[:, 2], **sty.get(e["type"], sty["Calle"]))
    for l in scen.get("loose", []):
        p = np.array(l["p"]).reshape(-1, 3)
        ax.plot(p[:, 0], p[:, 2], **sty.get(l["t"], sty["Camino"]))
    for pk in scen.get("peaks", []):
        ax.scatter([pk["x"]], [pk["z"]], marker="^", s=22, color="#5b3b1a", zorder=7)
        ax.annotate(pk["name"], (pk["x"], pk["z"]), xytext=(3, -8), textcoords="offset points", fontsize=6, color="#5b3b1a", zorder=7)
    for st in scen.get("stations", []):
        ax.scatter([st["x"]], [st["z"]], marker="s", s=50, color="#8e2bd0", edgecolor="k", zorder=8)
        ax.annotate("Estación " + st["name"], (st["x"], st["z"]), xytext=(5, 3), textcoords="offset points", fontsize=8,
                    weight="bold", color="#5a1a8a", zorder=8)
    from matplotlib.lines import Line2D
    extra_legend = [Line2D([0], [0], **{k: v for k, v in sty[t].items() if k != "zorder"}, label=n) for t, n in
                    (("Carretera", "Carretera"), ("Calle", "Calle"), ("Camino", "Camino de monte"), ("Bidegorri", "Bidegorri"), ("Tren", "Vía del tren"))]
    colors = ["#d62728", "#1f77b4", "#2ca02c", "#9467bd", "#ff7f0e", "#17becf", "#e377c2", "#8c564b"]
    for k, pr in enumerate(meta.get("presets", [])):
        if a.only is not None and k not in a.only:
            continue
        pts = np.array(pr["points"]).reshape(-1, 2)
        ids = [int(np.argmin(((xy - q) ** 2).sum(1))) for q in pts]
        try:
            path = loop_path(nodes, edges, adj, ids)
        except KeyError:
            continue
        c = colors[k % len(colors)]
        ax.plot(path[:, 0], path[:, 1], color=c, lw=3.2, alpha=0.75, zorder=5,
                label=f"{pr['name']} ({pr['length'] / 1000:.2f} km)")
        ax.scatter(pts[:, 0], pts[:, 1], s=60, color=c, edgecolor="k", zorder=6)
        for j, q in enumerate(pts):
            ax.annotate(str(j + 1), q, xytext=(4, 4), textcoords="offset points", fontsize=8, zorder=7)
    big = (vx1 - vx0) > 5000
    for z in [z for z in scen.get("zones", []) if (z.get("primary") if big else (z.get("district") or z.get("primary")))]:
        ax.text(z["x"], z["z"], z["name"], fontsize=11, weight="bold", ha="center", color="#333", zorder=8,
                bbox=dict(boxstyle="round,pad=0.2", fc="white", alpha=0.6, lw=0))
    ax.set_xlim(vx0, vx1)
    ax.set_ylim(vz0, vz1)
    ax.set_aspect("equal")
    ax.set_title("Hernani Racing - red viaria (b5m, SITNA, OSM), caminos, bidegorris, tren y circuitos predefinidos")
    h, _ = ax.get_legend_handles_labels()
    ax.legend(handles=h + extra_legend, loc="upper right", fontsize=9, facecolor="#dddddd")
    ax.set_xlabel(f"x (m) desde E={meta['originE']:.0f}")
    ax.set_ylabel(f"z (m) desde N={meta['originN']:.0f}")
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    fig.savefig(a.out, bbox_inches="tight")
    print("Mapa guardado en", os.path.abspath(a.out))


if __name__ == "__main__":
    main()
