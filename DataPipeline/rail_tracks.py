"""Anchos de las vías del tren.

b5m dibuja cada carril (vía) como una línea propia: en la vía doble salen dos líneas paralelas a ~3,8 m.
Por eso cada línea de tren es vía ÚNICA (ancho 4,5 m) salvo los tramos largos sin línea paralela,
que en realidad son vía doble dibujada con una sola línea (p. ej. Hernani -> Martutene): esos quedan a 8,5 m
y el juego pinta dos vías en ellos.

Uso suelto (corrige un roads.json ya generado):  python3 rail_tracks.py ruta/roads.json
"""
import json
import sys

import numpy as np
from scipy.spatial import cKDTree

SINGLE, DOUBLE = 4.5, 8.5


def _dens(p, step=2.0):
    p = np.asarray(p, float).reshape(-1, 3)[:, [0, 2]]
    out = [p[0]]
    for q0, q1 in zip(p[:-1], p[1:]):
        n = max(1, int(np.linalg.norm(q1 - q0) / step))
        out.extend(q0 + (q1 - q0) * k / n for k in range(1, n + 1))
    return np.array(out)


def fix(edges):
    """edges: lista de dicts de roads.json (modifica 'width' de los de tipo Tren). Devuelve (únicas, dobles)."""
    rail = [i for i, e in enumerate(edges) if e.get("type") == "Tren"]
    if not rail:
        return 0, 0
    pts = {i: _dens(edges[i]["p"]) for i in rail}
    single = double = 0
    for i in rail:
        others = [pts[j] for j in rail if j != i]
        cov = 0.0
        if others:
            d, _ = cKDTree(np.vstack(others)).query(pts[i])
            cov = float(np.mean((d > 2.0) & (d < 6.5)))
        if cov < 0.3 and edges[i]["length"] > 1000:
            edges[i]["width"] = DOUBLE; double += 1
        else:
            edges[i]["width"] = SINGLE; single += 1
    return single, double


if __name__ == "__main__":
    path = sys.argv[1]
    with open(path, encoding="utf8") as f:
        data = json.load(f)
    s, d = fix(data["edges"])
    with open(path, "w", encoding="utf8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    print(f"vías: {s} tramos de vía única, {d} de vía doble")
