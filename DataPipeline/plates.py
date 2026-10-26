"""Placas con el nombre de las casas: en qué pared van y a qué altura.

Lo usa extras.building_names al generar los datos. Suelto, recalcula las placas de un buildings.json ya generado
(sin descargar nada) con las vías de roads.json y el relieve de heightmap.bytes:

    python3 plates.py ../Assets/HernaniRacing/Resources/HernaniData
"""
import gzip
import json
import os
import sys

import numpy as np

def plate_facade(v, rtree, rp, Hs, top):
    """Pared para la placa del nombre: la que mira a la calle o camino más cercano (y es larga). Devuelve
    [x, z, y, nx, nz, ancho] (punto 7 cm por fuera de la pared, a la altura de una placa, y normal EXTERIOR) o None.
    v: anillo exterior (n x 2). rtree/rp: cKDTree y puntos de las vías. Hs(x, z) -> cota del terreno.
    Ojo: los anillos de b5m van en sentido horario visto desde arriba (x este, z norte): la normal exterior de
    un lado d = b - a es (-dz, dx). Se calcula con el signo del área para valer en los dos sentidos (hasta la v16
    se usaba la contraria: placas por dentro de la pared del fondo)."""
    n = len(v)
    if n < 3:
        return None
    x, z = v[:, 0], v[:, 1]
    area2 = float(np.sum(x * np.roll(z, -1) - np.roll(x, -1) * z))      # > 0: antihorario
    sgn = 1.0 if area2 > 0 else -1.0
    best = None
    for k in range(n):
        a_, b_ = v[k], v[(k + 1) % n]
        d = b_ - a_
        L = float(np.hypot(*d))
        if L < 2.6:
            continue
        nrm = sgn * np.array([d[1], -d[0]]) / L                          # normal exterior
        mid = (a_ + b_) / 2
        dist, j = rtree.query(mid, distance_upper_bound=120)
        if not np.isfinite(dist):
            dist, toward = 120.0, 0.5
        else:
            to = rp[j] - mid
            toward = float(np.dot(to, nrm) / max(np.hypot(*to), 1e-6))
        if toward < 0.2:
            continue
        score = dist - 0.4 * min(L, 12.0) - 6.0 * toward
        if best is None or score < best[0]:
            best = (score, mid, nrm, L)
    if best is None:
        return None
    _, mid, nrm, L = best
    gy = float(np.asarray(Hs(mid[0] + nrm[0] * 0.8, mid[1] + nrm[1] * 0.8)).reshape(-1)[0])
    y = gy + min(2.7, max(1.6, (top - gy) * 0.55))
    p = mid + nrm * 0.07
    return [round(float(p[0]), 2), round(float(p[1]), 2), round(y, 2), round(float(nrm[0]), 3),
            round(float(nrm[1]), 3), round(min(L - 0.6, 9.0), 2)]


def load_heightmap(data_dir):
    """Lee heightmap.bytes ('HMZ1') -> función Hs(x, z) con interpolación bilineal (m)."""
    with open(os.path.join(data_dir, "meta.json"), encoding="utf8") as f:
        meta = json.load(f)
    raw = open(os.path.join(data_dir, "heightmap.bytes"), "rb").read()
    assert raw[:4] == b"HMZ1"
    gx, gz = np.frombuffer(raw[4:12], "<i4")
    buf = gzip.decompress(raw[12:])
    n = gx * gz
    D = (np.frombuffer(buf[:n], np.uint8).astype(np.uint16) | (np.frombuffer(buf[n:2 * n], np.uint8).astype(np.uint16) << 8)).reshape(gz, gx)
    A = D.copy()
    A[:, 0] = np.cumsum(D[:, 0], dtype=np.uint16)          # aritmética módulo 2^16, como al escribir
    A = np.cumsum(A, axis=1, dtype=np.uint16)
    H = meta["heightMin"] + A.astype(np.float32) / 65535.0 * meta["heightRange"]
    cell = meta["cell"]

    def Hs(x, z):
        fx = np.clip(np.asarray(x, float) / cell, 0, gx - 1.001)
        fz = np.clip(np.asarray(z, float) / cell, 0, gz - 1.001)
        i, j = np.floor(fx).astype(int), np.floor(fz).astype(int)
        tx, tz = fx - i, fz - j
        return ((H[j, i] * (1 - tx) + H[j, i + 1] * tx) * (1 - tz) + (H[j + 1, i] * (1 - tx) + H[j + 1, i + 1] * tx) * tz)
    return Hs


def fix(data_dir):
    from scipy.spatial import cKDTree
    with open(os.path.join(data_dir, "roads.json"), encoding="utf8") as f:
        roads = json.load(f)
    # como en el pipeline: calles, carreteras y caminos (no bidegorris ni vías del tren)
    pts = [np.asarray(e["p"], float).reshape(-1, 3)[:, [0, 2]] for e in roads["edges"]
           if e.get("type") in ("Calle", "Carretera", "Camino")]
    rp = np.concatenate(pts)
    rtree = cKDTree(rp)
    Hs = load_heightmap(data_dir)
    path = os.path.join(data_dir, "buildings.json")
    with open(path, encoding="utf8") as f:
        data = json.load(f)
    kept = dropped = moved = 0
    for b in data["buildings"]:
        if not b.get("pn"):
            continue
        v = np.asarray(b["v"], float).reshape(-1, 2)[:b["r"][0]]
        res = plate_facade(v, rtree, rp, Hs, float(b["h"]))
        if res is None:
            b.pop("pn", None); b.pop("ps", None); dropped += 1
            continue
        old = b.get("ps")
        if old is None or abs(old[0] - res[0]) + abs(old[1] - res[1]) > 0.5:
            moved += 1
        b["ps"] = res
        kept += 1
    with open(path, "w", encoding="utf8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    print(f"placas: {kept} (cambiadas de pared {moved}), sin pared válida {dropped}")


if __name__ == "__main__":
    fix(sys.argv[1] if len(sys.argv) > 1 else "../Assets/HernaniRacing/Resources/HernaniData")
