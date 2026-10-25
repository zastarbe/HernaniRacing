"""Genera la tabla para que el servidor traslade los circuitos online guardados
con unos datos antiguos (ids de cruces viejos) a los datos nuevos.

uso: python3 make_circuit_remap.py <dir_datos_viejos> <dir_datos_nuevos> <salida.json>
(si salida.json ya existe se le añade la tabla; se pueden encadenar varias versiones viejas)
Cada cruce viejo se empareja con el cruce nuevo (de calles/carreteras) más cercano a menos de 4 m.
"""
import json, os, sys
import numpy as np
from scipy.spatial import cKDTree

ROAD = ("Calle", "Carretera")


def load(d):
    meta = json.load(open(os.path.join(d, "meta.json"), encoding="utf8"))
    roads = json.load(open(os.path.join(d, "roads.json"), encoding="utf8"))
    xy = np.array([[n["x"], n["z"]] for n in roads["nodes"]])
    deg = np.zeros(len(xy), int)
    for e in roads["edges"]:
        if e.get("type", "Calle") in ROAD:
            deg[e["a"]] += 1; deg[e["b"]] += 1
    return meta, xy, deg


def main(old, new, out):
    m0, xy0, d0 = load(old)
    m1, xy1, d1 = load(new)
    xy0 = xy0 + [m0["originE"] - m1["originE"], m0["originN"] - m1["originN"]]   # al origen nuevo
    idx1 = np.nonzero(d1 > 0)[0]
    tree = cKDTree(xy1[idx1])
    dist, k = tree.query(xy0)
    mp = np.where((dist < 4.0) & (d0 > 0), idx1[k], -1)
    junc = d0 >= 3
    ok = (mp[junc] >= 0).mean() if junc.any() else 0
    print(f"cruces viejos {junc.sum()}, emparejados {ok*100:.1f} %  (dist media {dist[junc & (mp>=0)].mean():.2f} m)")
    tables = []
    if os.path.exists(out):
        tables = [t for t in json.load(open(out)).get("tables", []) if t["from"] != m0["generated"]]
    tables.append({"from": m0["generated"], "to": m1["generated"], "map": [int(v) for v in mp]})
    json.dump({"tables": tables}, open(out, "w"), separators=(",", ":"))


if __name__ == "__main__":
    main(*sys.argv[1:4])
