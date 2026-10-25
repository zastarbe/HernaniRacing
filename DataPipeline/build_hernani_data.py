#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Hernani Racing - pipeline de datos geográficos reales
=====================================================

Descarga y procesa datos oficiales de:
  * b5m (IDE de Gipuzkoa, Diputación Foral de Gipuzkoa) - licencia CC BY 4.0
      - Viales (ejes de calles y carreteras)        GFA_DSET_PATHW
      - Edificios (huella + altura media LiDAR)      GFA_DSET_BU
      - Toponimia (barrios, polígonos, edificios)    GFA_DSET_TOPON
      - Hidrografía (Urumea y arroyos)               GFA_DSET_HYD
  * geoEuskadi (Gobierno Vasco) - MDT LiDAR 1 m (servicio WCS_KARTOGRAFIA)
      Fallback automático: MDT05 del IGN/IDEE (WCS) si geoEuskadi no responde.
  * SITNA / IDENA (Gobierno de Navarra) - CC BY 4.0: la parte del rectángulo que cae en Navarra (Arano, Goizueta):
      viales, edificios con altura catastral, hidrografía y toponimia (ver navarra.py).
      También puedes pasar un GeoTIFF descargado a mano con --dtm.

Genera en  <Unity>/Assets/HernaniRacing/Resources/HernaniData/ :
  meta.json, roads.json, buildings.json, scenery.json, heightmap.bytes

Uso:
  pip install -r requirements.txt
  python build_hernani_data.py                       # todo automático
  python build_hernani_data.py --dtm mdt_lidar.tif   # MDT local de geoEuskadi
  python build_hernani_data.py --bounds 580450 4779806 590150 4793100 --cell 2

Sistema de referencia: ETRS89 / UTM 30N (EPSG:25830). En Unity:
  x = E - origenE ;  z = N - origenN ;  y = altitud ortométrica (m)
