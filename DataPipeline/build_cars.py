#!/usr/bin/env python3
"""
Convierte los coches del Car Kit de Kenney (CC0, https://kenney.nl/assets/car-kit) al formato del juego.

Unity no importa GLB sin paquetes extra, así que cada modelo se convierte a un JSON propio que el juego
monta en tiempo de ejecución (igual que el resto del mundo):
  * coordenadas de Unity (mano izquierda: se invierte X y el sentido de los triángulos), en metros,
    con el origen en el suelo y el morro hacia +Z;
  * escalado por ejes hacia proporciones reales (los originales son de juguete);
  * la carrocería se separa por colores de la paleta (cada color, un submesh) y se marca cuál es la
    pintura, para que cada coche pueda llevar su color;
  * las ruedas van aparte (izquierda/derecha) con su posición, para que giren y se muevan con la suspensión.

Uso:  python build_cars.py            (descarga a cache/cars y escribe Resources/HernaniCars)
"""
import json
import os
import sys
import urllib.request

import numpy as np
from PIL import Image
import pygltflib

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache", "cars")
OUT = os.path.join(HERE, "..", "Assets", "HernaniRacing", "Resources", "HernaniCars")
SOURCES = [
    "https://cdn.cinevva.com/assets/packs/kenney/car-kit/{name}.glb?download=1",   # réplica CC0 del Car Kit
]
COLORMAP_URL = "https://cdn.cinevva.com/assets/packs/kenney/car-kit/Textures/colormap.png"

# id, modelo Kenney, nombre, categoría, escala (x ancho, y alto, z largo), seleccionable, físicas
CARS = [
    ("turismo", "sedan-sports", "Turismo GT", "Turismo", (1.30, 1.20, 1.60), True,
     dict(mass=1250, maxSpeed=50, engine=8600, brake=12000, grip=1.32, downforce=1.8)),
    ("berlina", "sedan", "Berlina familiar", "Turismo", (1.20, 1.15, 1.65), True,
     dict(mass=1350, maxSpeed=45, engine=7600, brake=11500, grip=1.22, downforce=1.4)),
    ("utilitario", "hatchback-sports", "Utilitario", "Utilitario", (1.35, 1.25, 1.35), True,
     dict(mass=1000, maxSpeed=42, engine=6400, brake=10000, grip=1.25, downforce=1.4)),
    ("furgoneta", "van", "Furgoneta", "Furgoneta", (1.25, 1.45, 1.75), True,
     dict(mass=1750, maxSpeed=37, engine=8000, brake=12500, grip=1.05, downforce=1.0)),
    ("f1", "race", "Fórmula 1", "F1", (1.50, 1.30, 1.85), True,
     dict(mass=760, maxSpeed=72, engine=15000, brake=17000, grip=2.1, downforce=6.0)),
    ("f1futuro", "race-future", "Fórmula 1 (futuro)", "F1", (1.60, 1.15, 1.80), True,
     dict(mass=760, maxSpeed=74, engine=15500, brake=17000, grip=2.15, downforce=6.5)),
    ("suv", "suv", "Todoterreno SUV", "Turismo", (1.25, 1.30, 1.60), True,
     dict(mass=1700, maxSpeed=43, engine=8400, brake=12000, grip=1.12, downforce=1.0)),
    # sólo tráfico
    ("taxi", "taxi", "Taxi", "Turismo", (1.20, 1.10, 1.60), False,
     dict(mass=1350, maxSpeed=42, engine=7200, brake=11500, grip=1.2, downforce=1.2)),
    ("policia", "police", "Ertzaintza", "Turismo", (1.20, 1.10, 1.45), False,
     dict(mass=1400, maxSpeed=48, engine=8200, brake=12000, grip=1.25, downforce=1.4)),
    ("ambulancia", "ambulance", "Ambulancia", "Furgoneta", (1.30, 1.25, 1.65), False,
     dict(mass=2600, maxSpeed=38, engine=9000, brake=14000, grip=1.0, downforce=1.0)),
    ("reparto", "delivery", "Camión de reparto", "Furgoneta", (1.35, 1.45, 1.80), False,
     dict(mass=3500, maxSpeed=33, engine=10000, brake=15000, grip=0.95, downforce=1.0)),
]