"""
import argparse
import datetime as dt
import io
import json
import math
import os
import re
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from collections import defaultdict

import numpy as np

try:
    import geopandas as gpd
    import rasterio
    from rasterio.io import MemoryFile
    from rasterio.warp import reproject, Resampling
    from rasterio.transform import from_origin
    import shapely
    from shapely.geometry import box, LineString, Point, Polygon
    from shapely.ops import substring
    from scipy.ndimage import gaussian_filter, map_coordinates, binary_dilation
    from scipy.spatial import cKDTree
    import networkx as nx
    import navarra
except ImportError as e:  # pragma: no cover
    sys.exit(f"Falta una dependencia ({e}). Ejecuta: pip install -r requirements.txt")

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.normpath(os.path.join(HERE, "..", "Assets", "HernaniRacing", "Resources", "HernaniData"))
CACHE = os.path.join(HERE, "cache")
# descargas grandes (GPKG de b5m y MDT): se puede llevar a otra carpeta con HERNANI_CACHE
BIG_CACHE = os.environ.get("HERNANI_CACHE", CACHE)
# Área por defecto (desde la v15): TODO el término municipal de Hernani (39,8 km²; su límite va de E 580682 a 589934 y
# de N 4780542 a 4792770) y todo lo que queda dentro del rectángulo: Urnieta, Astigarraga, parte de Martutene y de
# los pueblos vecinos del sur (Andoain, Elduain, Arano, Goizueta...). 10 x 13 teselas de 1024 m.
# El borde sur está 6 teselas (6144 m) más al sur que hasta la v14 (4785950), para que las teselas de antes
# (y la ortofoto) sigan siendo las mismas. UTM30 ETRS89: W S E N
DEFAULT_BOUNDS = [580450, 4779806, 590150, 4793100]
DEFAULT_CELL = 2.0          # m entre muestras del heightmap
TILE_CELLS = 512            # teselas de terreno de Unity: 512 x 512 celdas (513² muestras) -> 1024 m con celdas de 2 m

B5M_DATASETS = {
    "pathw": "https://b5m.gipuzkoa.eus/datasets/GFA_DSET_PATHW.gpkg",
    "bu": "https://b5m.gipuzkoa.eus/datasets/GFA_DSET_BU.gpkg",
    "topon": "https://b5m.gipuzkoa.eus/datasets/GFA_DSET_TOPON.gpkg",
    "hyd": "https://b5m.gipuzkoa.eus/datasets/GFA_DSET_HYD.gpkg",
}
GEOEUSKADI_WCS = "https://www.geo.euskadi.eus/WCS_KARTOGRAFIA"
IDEE_WCS = "https://servicios.idee.es/wcs-inspire/mdt"

# Zonas pedidas (se marcan como principales) + barrios adyacentes
PRIMARY_ZONES = ["Hernani, herrigunea", "Karabel", "Florida", "Elizatxo", "Portu", "Antziola",
                 "Urnieta, herrigunea", "Astigarraga, herrigunea", "Ereñotzu, herrigunea", "Fagollaga, herrigunea"]
# Zonas que b5m no tiene como barrio (sólo como edificio): Ugaldetxo de Hernani (área recreativa, GI-3410 km 8,7)
EXTRA_ZONES = [{"name": "Ugaldetxo", "full": "Ugaldetxo (Ereñotzu, Hernani)", "type": "barrio", "E": 587269, "N": 4786280,
                "primary": True}]
ZONE_DISPLAY = {"Hernani, herrigunea": "Centro (Erdigunea)"}

ROAD_WIDTH = {"Calle": 6.5, "Carretera": 8.0, "Camino": 3.0, "Bidegorri": 2.6, "Tren": 8.5}
# v16: caminos de monte, bidegorris y ferrocarril (extras.py). Las calles y carreteras son los "ROAD_TYPES".
ROAD_TYPES = ("Calle", "Carretera")
EXTRAS = {"osm": None, "loose": []}
# Túneles: la bóveda (exterior) llega a +5.8 m sobre la calzada; el terreno se mantiene >= +6.2 m encima.
TUNNEL_COVER = 6.2
TUNNEL_HOLE_HALF = 2.4    # agujeros en el terreno hasta media anchura + 2.4 m (el muro exterior está a +3.5 m)
MOTORWAY_RE = re.compile(r"^(A-15|AP-1|AP-8|N-1|GI-20)")

# Presets: 4 zonas (UTM). El pipeline elige, cerca de cada zona, la combinación de cruces
# que forma el mejor bucle (sin repetir calles, longitud cercana a la objetivo).
PRESETS = [
    {"name": "GP Urumea: Centro - Karabel - Florida - Latsunbe", "target": 3200,
     "points": [[583095, 4790846], [583580, 4790870], [583741, 4791387], [582920, 4791350]]},
    {"name": "Subida a Elizatxo: Portu - Elizatxo - Centro - Karabel", "target": 2800,
     "points": [[583290, 4790535], [582850, 4790250], [583095, 4790846], [583560, 4790860]]},
    {"name": "Vuelta Antziola: Antziola - Florida - Latsunbe - Galarreta", "target": 4500,
     "points": [[583100, 4792000], [583700, 4791650], [582900, 4791300], [582300, 4792000]]},
    {"name": "Sprint Casco: Centro - Portu - Karabel - Florida", "target": 2600,
     "points": [[583095, 4790846], [583290, 4790535], [583560, 4790860], [583600, 4791300]]},
    {"name": "Rally Urumea: Hernani - Fagollaga - Ereñotzu - Ugaldetxo (vuelta por el monte)", "target": 12000,
     "points": [[583290, 4790535], [585114, 4788939], [586044, 4788273], [587150, 4786350]]},
    {"name": "Hernani - Urnieta: Centro - Zikuñaga - Urnieta - Osinaga", "target": 7000,
     "points": [[583095, 4790846], [583744, 4790482], [581855, 4788723], [584002, 4788808]]},
    {"name": "Hernani - Astigarraga: Centro - Akarregi - Astigarraga - Florida", "target": 6500,
     "points": [[583095, 4790846], [584080, 4790890], [585397, 4792621], [583741, 4791387]]},
]


# --------------------------------------------------------------------------- utils
def log(*a):
    print("[hernani]", *a, flush=True)


def http_get(url, timeout=120):
    req = urllib.request.Request(url, headers={"User-Agent": "HernaniRacing-DataPipeline/1.0"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def cached_download(key, url):
    os.makedirs(BIG_CACHE, exist_ok=True)
    path = os.path.join(BIG_CACHE, os.path.basename(urllib.parse.urlparse(url).path))
    if os.path.exists(path) and os.path.getsize(path) > 1000:
        return path
    log(f"Descargando {key}: {url}")
    data = http_get(url, timeout=900)
    with open(path, "wb") as f:
        f.write(data)
    log(f"  -> {len(data)/1e6:.1f} MB")
    return path


def rnd(v, n=2):
    return float(round(float(v), n))


# --------------------------------------------------------------------------- DTM
def wcs_try_geoeuskadi(bbox):
    """Intenta obtener el MDT LiDAR 1 m de geoEuskadi vía WCS 2.0.1."""
    cap_url = f"{GEOEUSKADI_WCS}?service=WCS&request=GetCapabilities&version=2.0.1"
    log("Consultando WCS de geoEuskadi...")
    xml = http_get(cap_url, timeout=40)
    ids = re.findall(rb"CoverageId>([^<]+)<", xml)
    ids = [i.decode() for i in ids]
    log(f"  coberturas: {ids[:20]}")

    def score(cid):
        c = cid.lower()
        s = 0
        if "mdt" in c or "dtm" in c:
            s += 100
        if "mds" in c or "dsm" in c or "pend" in c or "orient" in c:
            s -= 1000
        yrs = re.findall(r"(20\d\d)", c)
        if yrs:
            s += int(yrs[-1]) - 2000
        if "1m" in c or "_1" in c:
            s += 5
        return s

    ids.sort(key=score, reverse=True)
    if not ids or score(ids[0]) < 50:
        raise RuntimeError("No se encontró cobertura MDT en geoEuskadi")
    cid = ids[0]
    x0, y0, x1, y1 = bbox
    url = (f"{GEOEUSKADI_WCS}?service=WCS&version=2.0.1&request=GetCoverage&CoverageId={cid}"
           f"&subset=x({x0},{x1})&subset=y({y0},{y1})&format=image/tiff")
    log(f"  GetCoverage {cid}")
    data = http_get(url, timeout=600)
    if not data[:4] in (b"II*\x00", b"MM\x00*"):
        raise RuntimeError("Respuesta WCS no es GeoTIFF: " + data[:200].decode("utf8", "ignore"))
    return data, f"geoEuskadi WCS_KARTOGRAFIA ({cid})"


def wcs_idee(bbox):
    x0, y0, x1, y1 = bbox
    url = (f"{IDEE_WCS}?service=WCS&version=2.0.1&request=GetCoverage&CoverageId=Elevacion25830_5"
           f"&subset=x({x0},{x1})&subset=y({y0},{y1})&format=image/tiff")
    log("Descargando MDT05 IGN/IDEE (fallback)...")
    data = http_get(url, timeout=600)
    return data, "IGN/IDEE MDT05 (WCS Elevacion25830_5) - fallback"


def load_dtm(args, bbox, gx, gz, cell):
    """Devuelve (heights[gz, gx] float32, fila 0 = sur, fuente)."""
    pad = 60
    pb = (bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad)
    src_name = None
    if args.dtm:
        with open(args.dtm, "rb") as f:
            data = f.read()
        src_name = f"Fichero local: {os.path.basename(args.dtm)}"
    else:
        cache_tif = os.path.join(BIG_CACHE, f"dtm_{int(pb[0])}_{int(pb[1])}_{int(pb[2])}_{int(pb[3])}.tif")
        cache_src = cache_tif + ".source.txt"
        if os.path.exists(cache_tif) and not args.refresh_dtm:
            data = open(cache_tif, "rb").read()
            src_name = open(cache_src).read() if os.path.exists(cache_src) else "cache"
        else:
            data = None
            if not args.no_geoeuskadi:
                try:
                    data, src_name = wcs_try_geoeuskadi(pb)
                except Exception as e:
                    log(f"  geoEuskadi no disponible ({e}); uso fallback IDEE.")
            if data is None:
                data, src_name = wcs_idee(pb)
            os.makedirs(BIG_CACHE, exist_ok=True)
            open(cache_tif, "wb").write(data)
            open(cache_src, "w").write(src_name)
    log(f"MDT: {src_name}")

    with MemoryFile(data) as mf, mf.open() as ds:
        src = ds.read(1).astype(np.float32)
        nod = ds.nodata
        if nod is not None:
            src[src == nod] = np.nan
        src[src < -100] = np.nan
        if np.isnan(src).any():
            src = np.where(np.isnan(src), np.nanmedian(src), src)
        # suavizado previo si la fuente es entera/gruesa (MDT05 int16)
        if ds.res[0] >= 2.0:
            src = gaussian_filter(src, 0.8)
        # Rejilla destino: centros de muestra en bbox[0] + i*cell (norte arriba para rasterio)
        dst = np.zeros((gz, gx), np.float32)
        dst_transform = from_origin(bbox[0] - cell / 2, bbox[3] + cell / 2, cell, cell)
        reproject(src, dst, src_transform=ds.transform, src_crs=ds.crs or "EPSG:25830",
                  dst_transform=dst_transform, dst_crs="EPSG:25830",
                  resampling=Resampling.cubic_spline if ds.res[0] >= 2.0 else Resampling.bilinear)
    heights = np.flipud(dst)  # fila 0 = sur (z = 0 en Unity)
    if "MDT05" in src_name:
        heights = gaussian_filter(heights, 1.2 * 1.465 / cell)   # mismo suavizado en metros que con celdas de 1.465 m
    return np.ascontiguousarray(heights, np.float32), src_name


class HeightSampler:
    def __init__(self, h, cell):
        self.h = h
        self.k = 1.0 / cell

    def __call__(self, x, z):
        x = np.atleast_1d(np.asarray(x, np.float64)) * self.k
        z = np.atleast_1d(np.asarray(z, np.float64)) * self.k
        return map_coordinates(self.h, [z, x], order=1, mode="nearest")


# --------------------------------------------------------------------------- roads
def densify(coords, step):
    out = [coords[0]]
    for a, b in zip(coords[:-1], coords[1:]):
        d = math.dist(a, b)
        n = max(1, int(math.ceil(d / step)))
        for i in range(1, n + 1):
            t = i / n
            out.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
    return out


def build_roads(pathw_path, bbox, origin, size, sampler, args, nav=None):
    x0, y0, x1, y1 = bbox
    margin = 25
    inner = box(x0 + margin, y0 + margin, x1 - margin, y1 - margin)
    v = gpd.read_file(pathw_path, bbox=bbox)
    v = v[v.TYPE_ES.isin(["Calle", "Carretera"])].copy()
    v["NAME"] = v["NAME"].fillna("")
    v = v[~v.NAME.str.match(r"^E\d+$")]           # rutas europeas duplicadas
    if args.no_motorways:
        v = v[~v.NAME.str.match(MOTORWAY_RE)]
    if nav is not None:
        v = navarra.merge_roads(v, nav, log)          # la parte de Navarra (IDENA), empalmada en la muga
    v["geometry"] = v.geometry.intersection(inner)
    v = v[~v.geometry.is_empty].explode(index_parts=False)
    v = v[v.geom_type == "LineString"]
    v["BRIDGE_TUNNEL"] = v["BRIDGE_TUNNEL"].fillna("0").astype(str)
    v["WIDTH"] = [ROAD_WIDTH.get(t, 6.5) if not MOTORWAY_RE.match(str(n).strip()) else
                  (11.0 if not re.match(r"^A-15-\d+", str(n).strip()) else 7.0)
                  for t, n in zip(v.TYPE_ES.values, v.NAME.values)]
    log(f"Viales tras recorte: {len(v)}")
    if EXTRAS.get("osm") is not None and not getattr(args, "no_extras", False):
        import extras
        v = extras.extra_lines(v, pathw_path, bbox, inner, EXTRAS["osm"], nav, log)
        log(f"Viales + caminos + bidegorris + tren: {len(v)}")

    # nodos por coordenada redondeada (b5m ya viene nodado en cruces)
    G = nx.MultiGraph()
    key = lambda p: (round(p[0] * 2) / 2, round(p[1] * 2) / 2)
    for _, r in v.iterrows():
        c = list(r.geometry.coords)
        if len(c) < 2:
            continue
        bt = str(r.BRIDGE_TUNNEL) if r.BRIDGE_TUNNEL is not None else "0"
        # Códigos BRIDGE_TUNNEL de b5m, verificados contra el MDT (cota del terreno a mitad del tramo
        # respecto a la recta entre sus extremos): 3 = puente/viaducto (mediana -5 m), 4 = túnel (+15 m),
        # 1 = paso inferior (la vía va a nivel y otra pasa por encima, ±0 m), 2 = paso cubierto (±0 m).
        struct = 0
        if bt == "3":
            struct = 1   # puente / viaducto
        elif bt == "4":
            struct = 2   # túnel
        elif bt == "2":
            struct = 3   # paso cubierto bajo edificio
        elif bt == "1":
            struct = 4   # paso inferior: a nivel (sólo sirve de pista en grade_separation; se exporta como 0)
        name = r.NAME.strip()
        rtype = r.TYPE_ES
        w = float(r.WIDTH) if "WIDTH" in r and r.WIDTH == r.WIDTH else ROAD_WIDTH.get(rtype, 6.5)
        if rtype == "Camino":
            struct = 0       # los caminos van pegados al terreno (sin puentes ni túneles propios)
        a, b = key(c[0]), key(c[-1])
        if a == b and len(c) < 4:
            continue
        G.add_edge(a, b, coords=c, name=name, type=rtype, width=w, struct=struct, length=r.geometry.length)

    # mayor componente conexa (sin contar el tren, que es una red aparte: se conserva entera)
    Gr = nx.MultiGraph()
    Gr.add_edges_from((a, b, k, d) for a, b, k, d in G.edges(keys=True, data=True) if d["type"] != "Tren")
    comps = sorted(nx.connected_components(Gr), key=len, reverse=True)
    keep = set(comps[0])
    loose = []
    for a, b, k, d in Gr.edges(keys=True, data=True):
        if a not in keep and d["type"] in ("Camino", "Bidegorri") and d["length"] >= 15:
            loose.append({"t": d["type"], "w": d["width"], "c": d["coords"]})
    EXTRAS["loose"] = loose
    Gt = nx.MultiGraph()
    Gt.add_edges_from((a, b, k, d) for a, b, k, d in G.edges(keys=True, data=True) if d["type"] == "Tren")
    for comp in nx.connected_components(Gt):
        Ls = sum(d["length"] for _, _, d in Gt.subgraph(comp).edges(data=True))
        if Ls >= 300:
            keep |= set(comp)
    G = G.subgraph(keep).copy()
    if Gt.number_of_edges():
        G.remove_edges_from([(a, b, k) for a, b, k, d in list(G.edges(keys=True, data=True))
                             if d["type"] == "Tren" and a not in Gt and b not in Gt])
    log(f"Grafo: {G.number_of_nodes()} nodos, {G.number_of_edges()} tramos (componente principal + tren); "
        f"caminos sueltos sin conexión (sólo dibujo): {len(loose)}")

    # contraer cadenas de grado 2 -> aristas entre cruces/finales (y donde cambia el tipo de vía)
    def is_junction(n):
        if G.degree(n) != 2:
            return True
        return len({d["type"] for _, _, d in G.edges(n, data=True)}) > 1

    visited = set()
    chains = []
    for n in G.nodes:
        if not is_junction(n):
            continue
        for _, nb, k, d in G.edges(n, keys=True, data=True):
            eid = (min(n, nb), max(n, nb), k)
            if eid in visited:
                continue
            visited.add(eid)
            parts = [(n, nb, d)]
            cur, prev_e = nb, eid
            while not is_junction(cur):
                nxt = None
                for _, nb2, k2, d2 in G.edges(cur, keys=True, data=True):
                    e2 = (min(cur, nb2), max(cur, nb2), k2)
                    if e2 not in visited:
                        nxt = (nb2, k2, d2, e2)
                        break
                if nxt is None:
                    break
                visited.add(nxt[3])
                parts.append((cur, nxt[0], nxt[2]))
                cur = nxt[0]
            chains.append(parts)
    # anillos aislados sin cruces (raro)
    log(f"Aristas contraídas: {len(chains)}")

    # ordenar coordenadas de cada parte en el sentido de la cadena
    nodes_used = {}
    node_list = []

    def node_id(k):
        if k not in nodes_used:
            nodes_used[k] = len(node_list)
            node_list.append(k)
        return nodes_used[k]

    edges = []
    for parts in chains:
        pts, flags, names = [], [], defaultdict(float)
        width = 0
        types = defaultdict(float)
        for (a, b, d) in parts:
            c = d["coords"]
            if key(c[0]) != a:
                c = c[::-1]
            c = densify(c, 3.0)
            f = [d["struct"]] * len(c)
            if pts:
                c, f = c[1:], f[1:]
            pts += c
            flags += f
            names[d["name"]] += d["length"]
            types[d["type"]] += d["length"]
            width = max(width, d["width"])
        if len(pts) < 2:
            continue
        # dilatar marcas de túnel (≈15 m) y puente (≈6 m): las bocas y estribos no siguen el MDT
        fl = np.array(flags)
        for code, n_d in ((2, 2), (1, 2)):
            m = fl == code
            if m.any():
                dm = m.copy()
                for k in range(1, n_d + 1):
                    dm[:-k] |= m[k:]
                    dm[k:] |= m[:-k]
                fl = np.where(dm & (fl == 0), code, fl)
        flags = fl.tolist()
        a_id = node_id(parts[0][0])
        b_id = node_id(parts[-1][1])
        name = max(names.items(), key=lambda t: t[1])[0] if names else ""
        rtype = max(types.items(), key=lambda t: t[1])[0]
        edges.append({"a": a_id, "b": b_id, "pts": pts, "flags": flags, "name": name,
                      "type": rtype, "width": width})

    # Bocas de túnel junto a un cruce a nivel: los primeros metros desde el cruce son trinchera a cielo
    # abierto, para que la boca (y su hueco en el terreno) no caiga encima del cruce.
    inc_w = defaultdict(float); inc_grade = defaultdict(int)
    for e in edges:
        for n, fl in ((e["a"], e["flags"][0]), (e["b"], e["flags"][-1])):
            inc_w[n] = max(inc_w[n], e["width"])
            if fl != 2:
                inc_grade[n] += 1
    n_set = 0
    for e in edges:
        f = np.array(e["flags"])
        if not (f == 2).any():
            continue
        p = np.array(e["pts"], np.float64)
        s_ = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(p, axis=0), axis=1))])
        for n, dist in ((e["a"], s_), (e["b"], s_[-1] - s_)):
            end_f = f[0] if n == e["a"] else f[-1]
            if end_f == 2 and inc_grade[n] > 0:
                m = (dist < inc_w[n] * 0.5 + 6.0) & (f == 2)
                if m.any():
                    f[m] = 0; n_set += 1
        e["flags"] = f.tolist()
    log(f"Bocas de túnel retranqueadas desde cruces a nivel: {n_set}")

    # ------------------- alturas
    node_xy = np.array(node_list, np.float64) - origin
    node_h = np.zeros(len(node_list))
    for i, (x, z) in enumerate(node_xy):
        ang = np.linspace(0, 2 * np.pi, 9)[:-1]
        sx = np.concatenate([[x], x + 3 * np.cos(ang)])
        sz = np.concatenate([[z], z + 3 * np.sin(ang)])
        node_h[i] = np.median(sampler(sx, sz))
    # Nodos dentro de túneles o sobre puentes: el MDT da la cota del monte o del valle, no la de la calzada.
    # Se interpola desde los primeros puntos a nivel de cada tramo que llega al nodo (ponderado por distancia).
    inc = defaultdict(list)
    for e in edges:
        inc[e["a"]].append((e, False)); inc[e["b"]].append((e, True))
    fixed = set()
    for _ in range(3):
        for n, lst in inc.items():
            if n in fixed or any(e["flags"][-1 if rev else 0] in (0, 4) for e, rev in lst):
                continue
            num = den = 0.0
            for e, rev in lst:
                p = np.array(e["pts"], np.float64) - origin
                f = np.array(e["flags"])
                if rev:
                    p, f = p[::-1], f[::-1]
                s_ = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(p, axis=0), axis=1))])
                g = np.where((f == 0) | (f == 4))[0]
                if len(g):
                    kk = g[:3]
                    h_ = float(np.median(sampler(p[kk, 0], p[kk, 1])))
                    w_ = 1.0 / max(s_[g[0]], 1.0)
                else:
                    other = e["a"] if rev else e["b"]
                    if other not in fixed:
                        continue
                    h_, w_ = node_h[other], 1.0 / max(s_[-1], 1.0)
                num += w_ * h_; den += w_
            if den > 0:
                node_h[n] = num / den
                fixed.add(n)
    log(f"Nodos en túnel/puente con cota interpolada: {len(fixed)}")
    for e in edges:
        p = np.array(e["pts"], np.float64) - origin
        f = np.array(e["flags"])
        s = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(p, axis=0), axis=1))])
        h = sampler(p[:, 0], p[:, 1]).astype(np.float64)
        ground = (f == 0) | (f == 4)
        # suavizado gaussiano en distancia (sigma ~ 7 m) sólo con puntos a nivel
        if ground.sum() >= 2:
            hs = np.empty_like(h)
            sg, hg = s[ground], h[ground]
            for i in range(len(s)):
                wts = np.exp(-0.5 * ((sg - s[i]) / 7.0) ** 2)
                hs[i] = (wts * hg).sum() / max(wts.sum(), 1e-9)
            h = np.where(ground, hs, h)
        # extremos -> altura de nodo, con fundido de 12 m
        ha, hb = node_h[e["a"]], node_h[e["b"]]
        L = s[-1]
        wa = np.clip(1 - s / 12.0, 0, 1)
        wb = np.clip(1 - (L - s) / 12.0, 0, 1)
        h = h * (1 - wa) + ha * wa
        h = h * (1 - wb) + hb * wb
        # tramos de estructura: interpolación lineal entre apoyos
        if (~ground).any():
            i = 0
            n = len(f)
            while i < n:
                if not ground[i]:
                    j = i
                    while j < n and not ground[j]:
                        j += 1
                    ia = max(i - 1, 0)
                    ib = min(j, n - 1)
                    ha_, hb_ = (h[ia] if ia != i or i == 0 else h[ia]), h[ib]
                    if i == 0:
                        ha_ = node_h[e["a"]]
                    if j >= n:
                        hb_ = node_h[e["b"]]
                    sa, sb = s[ia], s[ib]
                    for k in range(i, j):
                        t = (s[k] - sa) / max(sb - sa, 1e-6)
                        h[k] = ha_ + (hb_ - ha_) * t
                    i = j
                else:
                    i += 1
        h[0], h[-1] = ha, hb
        # limitador de pendiente (elimina artefactos del MDT en taludes y muros)
        gmax = 0.24 if e["type"] in ("Calle", "Bidegorri") else 0.12
        if e["type"] == "Camino":
            gmax = 0.6      # se vuelve a pegar al terreno final tras tallar (ver main)
        elif e["type"] == "Tren":
            gmax = 0.04
        if MOTORWAY_RE.match(e["name"]):
            gmax = 0.07
        if L > 1:
            gmax = max(gmax, abs(hb - ha) / L * 1.05)
            ds = np.diff(s)
            for _ in range(30):
                for i in range(1, len(h) - 1):
                    lim = gmax * ds[i - 1]
                    h[i] = min(max(h[i], h[i - 1] - lim), h[i - 1] + lim)
                for i in range(len(h) - 2, 0, -1):
                    lim = gmax * ds[i]
                    h[i] = min(max(h[i], h[i + 1] - lim), h[i + 1] + lim)
            # suavizado final ligero
            if len(h) > 4:
                hs = h.copy()
                hs[1:-1] = (h[:-2] + 2 * h[1:-1] + h[2:]) / 4
                h = hs
        e["xz"] = p
        e["h"] = h
        e["s"] = s
        e["length"] = float(L)
    # "túneles" sin monte encima (p.ej. bajo una plaza: el MDT ya no tiene la losa) -> paso cubierto a nivel
    n_cov = 0
    for e in edges:
        f = np.array(e["flags"])
        if not (f == 2).any():
            continue
        cover = sampler(e["xz"][:, 0], e["xz"][:, 1]) - e["h"]
        i = 0
        while i < len(f):
            if f[i] == 2:
                j = i
                while j + 1 < len(f) and f[j + 1] == 2:
                    j += 1
                if np.median(cover[i:j + 1]) < 4.0:
                    f[i:j + 1] = 3; n_cov += 1
                i = j + 1
            else:
                i += 1
        e["flags"] = f.tolist()
    log(f"Túneles sin cobertura convertidos en paso cubierto: {n_cov}")
    return node_list, node_xy, node_h, edges


# --------------------------------------------------------------------------- terrain carving
def grade_separation(edges, node_h, clearance=5.5):
    """Cruces a distinto nivel: garantiza gálibo y convierte la vía superior en puente.

    El MDT suaviza viaductos y pasos inferiores, así que dos vías que se cruzan sin nodo común pueden
    quedar a 1-3 m de diferencia (muro). Se sube la vía superior y se baja la inferior con rampas
    suaves (<= 6 %) hasta tener 'clearance' metros, y los puntos superiores pasan a ser puente.
    """
    pts, eid, pidx = [], [], []
    for i, e in enumerate(edges):
        pts.append(e["xz"]); eid += [i] * len(e["xz"]); pidx += list(range(len(e["xz"])))
    P = np.concatenate(pts); eid = np.array(eid); pidx = np.array(pidx)
    tree = cKDTree(P)
    ends = [{e["a"], e["b"]} for e in edges]

    def direction(e, k):
        p = e["xz"]; a, b = max(k - 1, 0), min(k + 1, len(p) - 1)
        d = p[b] - p[a]; n = np.linalg.norm(d)
        return d / n if n > 1e-6 else np.array([1.0, 0.0])

    # Dos vías que se cruzan en planta sin compartir nodo están a distinto nivel (en b5m los cruces a nivel
    # siempre tienen nodo), aunque el MDT las dé a la misma cota.
    crossings = {}
    for i, e in enumerate(edges):
        for k, q in enumerate(e["xz"]):
            for j in tree.query_ball_point(q, 6.0):
                o = eid[j]
                if o <= i or ends[i] & ends[o]:
                    continue
                e2 = edges[o]; k2 = pidx[j]
                dist = float(np.linalg.norm(P[j] - q))
                if dist > 0.5 * min(e["width"], e2["width"]):
                    continue
                dot = abs(np.dot(direction(e, k), direction(e2, k2)))
                if dot > 0.94:                       # paralelas: no es cruce
                    continue
                dh = e["h"][k] - e2["h"][k2]
                if abs(dh) < 1.0:
                    # casi a la misma cota: sólo si es un cruce franco entre calles distintas, lejos de los nodos
                    # o señalado por b5m como puente / paso inferior (si no, son las "cintas" de un mismo cruce)
                    if e["name"] == e2["name"] or dot > 0.8:
                        continue
                    hint = any(c in (1, 4) for c in e["flags"][max(k - 3, 0):k + 4] + e2["flags"][max(k2 - 3, 0):k2 + 4])
                    if not hint and min(e["s"][k], e["s"][-1] - e["s"][k], e2["s"][k2], e2["s"][-1] - e2["s"][k2]) < 12.0:
                        continue
                key = (i, o)
                if key not in crossings or dist < crossings[key][3]:
                    crossings[key] = (k, k2, dh, dist)
    rank = {1: 3, 0: 2, 3: 2, 4: 1, 2: 0}        # puente > a nivel > paso inferior > túnel
    inc = defaultdict(list)
    for i, e in enumerate(edges):
        inc[e["a"]].append(i); inc[e["b"]].append(i)

    def bumpf(d, T):
        return np.where(d < T, 0.5 * (1 + np.cos(np.pi * np.clip(d / T, 0, 1))), 0.0)

    def apply_bump(ei, ki, amount, exclude):
        T = max(25.0, abs(amount) / 0.06)
        e = edges[ei]; s = e["s"]
        e["h"] = e["h"] + amount * bumpf(np.abs(s - s[ki]), T)
        seen_e, seen_n = {ei, exclude}, set()
        frontier = [(e["a"], s[ki]), (e["b"], s[-1] - s[ki])]
        while frontier:
            n, dn = frontier.pop()
            if dn >= T or n in seen_n:
                continue
            seen_n.add(n)
            node_h[n] += amount * float(bumpf(np.array(dn), T))
            for oi in inc[n]:
                if oi in seen_e:
                    continue
                seen_e.add(oi)
                oe = edges[oi]; os_ = oe["s"]
                dist = dn + (os_ if oe["a"] == n else os_[-1] - os_)
                oe["h"] = oe["h"] + amount * bumpf(dist, T)
                frontier.append((oe["b"] if oe["a"] == n else oe["a"], dn + os_[-1]))

    def near_flag(e, k, r=3):
        f = e["flags"][max(k - r, 0):k + r + 1]
        return 1 if 1 in f else (2 if 2 in f else (4 if 4 in f else f[len(f) // 2]))
    resolved = {}
    for (i, o), (k, k2, dh, _) in crossings.items():
        ri, ro = rank[near_flag(edges[i], k)], rank[near_flag(edges[o], k2)]
        if ri != ro:
            up_i = ri > ro
        elif abs(dh) >= 1.0:
            up_i = dh > 0
        else:
            up_i = edges[i]["width"] >= edges[o]["width"]
        if up_i:
            resolved[(i, o)] = (k, k2, dh)
        else:
            resolved[(o, i)] = (k2, k, -dh)
    crossings = resolved
    n_raise = 0
    for (u, l), (ku, kl, dh) in crossings.items():
        lower_tunnel = edges[l]["flags"][kl] == 2
        upper_tunnel = edges[u]["flags"][ku] == 2
        # sobre un túnel el terreno (y la vía de encima) debe quedar por encima de la bóveda
        need = (TUNNEL_COVER + 0.4 if lower_tunnel else clearance) - dh
        if need > 0:
            # la mitad para cada vía; la rampa (coseno, <= 6 %) se propaga por el grafo, nodos incluidos,
            # para que un puente junto a un cruce también gane gálibo sin escalones
            apply_bump(u, ku, need * 0.5, exclude=l)
            apply_bump(l, kl, -need * 0.5, exclude=u)
            n_raise += 1
        # la vía superior es puente donde pasa sobre la inferior (más margen); no si la inferior va en
        # túnel (la de arriba sigue sobre el monte) ni si la superior es un túnel
        if lower_tunnel or upper_tunnel:
            continue
        eu, el = edges[u], edges[l]
        f = np.array(eu["flags"])
        dist, _ = cKDTree(el["xz"]).query(eu["xz"])
        mark = dist < el["width"] * 0.5 + eu["width"] * 0.5 + 4.0
        f[mark] = 1
        eu["flags"] = f.tolist()
    # cualquier tramo que pase por encima del pasillo de una vía inferior (p.ej. un cruce o un ramal justo
    # encima del paso inferior) también es tablero: si no, el terreno de la vía de abajo lo dejaría en el aire
    lowers = {l for (u, l), (ku, kl, dh) in crossings.items()
              if edges[l]["flags"][kl] != 2 and edges[u]["flags"][ku] != 2}
    n_over = 0
    for l in lowers:
        el = edges[l]; tl = cKDTree(el["xz"])
        for i, e in enumerate(edges):
            if i == l:
                continue
            d, j = tl.query(e["xz"], distance_upper_bound=el["width"] / 2 + e["width"] / 2 + 1.0)
            ok = np.isfinite(d)
            if not ok.any():
                continue
            f = np.array(e["flags"])
            over = np.zeros(len(f), bool)
            over[ok] = (e["h"][ok] - el["h"][j[ok]] > 3.0) & (f[ok] != 2)
            if (over & (f != 1)).any():
                dm = over.copy(); dm[1:] |= over[:-1]; dm[:-1] |= over[1:]
                f[dm & (f != 2)] = 1
                e["flags"] = f.tolist(); n_over += 1
    log(f"Cruces a distinto nivel: {len(crossings)} (gálibo corregido en {n_raise}; tramos sobre pasos inferiores: {n_over})")
    # tramos a nivel muy cortos (< 15 m) entre dos estructuras (puente-túnel, puente-puente): todo tablero,
    # para que el tablero empalme directamente con el suelo del túnel o con el otro puente
    n_gap = 0
    for e in edges:
        f = np.array(e["flags"]); s_ = e["s"]
        i = 0
        while i < len(f):
            if f[i] in (0, 4):
                j = i
                while j + 1 < len(f) and f[j + 1] in (0, 4):
                    j += 1
                if i > 0 and j < len(f) - 1 and f[i - 1] in (1, 2) and f[j + 1] in (1, 2) \
                        and s_[j + 1] - s_[i - 1] < 15.0:
                    f[i:j + 1] = 1; n_gap += 1
                i = j + 1
            else:
                i += 1
        e["flags"] = f.tolist()
    log(f"Huecos cortos entre estructuras convertidos en tablero: {n_gap}")


def _rss(tag):
    try:
        with open("/proc/self/status") as f_:
            log(f"  [mem] {tag}: " + next(l for l in f_ if l.startswith("VmRSS")).split(":")[1].strip())
    except Exception:  # noqa: BLE001
        pass


def carve_terrain(H, cell, edges, rivers):
    """El terreno ES la superficie de rodadura: queda a la cota exacta de la calzada, continuo en los cruces.

    Orden: 1) cauces (sólo bajan)  2) bajo puentes (sólo bajan)  3) calzadas a nivel/túneles (fuerzan cota,
    mezcla ponderada entre vías cercanas para que los cruces no tengan escalones)  4) suavizado en calzadas.
    """
    GZ, GX = H.shape
    NQ = GZ * GX

    def qc(gi):
        """Coordenadas (x, z) de las muestras de índice plano gi (no se guarda la rejilla entera: memoria)."""
        gi = np.asarray(gi, np.int64)
        return np.c_[(gi % GX) * cell, (gi // GX) * cell]

    def box_idx(x0, z0, x1, z1):
        ix0, ix1 = max(int(x0 / cell), 0), min(int(x1 / cell) + 1, GX - 1)
        iz0, iz1 = max(int(z0 / cell), 0), min(int(z1 / cell) + 1, GZ - 1)
        if ix1 < ix0 or iz1 < iz0:
            return np.zeros(0, np.int64)
        return (np.arange(iz0, iz1 + 1, dtype=np.int64)[:, None] * GX + np.arange(ix0, ix1 + 1)[None, :]).ravel()

    out = H.ravel().astype(np.float32).copy()
    CH = 120_000   # v16: trozos más pequeños (memoria: el sandbox tiene 2 GB)

    def samples(e, keep_fn):
        p = e["xz"]; f = np.array(e["flags"]); s = e["s"]
        n = max(2, int(e["length"] / 1.0))
        si = np.linspace(0, s[-1], n)
        xi = np.interp(si, s, p[:, 0]); zi = np.interp(si, s, p[:, 1])
        hi = np.interp(si, s, e["h"])
        # bandera del vértice más cercano (interpolar códigos 0/2 daría un 1 = puente falso)
        fi = f[np.round(np.interp(si, s, np.arange(len(s)))).astype(int)]
        k = keep_fn(fi)
        return np.c_[xi[k], zi[k]], hi[k], np.full(k.sum(), e["width"] / 2)

    def lower_pass(P, Hh, W, extra, fall_min):
        if len(P) == 0:
            return
        tree = cKDTree(P)
        for c0 in range(0, NQ, CH):
            gi = np.arange(c0, min(c0 + CH, NQ), dtype=np.int64)
            d, idx = tree.query(qc(gi), k=1, distance_upper_bound=30.0, workers=-1)
            ok = np.isfinite(d)
            if not ok.any():
                continue
            gi, di, ii = gi[ok], d[ok], idx[ok]
            h0 = out[gi]
            target = Hh[ii]
            core = W[ii] + extra
            fall = np.clip(np.abs(h0 - target) * 1.2, fall_min, 20.0)
            t = np.clip(1.0 - (di - core) / fall, 0.0, 1.0)
            w = t * t * (3 - 2 * t)
            w[di <= core] = 1.0
            out[gi] = np.minimum(h0, h0 * (1 - w) + target * w)

    # 1) cauces
    if rivers:
        P = np.concatenate([r["xz"] for r in rivers])
        Hh = np.concatenate([r["h"] - 1.2 for r in rivers])
        W = np.concatenate([np.full(len(r["xz"]), r["width"] / 2) for r in rivers])
        lower_pass(P, Hh, W, 0.0, 4.0)
    _rss("antes de 3)")
    # 3) calzadas a nivel y pasos cubiertos (los puentes y los túneles no tallan: van por encima/debajo)
    rp = [samples(e, lambda fi: (fi != 1) & (fi != 2)) for e in edges]
    P = np.concatenate([r[0] for r in rp]); Hh = np.concatenate([r[1] for r in rp])
    P_road, H_road = P, Hh
    W = np.concatenate([r[2] for r in rp])
    W_road = W
    K = 16
    tree3 = cKDTree(P)
    road_mask = np.zeros(out.shape, bool)
    for c0 in range(0, NQ, CH):                # por bloques: cabe en poca memoria
        gall = np.arange(c0, min(c0 + CH, NQ), dtype=np.int64)
        qq = qc(gall)
        d1, _ = tree3.query(qq, k=1, distance_upper_bound=40.0, workers=-1)
        near = np.isfinite(d1)                 # la mayor parte del monte está lejos de las calles
        if not near.any():
            continue
        gnear = gall[near]
        d, idx = tree3.query(qq[near], k=K, distance_upper_bound=40.0, workers=-1)
        okc = np.isfinite(d[:, 0])
        if not okc.any():
            continue
        d, idx = d[okc], idx[okc]
        valid = np.isfinite(d)
        idx_safe = np.where(valid, idx, 0)
        core_k = W[idx_safe] + 1.2
        # peso: dentro del núcleo de cada vía pesa mucho; mezcla suave entre vías que se solapan (cruces)
        wk = np.where(valid, 1.0 / (np.maximum(d - core_k, 0.0) + 0.6) ** 2, 0.0)
        # la celda pertenece a la vía cuyo BORDE está más cerca (no su eje): entre una autovía ancha y una
        # calle estrecha a distinta cota, el talud cae entre los dos bordes y no dentro de la autovía
        eff = np.where(valid, d - W[idx_safe], np.inf)
        o = np.argmin(eff, axis=1); rr = np.arange(len(o))
        # (sólo entre vías a distinta cota; en los cruces a nivel manda el eje más cercano)
        o = np.where(np.abs(Hh[idx_safe[rr, o]] - Hh[idx_safe[:, 0]]) > 2.0, o, 0)
        own = idx_safe[rr, o]
        # sólo se mezclan vías a cota parecida (cruces a nivel); nunca una vía con otra que pasa por encima/debajo
        wk *= np.abs(Hh[idx_safe] - Hh[own][:, None]) < 4.5
        target = (wk * Hh[idx_safe]).sum(1) / np.maximum(wk.sum(1), 1e-9)
        # núcleo plano hasta media anchura + 2 m: ninguna celda del terreno que toque el asfalto sube
        d0 = d[rr, o]; core = W[own] + 2.0
        gi = gnear[okc]
        h0 = out[gi]
        fall = np.clip(np.abs(h0 - target) * 1.4, 5.0, 30.0)
        t = np.clip(1.0 - (d0 - core) / fall, 0.0, 1.0)
        w = t * t * (3 - 2 * t)
        w[d0 <= core] = 1.0
        out[gi] = h0 * (1 - w) + target * w
        road_mask[gi[d0 <= core + 0.5]] = True
        del d, idx, valid, idx_safe, core_k, wk
    _rss("antes de 4)")
    # 4) suavizado sólo sobre las calzadas (quita aristas de la rejilla en cruces y curvas)
    Hn = out.reshape(H.shape)
    mask = gaussian_filter(road_mask.reshape(H.shape).astype(np.float32), 1.0)
    del road_mask
    Hs = gaussian_filter(Hn, 0.8)
    # en el sitio (memoria): Hn = Hn*(1-mask) + Hs*mask = Hn + (Hs-Hn)*mask
    Hs -= Hn
    Hs *= mask
    Hn += Hs
    del Hs, mask
    _rss("tallado 4")
    if os.environ.get("HERNANI_DEBUG"):
        np.save("/tmp/H_after4.npy", Hn)
    # 5) bajo los tableros de puente el terreno nunca asoma (con transición en los estribos)
    bp_P, bp_H, bp_W = [], [], []
    for e in edges:
        f = np.array(e["flags"]); s_ = e["s"]
        if not (f == 1).any():
            continue
        n = max(2, int(e["length"] / 1.0))
        si = np.linspace(0, s_[-1], n)
        fi = f[np.round(np.interp(si, s_, np.arange(len(s_)))).astype(int)]
        isb = fi == 1
        # distancia (a lo largo) al tramo no-puente más cercano
        # distancia al apoyo: tramo a nivel contiguo (+2 m) o nodo extremo (+ media anchura + 3 m, que el
        # cruce quede entero a cota); el terreno sólo baja más allá
        nb = si[~isb]
        if len(nb) == 0:
            d_in = np.full(n, 1e9)
        else:   # distancia al no-puente más cercano sin matriz n x m (las vías de tren son tramos de km)
            j = np.clip(np.searchsorted(nb, si), 1, len(nb) - 1) if len(nb) > 1 else np.zeros(n, int)
            d_in = np.minimum(np.abs(si - nb[j]), np.abs(si - nb[np.maximum(j - 1, 0)])) - 2.0
        d_end = np.minimum(si, s_[-1] - si) - (e["width"] / 2 + 3.0)
        dend = np.maximum(np.minimum(d_in, d_end), 0.0)
        allP = np.c_[np.interp(si, s_, e["xz"][:, 0]), np.interp(si, s_, e["xz"][:, 1])]
        allH = np.interp(si, s_, e["h"]) - 1.2 * np.clip(dend / 6.0, 0, 1) - 0.03
        # cada puente del tramo por separado (un tramo de vía de varios km puede tener varios)
        ib = np.flatnonzero(isb)
        for run in np.split(ib, np.flatnonzero(np.diff(ib) > 1) + 1):
            if len(run) == 0:
                continue
            bp_P.append(allP[run]); bp_H.append(allH[run])
            bp_W.append(np.full(len(run), e["width"] / 2 + 1.6))   # 1.6 m más allá del borde del tablero
    if bp_P:
        P = np.concatenate(bp_P); Hh = np.concatenate(bp_H); W = np.concatenate(bp_W)
        flat = Hn.ravel()
        # cada tablero por separado y se queda el mínimo: si dos tableros están cerca (calzadas gemelas a
        # distinta cota), el terreno queda por debajo de los dos
        road_tree = cKDTree(P_road)
        for Pb, Hb, Wb in zip(bp_P, bp_H, bp_W):
            if len(Pb) == 0:
                continue
            x0, z0 = Pb.min(0) - Wb.max() - 1.0; x1, z1 = Pb.max(0) + Wb.max() + 1.0
            gi = box_idx(x0, z0, x1, z1)
            if len(gi) == 0:
                continue
            d, idx = cKDTree(Pb).query(qc(gi), k=1, distance_upper_bound=Wb.max() + 0.5, workers=-1)
            ok = np.isfinite(d)
            gi, d, idx = gi[ok], d[ok], idx[ok]
            ins = d <= Wb[idx]
            gi, d, idx = gi[ins], d[ins], idx[ins]
            if len(gi) == 0:
                continue
            # en los estribos manda la calzada a nivel contigua (celdas más cerca de ella que del tablero)
            dr, ir = road_tree.query(qc(gi), k=1, workers=-1)
            keep_road = (dr < d) & (np.abs(H_road[ir] - Hb[idx]) < 2.0)
            gi, idx = gi[~keep_road], idx[~keep_road]
            flat[gi] = np.minimum(flat[gi], Hb[idx])
        Hn = flat.reshape(H.shape)
    if os.environ.get("HERNANI_DEBUG"):
        np.save("/tmp/H_after5.npy", Hn)
    _rss("antes de 6)")
    # 6) túneles: el monte queda por encima de la bóveda (>= TUNNEL_COVER sobre la calzada) y sólo se abren
    #    agujeros en el terreno donde éste se metería dentro del túnel (en la práctica, en las bocas).
    holes = np.zeros(0, np.int64)
    tp = [samples(e, lambda fi: fi == 2) + (ei,) for ei, e in enumerate(edges)]
    tp = [t for t in tp if len(t[0])]
    if tp:
        TE = np.concatenate([np.full(len(t[0]), t[-1]) for t in tp])
        TP = np.concatenate([t[0] for t in tp]); TH = np.concatenate([t[1] for t in tp])
        TW = np.concatenate([t[2] for t in tp])
        ttree, gtree = cKDTree(TP), cKDTree(P_road)
        flat = Hn.ravel().copy()
        rng_t = TW.max() + 3.5 + 6.0
        # v16: una caja por túnel (no la caja de todos: con los del tren cubriría casi todo el mapa)
        tb = np.unique(np.concatenate([box_idx(t[0][:, 0].min() - rng_t, t[0][:, 1].min() - rng_t,
                                               t[0][:, 0].max() + rng_t, t[0][:, 1].max() + rng_t) for t in tp]))
        ii_l, dt_l, it_l = [], [], []
        for c0 in range(0, len(tb), 1_000_000):
            tbc = tb[c0:c0 + 1_000_000]
            d_t, i_t = ttree.query(qc(tbc), k=1, distance_upper_bound=rng_t, workers=-1)
            okt = np.isfinite(d_t)
            ii_l.append(tbc[okt]); dt_l.append(d_t[okt]); it_l.append(i_t[okt])
        ii = np.concatenate(ii_l); dt = np.concatenate(dt_l); it = np.concatenate(it_l)
        del tb, ii_l, dt_l, it_l
        d_g, i_g = gtree.query(qc(ii), k=1, workers=-1)
        # más cerca del túnel que de cualquier calzada a nivel, y nunca sobre el núcleo de una de ellas
        own = (dt < d_g) & (d_g > W_road[i_g] + 2.5)
        tt = np.clip(1.0 - (dt - (TW[it] + 3.5)) / 6.0, 0.0, 1.0)
        wv = tt * tt * (3 - 2 * tt)
        target = TH[it] + TUNNEL_COVER
        cur = flat[ii]
        flat[ii] = np.where(own & (cur < target), cur + (target - cur) * wv, cur)
        Hn = flat.reshape(H.shape)
        # celdas (GX-1)*(GZ-1): agujero si la celda es del túnel y su superficie entra en el gálibo
        CX = GX - 1
        rh = TW.max() + TUNNEL_HOLE_HALF
        cells = []
        for t in tp:   # v16: celdas candidatas por túnel
            P_ = t[0]
            cx0, cx1 = max(int((P_[:, 0].min() - rh) / cell) - 1, 0), min(int((P_[:, 0].max() + rh) / cell) + 1, GX - 2)
            cz0, cz1 = max(int((P_[:, 1].min() - rh) / cell) - 1, 0), min(int((P_[:, 1].max() + rh) / cell) + 1, GZ - 2)
            if cx1 < cx0 or cz1 < cz0:
                continue
            cells.append((np.arange(cz0, cz1 + 1, dtype=np.int64)[:, None] * CX + np.arange(cx0, cx1 + 1)[None, :]).ravel())
        call = np.unique(np.concatenate(cells)) if cells else np.zeros(0, np.int64)
        del cells
        cq = np.c_[(call % CX + 0.5) * cell, (call // CX + 0.5) * cell]
        d_t, i_t = ttree.query(cq, k=1, distance_upper_bound=rh, workers=-1)
        sel = np.where(np.isfinite(d_t))[0]
        sel = sel[d_t[sel] <= TW[i_t[sel]] + TUNNEL_HOLE_HALF]
        cq = cq[sel]; ci = call[sel]; i_t = i_t[sel]
        d_g, i_g = gtree.query(cq, k=1, workers=-1)
        zc, xc = ci // CX, ci % CX
        c4 = np.stack([Hn[zc, xc], Hn[zc, xc + 1], Hn[zc + 1, xc], Hn[zc + 1, xc + 1]])
        road = TH[i_t]
        # agujero si la superficie de la celda se mete en el túnel, salvo que sea la calzada tallada de una
        # vía a nivel (otra calle junto a la boca, o la propia calle antes de la boca)
        on_grade = (d_g <= W_road[i_g] + 2.0) & (np.abs(c4 - H_road[i_g][None, :]).max(0) < 0.3) \
            & (np.abs(H_road[i_g] - road) > 0.5)
        hole = (c4.max(0) > road + 0.12) & (c4.min(0) < road + TUNNEL_COVER - 0.2) & ~on_grade
        # nunca por delante del plano de la boca: en Unity la estructura del túnel (muro de boca + cajón)
        # sólo cubre desde la boca hacia dentro (+1.2 m), así que un agujero delante sería un hueco al vacío
        cedge = TE[i_t]
        for P0, od, hwp, pe in tunnel_portals(edges):
            rel = cq - P0
            ahead = rel @ od
            lat = np.abs(rel @ np.array([od[1], -od[0]]))
            hole &= ~((ahead > 0.0) & (ahead < 15.0) & (lat < hwp + TUNNEL_HOLE_HALF + 1.5) & (cedge == pe))
        # y nunca más allá del cajón de ese tubo (que puede estar recortado por una vía vecina)
        runs = tunnel_runs(edges)
        RA = np.concatenate([r["ax"] for r in runs]); RR = np.concatenate([r["rt"] for r in runs])
        RL = np.concatenate([np.full(len(r["ax"]), r["OL"]) for r in runs])
        RO = np.concatenate([np.full(len(r["ax"]), r["OR"]) for r in runs])
        RH = np.concatenate([np.full(len(r["ax"]), r["hw"]) for r in runs])
        _, kk = cKDTree(RA).query(cq, k=1, workers=-1)
        lat = ((cq - RA[kk]) * RR[kk]).sum(1)
        # (la celda entera dentro del cajón: centro a <= O - 1.05; pero el interior del tubo siempre limpio)
        hole &= np.abs(lat) <= np.maximum(np.where(lat >= 0, RO[kk], RL[kk]) - 1.05, RH[kk] + 0.9)
        holes = ci[hole]
        log(f"Túneles: {len(TP)} m de eje, {len(holes)} celdas de terreno abiertas en las bocas")
    return Hn, holes


def tunnel_runs(edges):
    """Tramos de túnel (como los recorre WorldBuilder: de la boca de entrada a la de salida, +1.2 m) con la
    media anchura exterior del cajón a cada lado (OL, OR). Por defecto media calzada + 3.5 m, pero se recorta
    si al lado hay otra vía dentro del rango de alturas del cajón (p.ej. el otro tubo de la A-15): el cajón
    y la solera de un tubo nunca deben asomar dentro del otro ni sobre una calle vecina.
    Deja en cada arista e["tunnel_o"] = [OL, OR, ...] (uno por tramo) para el JSON."""
    allP = np.concatenate([np.asarray(e["xz"], float) for e in edges])
    allY = np.concatenate([np.asarray(e["h"], float) for e in edges])
    allE = np.concatenate([np.full(len(e["xz"]), ei) for ei, e in enumerate(edges)])
    allM = np.concatenate([np.where(np.asarray(e["flags"]) == 2, e["width"] / 2 + 1.0, e["width"] / 2 + 0.5)
                           for e in edges])
    tall = cKDTree(allP)
    runs = []
    for ei, e in enumerate(edges):
        f = np.asarray(e["flags"]); p = np.asarray(e["xz"], float); h = np.asarray(e["h"], float)
        s = np.asarray(e["s"], float)
        e["tunnel_o"] = []
        hw = e["width"] / 2
        i = 0
        while i < len(f):
            if f[i] != 2:
                i += 1
                continue
            j = i
            while j + 1 < len(f) and f[j + 1] == 2:
                j += 1
            a, b = max(i - 1, 0), j
            s0, s1 = max(s[a] - 1.2, 0.0), min(s[b] + 1.2, s[-1])
            ss = np.linspace(s0, s1, max(2, int((s1 - s0) / 1.0) + 1))
            ax = np.c_[np.interp(ss, s, p[:, 0]), np.interp(ss, s, p[:, 1])]
            ay = np.interp(ss, s, h)
            tg = np.gradient(ax, axis=0); tg /= np.maximum(np.linalg.norm(tg, axis=1)[:, None], 1e-9)
            rt = np.c_[tg[:, 1], -tg[:, 0]]           # = 'right' de Unity (t.z, -t.x)
            OL = OR = hw + 3.5
            atree = cKDTree(ax)
            cand = set()
            for lst in tall.query_ball_point(ax, hw + 3.5 + 8.0):
                cand.update(lst)
            for jj in cand:
                if allE[jj] == ei:
                    continue
                d, k = atree.query(allP[jj])
                rel = allP[jj] - ax[k]
                if (k == 0 and rel @ tg[k] < 0) or (k == len(ax) - 1 and rel @ tg[k] > 0):
                    continue                          # por delante de la boca: no estorba al cajón
                if not (ay[k] - 8.0 < allY[jj] < ay[k] + 6.5):
                    continue                          # pasa muy por encima o muy por debajo
                lim = d - allM[jj]
                if rel @ rt[k] >= 0:
                    OR = min(OR, lim)
                else:
                    OL = min(OL, lim)
            # sin sitio (tubos gemelos pegados): el cajón se queda en el propio hastial (hw + 0.8)
            OL, OR = max(OL, hw + 0.8), max(OR, hw + 0.8)
            e["tunnel_o"] += [round(float(OL), 2), round(float(OR), 2)]
            runs.append(dict(ei=ei, ax=ax, rt=rt, OL=OL, OR=OR, hw=hw))
            i = j + 1
    return runs


def tunnel_portals(edges):
    """Bocas de túnel: (punto XZ, dirección hacia fuera, media anchura). La boca es el último punto a nivel
    antes del tramo de túnel y el último punto de túnel al salir (igual que los tramos en WorldBuilder)."""
    out = []
    for ei, e in enumerate(edges):
        f = np.asarray(e["flags"]); p = np.asarray(e["xz"], float)
        i = 0
        while i < len(f):
            if f[i] == 2:
                j = i
                while j + 1 < len(f) and f[j + 1] == 2:
                    j += 1
                a, b = max(i - 1, 0), j
                if a + 1 < len(p):
                    d = p[a] - p[a + 1]; out.append((p[a], d / max(np.linalg.norm(d), 1e-6), e["width"] / 2, ei))
                if b + 1 < len(p):
                    d = p[b + 1] - p[b]; out.append((p[b], d / max(np.linalg.norm(d), 1e-6), e["width"] / 2, ei))
                i = j + 1
            else:
                i += 1
    return out


# --------------------------------------------------------------------------- rivers
def build_rivers(hyd_path, bbox, origin, sampler, nav=None):
    x0, y0, x1, y1 = bbox
    inner = box(x0 + 5, y0 + 5, x1 - 5, y1 - 5)
    h = gpd.read_file(hyd_path, bbox=bbox)
    h = h[h.HIDDEN.astype(str) != "1"] if "HIDDEN" in h.columns else h
    if nav is not None:
        h = navarra.merge_rivers(h, nav, log)
    h["geometry"] = h.geometry.intersection(inner)
    h = h[~h.geometry.is_empty].explode(index_parts=False)
    h = h[h.geom_type == "LineString"]
    out = []
    for _, r in h.iterrows():
        name = (r.NAME or "").strip()
        if name.lower() == "urumea":
            width = 30.0
        else:
            if r.geometry.length < 60:
                continue
            width = 3.0
        c = np.array(densify(list(r.geometry.coords), 4.0)) - origin
        # nivel del agua: mínimo transversal suavizado
        hh = sampler(c[:, 0], c[:, 1])
        if len(c) > 2:
            dirs = np.gradient(c, axis=0)
            nrm = np.c_[-dirs[:, 1], dirs[:, 0]]
            nrm /= np.maximum(np.linalg.norm(nrm, axis=1)[:, None], 1e-6)
            samples = [hh]
            for off in (-width * 0.3, width * 0.3):
                q = c + nrm * off
                samples.append(sampler(q[:, 0], q[:, 1]))
            hh = np.min(np.vstack(samples), axis=0)
        hh = np.minimum.accumulate(hh) if hh[0] > hh[-1] else np.minimum.accumulate(hh[::-1])[::-1]
        out.append({"name": name, "width": width, "xz": c, "h": hh.astype(float)})
    log(f"Cursos de agua: {len(out)} ({sum(1 for r in out if r['width'] > 10)} tramos Urumea)")
    return out


# --------------------------------------------------------------------------- buildings
def build_buildings(bu_path, bbox, origin, size, Hs, edges, nav=None):
    x0, y0, x1, y1 = bbox
    inner = box(x0 + 2, y0 + 2, x1 - 2, y1 - 2)
    b = gpd.read_file(bu_path, bbox=bbox)
    if nav is not None:
        b = navarra.merge_buildings(b, nav, log)
    b = b[b.geometry.within(inner)]
    b = b.explode(index_parts=False)
    b = b[b.geom_type == "Polygon"]
    log(f"Edificios en área: {len(b)}")

    # buffers de calzada (a nivel) para detectar soportales / estructuras sobre la calle
    lines = []
    for e in edges:
        p = e["xz"] + origin
        f = np.array(e["flags"])
        if len(p) >= 2:
            lines.append(LineString(p).buffer(max(e["width"] / 2 - 1.0, 1.5), cap_style=2))
    road_tree = shapely.STRtree(lines)

    out = []
    dropped = arcades = 0
    for _, r in b.iterrows():
        g = r.geometry.simplify(0.35, preserve_topology=True)
        if g.is_empty or g.area < 6 or g.geom_type != "Polygon":
            continue
        g = shapely.make_valid(g)
        if g.geom_type != "Polygon":
            continue
        kind = 0 if r.TYPE_ES == "Edificio" else 1
        cand = road_tree.query(g)
        over = sum(lines[i].intersection(g).area for i in cand) if len(cand) else 0.0
        lift = 0.0
        if over > 3.0:
            if kind == 1 or over > 0.5 * g.area:
                dropped += 1
                continue
            lift = 4.5
            arcades += 1
        hgt = r.AVERAGE_HEIGHT
        if hgt is None or not np.isfinite(hgt) or hgt <= 0:
            hgt = 7.0 if kind == 0 else 3.5
        hgt = float(np.clip(hgt, 2.5, 60))
        ext = np.array(g.exterior.coords)[:-1]
        if shapely.is_ccw(g.exterior):
            ext = ext[::-1]  # anillo exterior en sentido horario (visto desde arriba, en Unity)
        rings = [ext]
        for it in g.interiors:
            ring = np.array(it.coords)[:-1]
            if not shapely.is_ccw(it):
                ring = ring[::-1]
            if len(ring) >= 3:
                rings.append(ring)
        allv = np.concatenate(rings)
        loc = allv - origin
        ground = Hs(loc[:, 0], loc[:, 1])
        base = float(ground.min()) - 0.6
        top = float(ground.mean()) + hgt
        top = max(top, float(ground.max()) + 2.2)
        # triangulación del tejado (Delaunay restringida, sin vértices nuevos)
        try:
            tris = shapely.constrained_delaunay_triangles(g)
        except Exception:
            continue
        vtree = cKDTree(allv)
        tri_idx = []
        okt = True
        for t in tris.geoms:
            c = list(t.exterior.coords)[:3]
            dd, ids = vtree.query(c)
            if dd.max() > 1e-3:
                okt = False
                break
            ids = [int(i) for i in ids]
            # orientación horaria vista desde arriba => normal +Y en Unity (x=E, z=N)
            (ax, ay), (bx, by), (cx, cy) = c
            cross = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)
            if cross > 0:
                ids = [ids[0], ids[2], ids[1]]
            tri_idx += ids
        if not okt or not tri_idx:
            continue
        name = (r.NAME_BUILDING_ES or r.NAME_BUILDING_EU or "") if "NAME_BUILDING_ES" in r else ""
        name_eu = (r.NAME_BUILDING_EU or "") if "NAME_BUILDING_EU" in r else ""
        addr = r.NAME or ""
        out.append({
            "v": [rnd(c, 2) for xy in loc for c in xy],
            "r": [len(x) for x in rings],
            "t": tri_idx,
            "b": rnd(base),
            "h": rnd(top),
            "l": lift,
            "k": kind,
            "n": (name or "").strip(),
            "a": addr.strip(),
            "y": int(r.DATE_CONSTRUCTION) if str(r.DATE_CONSTRUCTION).isdigit() else 0,
            "_eu": (name_eu or "").strip(),
        })
    log(f"Edificios exportados: {len(out)} (soportales: {arcades}, descartados sobre calzada: {dropped})")
    return out


# --------------------------------------------------------------------------- toponymy
def build_places(topon_path, bbox, origin, Hs, nav=None):
    x0, y0, x1, y1 = bbox
    t = gpd.read_file(topon_path, bbox=bbox, ignore_geometry=True)
    if nav is not None:
        t = navarra.merge_places(t, nav, log)
    zones, landmarks = [], []
    zone_types = {"barrio", "barrio y/o nombre urbano", "núcleo urbano", "polígono industrial",
                  "parque urbano", "parque tecnológico", "rotonda", "puente"}
    for _, r in t.iterrows():
        x, y = r.X_CEN_ETRS89, r.Y_CEN_ETRS89
        if x is None or not (x0 < x < x1 and y0 < y < y1):
            continue
        lx, lz = x - origin[0], y - origin[1]
        name = (r.NAME or "").strip()
        ty = (r.TYPE_ES or "").strip()
        if ty in zone_types:
            short = name.split(" / ")[0]
            short = re.sub(r",\s*(auzoa|herrigunea|biribilgunea)$", "", short)
            primary = any(name.startswith(p) for p in PRIMARY_ZONES)
            disp = ZONE_DISPLAY.get(name.split(" / ")[0], short)
            zones.append({"name": disp, "full": name, "type": ty, "x": rnd(lx, 1), "z": rnd(lz, 1),
                          "y": rnd(Hs(lx, lz)[0], 1), "primary": bool(primary),
                          "district": ty in ("barrio", "barrio y/o nombre urbano", "núcleo urbano",
                                             "polígono industrial", "parque tecnológico")})
        elif ty in ("edificio", "monumento", "iglesia", "ermita", "cima", "monte") and name:
            if "baserria" in name.lower() and ty == "edificio":
                continue
            landmarks.append({"name": name, "type": ty, "x": rnd(lx, 1), "z": rnd(lz, 1),
                              "y": rnd(Hs(lx, lz)[0], 1)})
    for ez in EXTRA_ZONES:
        if x0 < ez["E"] < x1 and y0 < ez["N"] < y1:
            lx, lz = ez["E"] - origin[0], ez["N"] - origin[1]
            zones.append({"name": ez["name"], "full": ez["full"], "type": ez["type"], "x": rnd(lx, 1), "z": rnd(lz, 1),
                          "y": rnd(Hs(lx, lz)[0], 1), "primary": ez["primary"], "district": True})
    # deduplicar
    seen = set()
    lm = []
    for l in landmarks:
        k = (l["name"], round(l["x"] / 20), round(l["z"] / 20))
        if k not in seen:
            seen.add(k)
            lm.append(l)
    log(f"Zonas/topónimos: {len(zones)}, lugares de interés: {len(lm)}")
    return zones, lm


# --------------------------------------------------------------------------- trees
def build_trees(H, cell, edges, buildings, rivers, seed=7):
    rng = np.random.default_rng(seed)
    GZ, GX = H.shape
    sx, sz = (GX - 1) * cell, (GZ - 1) * cell
    big = [e for e in edges if e["type"] not in ("Camino", "Bidegorri")]
    small = [e for e in edges if e["type"] in ("Camino", "Bidegorri")]
    road_pts = np.concatenate([e["xz"] for e in big])
    rtree = cKDTree(road_pts)
    bpts = []
    for b in buildings:
        v = np.array(b["v"]).reshape(-1, 2)
        bpts.append(v)
        bpts.append(v.mean(axis=0, keepdims=True))
    btree = cKDTree(np.concatenate(bpts))
    wpts = np.concatenate([r["xz"] for r in rivers]) if rivers else np.zeros((1, 2)) - 1e5
    wtree = cKDTree(wpts)
    step = TREE_STEP
    gx, gz = np.meshgrid(np.arange(step / 2, sx, step), np.arange(step / 2, sz, step))
    P = np.c_[gx.ravel(), gz.ravel()] + rng.uniform(-step * 0.45, step * 0.45, (gx.size, 2))
    P = P[(P[:, 0] > 2) & (P[:, 0] < sx - 2) & (P[:, 1] > 2) & (P[:, 1] < sz - 2)]
    dr, _ = rtree.query(P, distance_upper_bound=60)
    if small:
        sp = np.concatenate([e["xz"] for e in small])
        ds, _ = cKDTree(sp).query(P, distance_upper_bound=60)
        dr = np.minimum(dr, ds + 5.0)          # junto a los caminos basta con 4 m libres
    db, _ = btree.query(P, distance_upper_bound=60)
    dw, _ = wtree.query(P, distance_upper_bound=60)
    gy, gxg = np.gradient(H, cell)
    slope = np.hypot(gy, gxg)
    ix = np.clip((P[:, 0] / cell).astype(int), 0, GX - 1)
    iz = np.clip((P[:, 1] / cell).astype(int), 0, GZ - 1)
    sl = slope[iz, ix]
    del gy, gxg, slope
    keep = (dr > 9) & (db > 9) & (dw > 18) & (sl < 1.1)
    # densidad: más bosque lejos de edificios
    prob = np.clip((np.minimum(db, 60) - 9) / 40.0, 0.08, 0.8)
    keep &= rng.random(len(P)) < prob
    P = P[keep]
    y = map_coordinates(H, [P[:, 1] / cell, P[:, 0] / cell], order=1)
    scale = rng.uniform(0.75, 1.45, len(P))
    kind = (rng.random(len(P)) < 0.35).astype(int)   # 0 = frondoso, 1 = conífera
    flat = np.c_[np.round(P, 1), np.round(y, 2), np.round(scale, 2), kind].ravel().tolist()
    log(f"Árboles: {len(P)}")
    return flat


TREE_STEP = 14.0


def write_heightmap(path, Hc, hmin, hrange):
    """heightmap.bytes comprimido: 'HMZ1', gx, gz (int32 LE) y luego gzip de las diferencias uint16 (por filas,
    con la primera columna respecto a la fila anterior), separadas en plano de bytes bajos y altos."""
    import gzip
    A = (np.clip((Hc - hmin) / hrange, 0, 1) * 65535).round().astype(np.uint16)
    D = A.copy()
    D[:, 1:] = A[:, 1:] - A[:, :-1]          # aritmética módulo 2^16
    D[1:, 0] = A[1:, 0] - A[:-1, 0]
    lo = (D & 0xFF).astype(np.uint8).tobytes()
    hi = (D >> 8).astype(np.uint8).tobytes()
    gz_, gx_ = A.shape
    with open(path, "wb") as f:
        f.write(b"HMZ1" + np.array([gx_, gz_], "<i4").tobytes())
        f.write(gzip.compress(lo + hi, 6))



# --------------------------------------------------------------------------- presets check
def max_turn(poly):
    """Giro más cerrado (grados) a escala de ±6 m en una polilínea cerrada."""
    P = np.asarray(poly)
    if len(P) < 8:
        return 0.0
    seg = np.linalg.norm(np.diff(np.vstack([P, P[:1]]), axis=0), axis=1)
    s = np.concatenate([[0], np.cumsum(seg)])
    L = s[-1]
    t = np.arange(0, L, 2.0)
    x = np.interp(t, s, np.r_[P[:, 0], P[0, 0]])
    z = np.interp(t, s, np.r_[P[:, 1], P[0, 1]])
    Q = np.c_[x, z]
    d0 = Q - np.roll(Q, 3, axis=0)
    d1 = np.roll(Q, -3, axis=0) - Q
    c = (d0 * d1).sum(1) / np.maximum(np.linalg.norm(d0, axis=1) * np.linalg.norm(d1, axis=1), 1e-6)
    return float(np.degrees(np.arccos(np.clip(c, -1, 1))).max())


def loop_route(G, ids, penalty=4.0, want_poly=False):
    """Mismo algoritmo que Unity (CircuitBuilder): Dijkstra por tramos con penalización de reutilización."""
    used = set()
    poly = []
    total = reuse = 0.0
    for i in range(4):
        a, b = ids[i], ids[(i + 1) % 4]
        if a == b:
            return None
        wfun = lambda u, v, d: d["w"] * (penalty if (min(u, v), max(u, v)) in used else 1.0)
        try:
            path = nx.dijkstra_path(G, a, b, weight=wfun)
        except nx.NetworkXNoPath:
            return None
        for u, v in zip(path[:-1], path[1:]):
            k = (min(u, v), max(u, v))
            w = G[u][v]["w"]
            if want_poly:
                c = G[u][v]["xz"]
                if G[u][v]["a"] != u:
                    c = c[::-1]
                poly.extend(c.tolist())
            total += w
            if k in used:
                reuse += w
            used.add(k)
    if want_poly:
        return total, reuse, max_turn(poly)
    return total, reuse


def check_presets(node_xy, edges, origin):
    G = nx.Graph()
    for i, e in enumerate(edges):
        if e["a"] == e["b"]:
            continue
        if G.has_edge(e["a"], e["b"]) and G[e["a"]][e["b"]]["w"] <= e["length"]:
            continue
        G.add_edge(e["a"], e["b"], w=e["length"], xz=e["xz"], a=e["a"])
    deg = dict(G.degree())
    junctions = np.array([n for n, d in deg.items() if d >= 3])
    jt = cKDTree(node_xy[junctions])
    out = []
    import itertools
    for p in PRESETS:
        cands = []
        for (x, y) in p["points"]:
            d, k = jt.query([x - origin[0], y - origin[1]], k=6)
            cands.append([(int(junctions[j]), float(dd)) for dd, j in zip(d, k)])
        best = None
        for combo in itertools.product(*cands):
            ids = [c[0] for c in combo]
            if len(set(ids)) < 4:
                continue
            r = loop_route(G, ids, want_poly=True)
            if r is None:
                continue
            total, reuse, turn = r
            score = (60.0 * reuse / total + abs(total - p["target"]) / p["target"] + sum(c[1] for c in combo) / 1500
                     + (3.0 if turn > 160 else 0.0) + (0.5 if turn > 140 else 0.0))
            if best is None or score < best[0]:
                best = (score, ids, total, reuse)
        if best is None:
            log(f"Preset '{p['name']}' sin solución")
            continue
        _, ids, total, reuse = best
        turn = loop_route(G, ids, want_poly=True)[2]
        log(f"Preset '{p['name']}': nodos {ids}  longitud {total:.0f} m  reutilizado {reuse:.0f} m  giro máx {turn:.0f}º")
        out.append({"name": p["name"], "length": rnd(total, 0),
                    "points": [rnd(node_xy[i][k], 1) for i in ids for k in (0, 1)]})
    return out


# --------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(description="Genera los datos reales de Hernani para Unity")
    ap.add_argument("--bounds", nargs=4, type=float, default=DEFAULT_BOUNDS, metavar=("W", "S", "E", "N"),
                    help="Rectángulo en UTM30 ETRS89 (por defecto: todo el término de Hernani)")
    ap.add_argument("--center", nargs=2, type=float, help="(alternativa) centro de un área cuadrada (E N)")
    ap.add_argument("--size", type=float, help="(con --center) lado del área cuadrada en metros")
    ap.add_argument("--cell", type=float, default=DEFAULT_CELL, help="Separación del heightmap en metros (2 m)")
    ap.add_argument("--dtm", help="GeoTIFF MDT local (p.ej. MDT LiDAR geoEuskadi 1 m, EPSG:25830)")
    ap.add_argument("--no-geoeuskadi", action="store_true", help="No intentar WCS de geoEuskadi")
    ap.add_argument("--refresh-dtm", action="store_true")
    ap.add_argument("--no-motorways", action="store_true", help="Excluir A-15/AP-1/AP-8")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--no-osm", action="store_true", help="No añadir sentidos/semáforos/señales de OpenStreetMap")
    ap.add_argument("--no-extras", action="store_true", help="Sin caminos de monte, bidegorris, tren ni toponimia v16")
    ap.add_argument("--no-navarra", action="store_true", help="No completar la parte de Navarra con los datos de IDENA (SITNA)")
    args = ap.parse_args()

    cell = float(args.cell)
    if args.center:
        sq = args.size or 3000.0
        W, S = args.center[0] - sq / 2, args.center[1] - sq / 2
        E, N = W + sq, S + sq
    else:
        W, S, E, N = args.bounds
    ox, oy = round(W), round(S)
    # nº de celdas por eje múltiplo de TILE_CELLS (el rectángulo crece hacia el este y el norte, nunca hacia el sur)
    cx_n = int(math.ceil((E - W) / cell / TILE_CELLS - 1e-6)) * TILE_CELLS
    cz_n = int(math.ceil((N - S) / cell / TILE_CELLS - 1e-6)) * TILE_CELLS
    gx, gz = cx_n + 1, cz_n + 1
    sx, sz = cx_n * cell, cz_n * cell
    bbox = (ox, oy, ox + sx, oy + sz)
    origin = np.array([ox, oy], np.float64)
    log(f"Área: E {bbox[0]}-{bbox[2]:.0f}  N {bbox[1]}-{bbox[3]:.0f}  ({sx:.0f} x {sz:.0f} m, rejilla {gx} x {gz}"
        f" a {cell} m, {cx_n // TILE_CELLS} x {cz_n // TILE_CELLS} teselas de {TILE_CELLS * cell:.0f} m)")

    paths = {k: cached_download(k, u) for k, u in B5M_DATASETS.items()}
    if not args.no_extras:
        import extras
        try:
            EXTRAS["osm"] = extras.load_osm(CACHE, bbox, log)
        except Exception as ex:  # noqa: BLE001
            log(f"AVISO: sin caminos/bidegorris de OpenStreetMap ({ex}); sólo los de b5m")
            EXTRAS["osm"] = {"elements": []}
    nav = None
    if not args.no_navarra:
        try:
            nav = navarra.load(bbox, CACHE, log)
        except Exception as ex:  # noqa: BLE001
            log(f"AVISO: sin datos de Navarra de IDENA ({ex}); esa parte quedará sin calles ni edificios")
    H, dtm_src = load_dtm(args, bbox, gx, gz, cell)
    Hs_raw = HeightSampler(H, cell)
    log(f"Altitudes: {H.min():.1f} - {H.max():.1f} m")

    node_list, node_xy, node_h, edges = build_roads(paths["pathw"], bbox, origin, None, Hs_raw, args, nav)
    rivers = build_rivers(paths["hyd"], bbox, origin, Hs_raw, nav)
    # memoria (2 GB): los datos de OSM de la v16 se sueltan antes de tallar y se vuelven a leer después
    EXTRAS["osm"] = None
    import gc
    gc.collect()
    try:
        with open("/proc/self/status") as f_:
            log("Memoria antes de tallar: " + next(l for l in f_ if l.startswith("VmRSS")).split(":")[1].strip())
    except Exception:  # noqa: BLE001
        pass
    carve_e = [e for e in edges if e["type"] != "Camino"]
    road_e = [e for e in edges if e["type"] in ROAD_TYPES]
    grade_separation(carve_e, node_h)
    log("Tallando calles y cauces en el terreno...")
    Hc, holes = carve_terrain(H, cell, carve_e, rivers)
    del H
    Hs = HeightSampler(Hc, cell)
    # los caminos no tallan: se pegan al terreno final (incluido el tallado de las calles con las que empalman)
    road_nodes = {n for e in edges if e["type"] != "Camino" for n in (e["a"], e["b"])}
    for e in edges:
        if e["type"] != "Camino":
            continue
        h = Hs(e["xz"][:, 0], e["xz"][:, 1]).astype(np.float64)
        if len(h) > 4:
            h[1:-1] = (h[:-2] + 2 * h[1:-1] + h[2:]) / 4
        for ni, k in ((e["a"], 0), (e["b"], -1)):
            if ni in road_nodes:
                h[k] = node_h[ni]
            else:
                node_h[ni] = h[k]
        e["h"] = h
    buildings = build_buildings(paths["bu"], bbox, origin, None, Hs, road_e, nav)
    zones, landmarks = build_places(paths["topon"], bbox, origin, Hs, nav)
    trees = build_trees(Hc, cell, edges, buildings, rivers)
    presets = check_presets(node_xy, road_e, origin)
    extra_scn = {}
    if not args.no_extras:
        import extras
        try:
            osm = extras.load_osm(CACHE, bbox, log)
        except Exception:  # noqa: BLE001
            osm = {"elements": []}
        extras.building_names(paths["topon"], bbox, origin, buildings, osm, road_e + [e for e in edges if e["type"] == "Camino"], Hs, log)
        extra_scn["peaks"] = extras.peaks(paths["topon"], bbox, origin, Hs, osm, landmarks, log)
        extra_scn["stations"] = extras.stations(osm, origin)
        try:
            extra_scn["areas"] = extras.admin_areas(CACHE, bbox, origin, log)
        except Exception as ex:  # noqa: BLE001
            log(f"AVISO: sin términos municipales de OSM ({ex})")
            extra_scn["areas"] = []
        extra_scn["loose"] = [{"t": l["t"], "w": l["w"],
                               "p": [rnd(v) for x, z in l["c"] for v in (x - origin[0], float(Hs(x - origin[0], z - origin[1])[0]), z - origin[1])]}
                              for l in EXTRAS["loose"]]
        log(f"Estaciones: {', '.join(s['name'] for s in extra_scn['stations'])}; caminos sueltos: {len(extra_scn['loose'])}")

    os.makedirs(args.out, exist_ok=True)
    # heightmap: uint16 LE, filas de sur a norte, columnas de oeste a este
    hmin = 0.0
    hrange = float(math.ceil((Hc.max() + 20) / 10) * 10)
    write_heightmap(os.path.join(args.out, "heightmap.bytes"), Hc, hmin, hrange)

    nodes_json = [{"x": rnd(x), "z": rnd(z), "y": rnd(h)} for (x, z), h in zip(node_xy, node_h)]
    edges_json = []
    for e in edges:
        edges_json.append({
            "a": e["a"], "b": e["b"], "name": e["name"], "type": e["type"], "width": e["width"],
            "length": rnd(e["length"], 1),
            "p": [rnd(v) for xz, h in zip(e["xz"], e["h"]) for v in (xz[0], h, xz[1])],
            "f": [0 if int(x) == 4 else int(x) for x in e["flags"]],
            "o": e.get("tunnel_o", []),
        })
    rivers_json = [{"name": r["name"], "width": r["width"],
                    "p": [rnd(v) for xz, h in zip(r["xz"], r["h"]) for v in (xz[0], h + 0.15, xz[1])]}
                   for r in rivers]
    meta = {
        "version": 1,
        "generated": dt.datetime.now().isoformat(timespec="seconds"),
        "crs": "EPSG:25830 (ETRS89 / UTM 30N)",
        "originE": float(ox), "originN": float(oy),
        "sizeX": float(sx), "sizeZ": float(sz), "size": float(max(sx, sz)),
        "gridX": int(gx), "gridZ": int(gz), "cell": cell,
        "tileCells": TILE_CELLS,
        "heightmapRes": 0, "heightMin": hmin, "heightRange": hrange,
        "dtmSource": dtm_src,
        "attribution": [
            "Viales, edificios, toponimia e hidrografía: b5m - IDE de Gipuzkoa, Diputación Foral de Gipuzkoa (CC BY 4.0)",
            f"Modelo digital del terreno: {dtm_src}",
        ] + ([navarra.ATTRIBUTION] if nav is not None else []),
        "presets": presets,
    }
    with open(os.path.join(args.out, "meta.json"), "w", encoding="utf8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
    with open(os.path.join(args.out, "roads.json"), "w", encoding="utf8") as f:
        # holes: celdas del terreno (índice z*(gridX-1)+x) abiertas en las bocas de los túneles
        json.dump({"nodes": nodes_json, "edges": edges_json, "holes": [int(v) for v in holes]},
                  f, ensure_ascii=False, separators=(",", ":"))
    for b in buildings:
        b.pop("_eu", None)
    with open(os.path.join(args.out, "buildings.json"), "w", encoding="utf8") as f:
        json.dump({"buildings": buildings}, f, ensure_ascii=False, separators=(",", ":"))
    with open(os.path.join(args.out, "scenery.json"), "w", encoding="utf8") as f:
        json.dump({"zones": zones, "landmarks": landmarks, "rivers": rivers_json, "trees": trees, **extra_scn},
                  f, ensure_ascii=False, separators=(",", ":"))
    if not args.no_osm:
        try:
            import osm_traffic
            osm_traffic.run(os.path.join(args.out, "roads.json"))
        except Exception as ex:  # noqa: BLE001
            log(f"AVISO: sin datos de OpenStreetMap ({ex}); todas las calles serán de doble sentido")
    for fn in os.listdir(args.out):
        if not fn.endswith(".meta"):
            log(f"  {fn}: {os.path.getsize(os.path.join(args.out, fn))/1e6:.2f} MB")
    log(f"Listo -> {args.out}")


if __name__ == "__main__":
    main()