def fetch(url, path):
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return path
    os.makedirs(os.path.dirname(path), exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": "HernaniRacing/1.0"})
    with urllib.request.urlopen(req, timeout=60) as r, open(path, "wb") as f:
        f.write(r.read())
    return path


class Glb:
    def __init__(self, path):
        self.g = pygltflib.GLTF2().load(path)
        self.blob = self.g.binary_blob()

    def acc(self, i):
        g = self.g
        a = g.accessors[i]; bv = g.bufferViews[a.bufferView]
        comp = {5126: np.float32, 5123: np.uint16, 5125: np.uint32, 5121: np.uint8}[a.componentType]
        n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a.type]
        off = (bv.byteOffset or 0) + (a.byteOffset or 0)
        size = np.dtype(comp).itemsize
        if bv.byteStride and bv.byteStride != n * size:
            raw = np.frombuffer(self.blob, np.uint8, bv.byteStride * a.count, off).reshape(a.count, bv.byteStride)
            return raw[:, :n * size].copy().view(comp).reshape(a.count, n)
        return np.frombuffer(self.blob, comp, a.count * n, off).reshape(a.count, n).copy()

    def node_mesh(self, name):
        for nd in self.g.nodes:
            if nd.name == name and nd.mesh is not None:
                prim = self.g.meshes[nd.mesh].primitives[0]
                P = self.acc(prim.attributes.POSITION).astype(float)
                Nn = self.acc(prim.attributes.NORMAL).astype(float)
                UV = self.acc(prim.attributes.TEXCOORD_0).astype(float)
                I = self.acc(prim.indices).ravel().astype(int)
                t = np.array(nd.translation or [0, 0, 0], float)
                return P, Nn, UV, I, t
        raise KeyError(name)


def cell_colors(cmap):
    """La paleta son 8 x 4 celdas de 64 x 128 px; cada celda, un color (su mediana)."""
    cols = {}
    for cx in range(8):
        for cy in range(4):
            c = cmap[cy * 128:(cy + 1) * 128, cx * 64:(cx + 1) * 64, :3].reshape(-1, 3)
            cols[(cx, cy)] = np.median(c, axis=0) / 255.0
    return cols


def tri_cells(UV, I):
    tri = I.reshape(-1, 3)
    c = UV[tri].mean(1)
    cx = np.clip((c[:, 0] * 8).astype(int), 0, 7)
    cy = np.clip((c[:, 1] * 4).astype(int), 0, 3)
    return list(zip(cx.tolist(), cy.tolist()))


def to_unity(P, Nn, scale, offset):
    """glTF (mano derecha) -> Unity (mano izquierda): x -> -x. Escala por ejes."""
    s = np.array(scale, float)
    V = (P + offset) * s
    V[:, 0] *= -1
    N = Nn / s                      # normales con escala no uniforme
    N[:, 0] *= -1
    N /= np.maximum(np.linalg.norm(N, axis=1)[:, None], 1e-9)
    return V, N


def build_part(P, Nn, UV, I, scale, offset, cols, paint_cell=None):
    V, N = to_unity(P, Nn, scale, offset)
    cells = tri_cells(UV, I)
    tri = I.reshape(-1, 3)[:, ::-1]           # se invierte el sentido al reflejar X
    subs = {}
    for k, c in enumerate(cells):
        subs.setdefault(c, []).append(tri[k])
    out = []
    for c, ts in sorted(subs.items()):
        out.append({"color": [round(float(x), 3) for x in cols[c]], "paint": 1 if c == paint_cell else 0,
                    "t": np.array(ts).ravel().tolist(), "_cell": c})
    return {"v": np.round(V, 4).ravel().tolist(), "n": np.round(N, 3).ravel().tolist(), "sub": out}


def pick_paint(UV, I, P, cols):
    """Color de pintura: la celda saturada con más superficie en la carrocería."""
    cells = tri_cells(UV, I)
    tri = I.reshape(-1, 3)
    a = np.linalg.norm(np.cross(P[tri[:, 1]] - P[tri[:, 0]], P[tri[:, 2]] - P[tri[:, 0]]), axis=1) / 2
    area = {}
    for c, ar in zip(cells, a):
        area[c] = area.get(c, 0) + ar
    best, ba = None, 0
    for c, ar in area.items():
        col = cols[c]
        sat = col.max() - col.min()
        if sat > 0.2 and col.max() > 0.3 and ar > ba:
            best, ba = c, ar
    return best


def main():
    os.makedirs(OUT, exist_ok=True)
    cmap = np.array(Image.open(fetch(COLORMAP_URL, os.path.join(CACHE, "colormap.png"))).convert("RGBA"))
    cols = cell_colors(cmap)
    index = []
    for cid, model, name, cat, scale, selectable, phys in CARS:
        path = None
        for src in SOURCES:
            try:
                path = fetch(src.format(name=model), os.path.join(CACHE, model + ".glb"))
                break
            except Exception as ex:  # noqa: BLE001
                print("  descarga fallida", model, ex, file=sys.stderr)
        if path is None:
            continue
        glb = Glb(path)
        P, Nn, UV, I, t = glb.node_mesh("body")
        paint = pick_paint(UV, I, P, cols)
        body = build_part(P, Nn, UV, I, scale, t, cols, paint)
        # ruedas: radio con la media de las escalas vertical y longitudinal (siguen siendo redondas)
        sr = scale[1] * 0.55 + scale[2] * 0.45
        wheels, wl, wr = [], None, None
        for wn in ("wheel-front-left", "wheel-front-right", "wheel-back-left", "wheel-back-right"):
            Pw, Nw, UVw, Iw, tw = glb.node_mesh(wn)
            r0 = float(np.abs(Pw[:, 1]).max())
            center = np.array([-tw[0] * scale[0], r0 * sr, tw[2] * scale[2]])   # apoyada en el suelo
            wheels.append(np.round(center, 4).tolist())
            part = build_part(Pw, Nw, UVw, Iw, (scale[0], sr, sr), np.zeros(3), cols)
            if wn == "wheel-front-left":
                wl = part
            elif wn == "wheel-front-right":
                wr = part
        Vb = np.array(body["v"]).reshape(-1, 3)
        lo, hi = Vb.min(0), Vb.max(0)
        radius = float(np.abs(np.array(glb.node_mesh("wheel-front-left")[0])[:, 1]).max() * sr)
        for s in body["sub"]:
            s.pop("_cell", None)
        for p in (wl, wr):
            for s in p["sub"]:
                s.pop("_cell", None)
        car = {
            "id": cid, "name": name, "category": cat, "selectable": selectable, "source": "Kenney Car Kit: " + model,
            "size": np.round(hi - lo, 3).tolist(), "boundsMin": np.round(lo, 3).tolist(), "boundsMax": np.round(hi, 3).tolist(),
            "wheelRadius": round(radius, 3), "wheels": sum(wheels, []),
            "body": body, "wheelL": wl, "wheelR": wr,
            "paintColor": [round(float(x), 3) for x in cols[paint]] if paint else [0.8, 0.1, 0.1],
            **{k: float(v) for k, v in phys.items()},
        }
        with open(os.path.join(OUT, cid + ".json"), "w") as f:
            json.dump(car, f, separators=(",", ":"))
        tris = sum(len(s["t"]) for s in body["sub"]) // 3 + 4 * sum(len(s["t"]) for s in wl["sub"]) // 3
        print(f"{cid:11s} {name:22s} {np.round(hi - lo, 2)} m  {tris} triángulos  rueda r={radius:.2f}")
        index.append({"id": cid, "name": name, "category": cat, "selectable": selectable})
    with open(os.path.join(OUT, "cars.json"), "w") as f:
        json.dump({"cars": index, "credits": "Car Kit de Kenney (kenney.nl), licencia CC0 1.0"}, f, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
