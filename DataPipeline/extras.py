"""Versión 16: caminos de monte, bidegorris, ferrocarril, nombres de montes y casas, términos municipales.

Fuentes:
  * b5m PATHW (Diputación Foral de Gipuzkoa, CC BY 4.0): TYPE_ES 'Carril-bici' (bidegorris), 'Sendero' (GR/PR/SL)
    y 'Ferrocarril' (Adif y ETS, con puentes y túneles).
  * OpenStreetMap (ODbL): highway=track/path/bridleway/footway (caminos), highway=cycleway (bidegorris),
    estaciones y apeaderos, cimas (natural=peak) y nombres de edificios, límites municipales y de barrio.
  * IDENA (SITNA, Gobierno de Navarra, CC BY 4.0): caminos de la parte de Navarra.
  * b5m TOPON: cimas, montes y edificios con nombre (caseríos...).

Todo se devuelve en las mismas columnas que los viales de b5m (TYPE_ES, NAME, BRIDGE_TUNNEL) más WIDTH, ya
recortado, sin duplicar las calles y carreteras existentes, y nodado con ellas (empalmes y cruces a nivel).
"""
import json
import math
import os
import re
import time
import urllib.parse
import urllib.request

import geopandas as gpd
import numpy as np
from shapely.geometry import LineString, MultiLineString, Point, Polygon, MultiPolygon, box
from shapely.ops import substring, linemerge, polygonize, unary_union
from shapely.strtree import STRtree
from plates import plate_facade   # placa del nombre en la fachada (también corrige datos ya generados)

OVERPASS = ["https://maps.mail.ru/osm/tools/overpass/api/interpreter", "https://overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter"]
UA = "HernaniRacing/1.0 (data pipeline)"
OSM_EXTRA = "osm16_extra.json"
OSM_ADMIN = "osm16_admin.json"

W_TRACK, W_PATH, W_BIDE, W_TREN = 3.0, 2.0, 2.6, 8.5
NEW_TYPES = ("Camino", "Bidegorri", "Tren")
SNAP = 12.0              # un camino que acaba a menos de 12 m de otra vía se empalma con ella
MIN_PIECE = 12.0


def _log(*a):
    print("[extras]", *a, flush=True)


def ll_bbox(bbox):
    from pyproj import Transformer
    t = Transformer.from_crs(25830, 4326, always_xy=True)
    w, s = t.transform(bbox[0], bbox[1])
    e, n = t.transform(bbox[2], bbox[3])
    w2, n2 = t.transform(bbox[0], bbox[3])
    e2, s2 = t.transform(bbox[2], bbox[1])
    return min(s, s2), min(w, w2), max(n, n2), max(e, e2)


def overpass(q, log=_log):
    last = None
    for url in OVERPASS * 2:
        try:
            log("Descargando OSM:", url)
            req = urllib.request.Request(url, data=urllib.parse.urlencode({"data": q}).encode(),
                                         headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=200) as r:
                raw = r.read()
            d = json.loads(raw)
            if "elements" in d:
                return d
        except Exception as ex:  # noqa: BLE001
            last = ex
            log("  fallo:", ex)
            time.sleep(5)
    raise RuntimeError(f"No se pudo descargar OSM: {last}")


def load_osm(cache, bbox, log=_log):
    path = os.path.join(cache, OSM_EXTRA)
    if os.path.exists(path) and os.path.getsize(path) > 1000:
        with open(path, encoding="utf8") as f:
            return json.load(f)
    s, w, n, e = ll_bbox(bbox)
    bb = f"{s:.4f},{w:.4f},{n:.4f},{e:.4f}"
    q = (f'[out:json][timeout:180];('
         f'way["highway"~"^(track|path|footway|bridleway|cycleway|pedestrian|steps)$"]({bb});'
         f'way["railway"~"^(rail|platform|construction|disused|abandoned)$"]({bb});'
         f'node["railway"~"^(station|halt|stop)$"]({bb});'
         f'node["natural"~"^(peak|hill)$"]({bb});'
         f'way["building"]["name"]({bb});node["name"]["building"]({bb});'
         f'way["building"]["addr:housename"]({bb});'
         f');out geom tags;')
    d = overpass(q, log)
    os.makedirs(cache, exist_ok=True)
    with open(path, "w", encoding="utf8") as f:
        json.dump(d, f)
    return d


def _tr():
    from pyproj import Transformer
    return Transformer.from_crs(4326, 25830, always_xy=True)


# --------------------------------------------------------------------------- líneas nuevas
def _explode(g):
    if g is None or g.is_empty:
        return []
    if isinstance(g, LineString):
        return [g]
    if isinstance(g, MultiLineString):
        return list(g.geoms)
    if hasattr(g, "geoms"):
        out = []
        for x in g.geoms:
            out += _explode(x)
        return out
    return []


def _clean_bide_name(n):
    n = (n or "").strip()
    if not n or re.match(r"^\d\d \d{3}", n):
        return "Bidegorri"
    if "Navarra" in n and "Andoain" in n:
        return "Bidegorri Plazaola (Andoain - Navarra)"
    return "Bidegorri " + n if len(n) < 34 else "Bidegorri"


def _rows_b5m(pathw_path, bbox):
    p = gpd.read_file(pathw_path, bbox=bbox)
    p = p[p.TYPE_ES.isin(["Carril-bici", "Sendero", "Ferrocarril"])]
    rows = []
    for _, r in p.iterrows():
        bt = str(r.BRIDGE_TUNNEL) if r.BRIDGE_TUNNEL is not None else "0"
        nm = (r.NAME or "").strip()
        if r.TYPE_ES == "Carril-bici":
            t, w, name = "Bidegorri", W_BIDE, _clean_bide_name(nm)
        elif r.TYPE_ES == "Sendero":
            t, w = "Camino", W_PATH
            name = "Sendero " + nm if nm and nm != "SIN NOMBRE" else "Sendero"
            bt = "0"
        else:
            t, w = "Tren", W_TREN
            name = "Ferrocarril Madrid-Irun (Adif)" if nm.startswith("Adif") else "Y vasca (ETS, en obras)"
        for g in _explode(r.geometry):
            rows.append({"geometry": LineString([(c[0], c[1]) for c in g.coords]), "TYPE_ES": t, "NAME": name,
                         "BRIDGE_TUNNEL": bt, "WIDTH": w, "SRC": "b5m-" + t})
    return rows


def _rows_osm(osm):
    T = _tr()
    ways = []
    for e in osm["elements"]:
        if e["type"] != "way" or "geometry" not in e:
            continue
        t = e.get("tags", {})
        hw = t.get("highway")
        if t.get("area") == "yes":
            continue
        if hw in ("track", "path", "bridleway") or (hw == "footway" and t.get("footway") not in
                                                    ("sidewalk", "crossing", "traffic_island", "access_aisle")):
            typ = "Camino"
            w = W_TRACK if hw == "track" else W_PATH
            name = t.get("name") or ("Pista" if hw == "track" else "Senda")
        elif hw == "cycleway":
            typ, w = "Bidegorri", W_BIDE
            name = "Bidegorri " + t["name"] if t.get("name") and "idegorri" not in t["name"] else (t.get("name") or "Bidegorri")
        else:
            continue
        if t.get("access") in ("private", "no") and typ == "Camino" and hw != "track":
            continue
        bt = "0"
        if t.get("bridge") not in (None, "no"):
            bt = "3"
        elif t.get("tunnel") not in (None, "no"):
            bt = "4"
        xy = [T.transform(p["lon"], p["lat"]) for p in e["geometry"]]
        ways.append((xy, typ, name, bt, w))
    # partir las vías de OSM en los nodos compartidos (OSM sólo comparte nodos, no corta las vías)
    cnt = {}
    for xy, *_ in ways:
        for i, p in enumerate(xy):
            k = (round(p[0], 2), round(p[1], 2))
            cnt[k] = cnt.get(k, 0) + (1 if 0 < i < len(xy) - 1 else 2)
    rows = []
    for xy, typ, name, bt, w in ways:
        cur = [xy[0]]
        for p in xy[1:]:
            cur.append(p)
            if cnt.get((round(p[0], 2), round(p[1], 2)), 0) >= 2 and p is not xy[-1] and len(cur) >= 2:
                rows.append({"geometry": LineString(cur), "TYPE_ES": typ, "NAME": name, "BRIDGE_TUNNEL": bt,
                             "WIDTH": w, "SRC": "osm-" + typ})
                cur = [p]
        if len(cur) >= 2:
            rows.append({"geometry": LineString(cur), "TYPE_ES": typ, "NAME": name, "BRIDGE_TUNNEL": bt,
                         "WIDTH": w, "SRC": "osm-" + typ})
    return rows


def _rows_idena(nav):
    if nav is None or nav.roads is None or nav.roads.empty or "TIPO" not in nav.roads:
        return []
    r = nav.roads[nav.roads.TIPO.astype(str) == "Camino"]
    rows = []
    for _, x in r.iterrows():
        for g in _explode(x.geometry):
            rows.append({"geometry": LineString([(c[0], c[1]) for c in g.coords]), "TYPE_ES": "Camino",
                         "NAME": "Camino", "BRIDGE_TUNNEL": "0", "WIDTH": W_PATH, "SRC": "idena-Camino"})
    return rows


def _subtract(rows, base_geoms, base_buf, log=None, label=""):
    """Quita de cada fila las partes a menos de base_buf[i] de base_geoms[i]."""
    if not base_geoms or not rows:
        return rows
    tree = STRtree(base_geoms)
    bufs = {}
    mx = float(max(base_buf))
    out = []
    removed = 0.0
    for r in rows:
        g = r["geometry"]
        idx = tree.query(g.buffer(mx))
        if len(idx) == 0:
            out.append(r)
            continue
        polys = []
        for i in idx:
            i = int(i)
            if i not in bufs:
                bufs[i] = base_geoms[i].buffer(base_buf[i], resolution=4)
            polys.append(bufs[i])
        d = g.difference(unary_union(polys))
        L0 = g.length
        keep = [x for x in _explode(d) if x.length >= MIN_PIECE]
        removed += L0 - sum(x.length for x in keep)
        for x in keep:
            out.append({**r, "geometry": x})
    if log:
        log(f"  {label}: quitados {removed / 1000:.1f} km solapados")
    return out


def extra_lines(v, pathw_path, bbox, inner, osm, nav, log=_log):
    """v: viales (Calle/Carretera) ya recortados (GeoDataFrame de LineString). Devuelve v + líneas nuevas, nodado."""
    b5 = _rows_b5m(pathw_path, bbox)
    os_ = _rows_osm(osm) if osm else []
    idn = _rows_idena(nav)
    tren = [r for r in b5 if r["TYPE_ES"] == "Tren"]
    bide_b5 = [r for r in b5 if r["TYPE_ES"] == "Bidegorri"]
    send_b5 = [r for r in b5 if r["TYPE_ES"] == "Camino"]
    bide_os = [r for r in os_ if r["TYPE_ES"] == "Bidegorri"]
    cam_os = [r for r in os_ if r["TYPE_ES"] == "Camino"]
    L = lambda rs: sum(r["geometry"].length for r in rs) / 1000
    log(f"Fuentes: tren {L(tren):.1f} km, bidegorri b5m {L(bide_b5):.1f} + OSM {L(bide_os):.1f} km, "
        f"caminos OSM {L(cam_os):.1f} + senderos b5m {L(send_b5):.1f} + IDENA {L(idn):.1f} km")

    # 1) quitar lo que ya es calle o carretera
    rg = list(v.geometry.values)
    rw = [float(w) / 2 + 2.0 for w in v["WIDTH"].values]
    # bidegorris: sólo se quita lo que va POR la calzada (carril pintado); los que van al lado se quedan
    rwb = [float(w) / 2 + 0.5 for w in v["WIDTH"].values]
    bide_b5 = _subtract(bide_b5, rg, rwb, log, "bidegorris b5m sobre calles")
    bide_os = _subtract(bide_os, rg, rwb, log, "bidegorris OSM sobre calles")
    bide_os = _subtract(bide_os, [r["geometry"] for r in bide_b5], [6.0] * len(bide_b5), log, "bidegorris OSM ya en b5m")
    bide = bide_b5 + bide_os
    cam = cam_os + idn
    cam = _subtract(cam, rg, [w + 0.5 for w in rw], log, "caminos sobre calles")
    cam = _subtract(cam, [r["geometry"] for r in bide], [4.0] * len(bide), log, "caminos sobre bidegorris")
    send_b5 = _subtract(send_b5, rg, [w + 0.5 for w in rw], log, "senderos b5m sobre calles")
    send_b5 = _subtract(send_b5, [r["geometry"] for r in cam + bide], [10.0] * len(cam + bide), log,
                        "senderos b5m ya en OSM")
    cam += send_b5
    # 2) recorte
    def clip(rows):
        out = []
        for r in rows:
            for g in _explode(r["geometry"].intersection(inner)):
                if g.length >= 3.0:
                    out.append({**r, "geometry": g})
        return out
    tren, bide, cam = clip(tren), clip(bide), clip(cam)
    log(f"Tras quitar duplicados: tren {L(tren):.1f} km, bidegorris {L(bide):.1f} km, caminos {L(cam):.1f} km")

    # 3) nodado: vías existentes + nuevas (el tren va aparte: sólo se empalma consigo mismo, ya viene nodado)
    base = [{"geometry": g, "TYPE_ES": t, "NAME": n, "BRIDGE_TUNNEL": str(bt) if bt is not None else "0",
             "WIDTH": float(w), "SRC": "road"}
            for g, t, n, bt, w in zip(v.geometry.values, v.TYPE_ES.values, v.NAME.values, v.BRIDGE_TUNNEL.values,
                                      v.WIDTH.values)]
    allr = base + bide + cam
    nb = len(base)
    geoms = [r["geometry"] for r in allr]
    tree = STRtree(geoms)
    splits = [[] for _ in allr]
    ends = {}
    for i, g in enumerate(geoms):
        for p in (g.coords[0], g.coords[-1]):
            ends.setdefault((round(p[0] * 2) / 2, round(p[1] * 2) / 2), []).append(i)
    n_snap = n_cross = 0
    newc = {}
    for i in range(nb, len(allr)):
        g = geoms[i]
        c = list(g.coords)
        changed = False
        for side in (0, -1):
            p = c[side]
            k = (round(p[0] * 2) / 2, round(p[1] * 2) / 2)
            if len(ends.get(k, [])) > 1:
                continue          # ya empalma con otra línea
            P = Point(p)
            best = None
            for j in tree.query(P.buffer(SNAP)):
                j = int(j)
                if j == i:
                    continue
                if allr[j]["BRIDGE_TUNNEL"] in ("3", "4") and j < nb:
                    continue
                d = geoms[j].distance(P)
                if d <= SNAP and (best is None or d < best[0]):
                    best = (d, j)
            if best is None:
                continue
            j = best[1]
            gj = geoms[j]
            s = gj.project(P)
            if s < 2.0:
                q = gj.coords[0]
            elif s > gj.length - 2.0:
                q = gj.coords[-1]
            else:
                q = gj.interpolate(s).coords[0]
                splits[j].append(s)
            if math.dist(q, p) > 0.05:
                if side == 0:
                    c.insert(0, (q[0], q[1]))
                else:
                    c.append((q[0], q[1]))
                changed = True
            n_snap += 1
        if changed:
            newc[i] = c
    for i, c in newc.items():
        geoms[i] = LineString(c)
        allr[i]["geometry"] = geoms[i]
    tree = STRtree(geoms)
    # cruces a nivel (nunca con puentes, túneles ni pasos inferiores de otra vía)
    for i in range(nb, len(allr)):
        if allr[i]["BRIDGE_TUNNEL"] != "0":
            continue
        g = geoms[i]
        for j in tree.query(g):
            j = int(j)
            if j == i or (j > i and j >= nb) or allr[j]["BRIDGE_TUNNEL"] not in ("0",):
                continue
            if j >= nb and allr[j]["SRC"][:3] == allr[i]["SRC"][:3] == "osm":
                continue      # OSM ya comparte nodos en los cruces de verdad
            x = g.intersection(geoms[j])
            if x.is_empty:
                continue
            pts = [x] if isinstance(x, Point) else [q for q in getattr(x, "geoms", []) if isinstance(q, Point)]
            for q in pts:
                si, sj = g.project(q), geoms[j].project(q)
                if 1.0 < si < g.length - 1.0:
                    splits[i].append(si)
                if 1.0 < sj < geoms[j].length - 1.0:
                    splits[j].append(sj)
                n_cross += 1
    out = []
    n_cut = 0
    for i, r in enumerate(allr):
        g = geoms[i]
        ss = sorted(set(round(s, 2) for s in splits[i]))
        cuts = [0.0]
        for s in ss:
            if s - cuts[-1] > 0.6 and g.length - s > 0.6:
                cuts.append(s)
        cuts.append(g.length)
        if len(cuts) == 2:
            out.append(r)
            continue
        for a, b in zip(cuts[:-1], cuts[1:]):
            seg = substring(g, a, b)
            if isinstance(seg, LineString) and seg.length > 0.3:
                out.append({**r, "geometry": seg})
                n_cut += 1
    log(f"Empalmes: {n_snap}, cruces a nivel: {n_cross}, trozos tras cortar: {n_cut}")
    out += tren
    g = gpd.GeoDataFrame(out, geometry="geometry", crs=v.crs)
    return g


# --------------------------------------------------------------------------- estaciones, cimas, nombres
def stations(osm, origin):
    T = _tr()
    seen = {}
    for e in osm["elements"]:
        t = e.get("tags", {})
        if e["type"] != "node" or t.get("railway") not in ("station", "halt", "stop"):
            continue
        name = t.get("name")
        if not name:
            continue
        x, y = T.transform(e["lon"], e["lat"])
        pri = 0 if t.get("railway") in ("station", "halt") else 1
        if name not in seen or pri < seen[name][0]:
            seen[name] = (pri, x - origin[0], y - origin[1])
    return [{"name": n, "x": round(v[1], 1), "z": round(v[2], 1)} for n, v in seen.items()]


def _peak_name(n):
    n = n.split(" / ")[0]
    n = re.sub(r",\s*(tontorra|mendia|gailurra|kaskoa)$", "", n.strip())
    return n.strip()


def peaks(topon_path, bbox, origin, Hs, osm, landmarks=None, log=_log):
    """Cimas con nombre: b5m TOPON (cima / monte, en euskera), lugares ya cargados (montes de Navarra de IDENA)
    y OSM natural=peak. Devuelve [{name, x, z, y}] en coordenadas locales."""
    t = gpd.read_file(topon_path, bbox=bbox, ignore_geometry=True)
    cand = []
    for _, r in t.iterrows():
        ty = (r.TYPE_ES or "").strip()
        if ty not in ("cima", "monte"):
            continue
        nm = (r.NAME_EU or r.NAME or "").strip()
        if not nm or r.X_CEN_ETRS89 is None:
            continue
        cand.append((0 if ty == "cima" else 1, _peak_name(nm), float(r.X_CEN_ETRS89) - origin[0],
                     float(r.Y_CEN_ETRS89) - origin[1]))
    for l in landmarks or []:
        if l.get("type") in ("cima", "monte"):
            cand.append((2, _peak_name(l["name"]), float(l["x"]), float(l["z"])))
    T = _tr()
    for e in (osm or {}).get("elements", []):
        tg = e.get("tags", {})
        if e["type"] == "node" and tg.get("natural") in ("peak", "hill") and tg.get("name"):
            x, y = T.transform(e["lon"], e["lat"])
            cand.append((3, _peak_name(tg["name"]), x - origin[0], y - origin[1]))
    cand.sort(key=lambda c: c[0])
    out = []
    sx, sz = bbox[2] - bbox[0], bbox[3] - bbox[1]
    for pr, nm, lx, lz in cand:
        if not nm or not (40 < lx < sx - 40 and 40 < lz < sz - 40):
            continue
        dup = False
        for o in out:
            d = math.hypot(o["x"] - lx, o["z"] - lz)
            if (d < 400 and o["name"].lower() == nm.lower()) or d < 60:
                dup = True
                break
        if dup:
            continue
        out.append({"name": nm, "x": round(lx, 1), "z": round(lz, 1), "y": round(float(Hs(lx, lz)[0]), 1)})
    log(f"Cimas con nombre: {len(out)}")
    return out


def _house_name(nm):
    if not nm:
        return ""
    nm = nm.split(";")[0].split(" / ")[0].split(" - ")[0].strip()
    nm = re.sub(r",\s*(baserria|etxea|borda|eraikina|txabola|nekazalturismoa|indarretxea|eliza|baseliza|"
                r"ermita|caserío|casa|agroturismo|landetxea|etxetxoa|txabola)$", "", nm, flags=re.I)
    return nm.strip()


def building_names(topon_path, bbox, origin, buildings, osm, edges, Hs, log=_log):
    """Placas con el nombre de la casa: b5m BU (NAME_BUILDING_EU, en euskera como las placas reales), si no TOPON
    'edificio' y OSM name / addr:housename. Una placa por nombre (el edificio mayor) en la pared que mira a la
    calle o camino más cercano. Añade a cada edificio con placa: "pn" (texto) y "ps" [x, z, y, nx, nz, ancho]."""
    polys = []
    for b in buildings:
        v = np.array(b["v"]).reshape(-1, 2)
        n0 = b["r"][0]
        polys.append(Polygon(v[:n0]).buffer(0) if n0 >= 3 else Point(v[0]))
    tree = STRtree(polys)
    names = [_house_name(b.get("_eu") or b.get("n") or "") for b in buildings]
    # nombres de TOPON / OSM para los edificios que no traen nombre
    pts = []
    t = gpd.read_file(topon_path, bbox=bbox, ignore_geometry=True)
    for _, r in t.iterrows():
        if (r.TYPE_ES or "").strip() != "edificio" or r.X_CEN_ETRS89 is None:
            continue
        nm = _house_name(r.NAME_BUILDING_EU or r.NAME_EU or r.NAME or "")
        if nm:
            pts.append((nm, float(r.X_CEN_ETRS89) - origin[0], float(r.Y_CEN_ETRS89) - origin[1]))
    T = _tr()
    for e in (osm or {}).get("elements", []):
        tg = e.get("tags", {})
        if "building" not in tg:
            continue
        nm = _house_name(tg.get("name") or tg.get("addr:housename") or "")
        if not nm:
            continue
        if e["type"] == "way" and "geometry" in e:
            xy = np.array([T.transform(p["lon"], p["lat"]) for p in e["geometry"]])
            cx, cy = xy[:, 0].mean(), xy[:, 1].mean()
        elif e["type"] == "node":
            cx, cy = T.transform(e["lon"], e["lat"])
        else:
            continue
        pts.append((nm, cx - origin[0], cy - origin[1]))
    added = 0
    for nm, x, z in pts:
        P = Point(x, z)
        best = None
        for j in tree.query(P.buffer(15.0)):
            j = int(j)
            d = polys[j].distance(P)
            if d <= 15.0 and (best is None or d < best[0]):
                best = (d, j)
        if best is not None and not names[best[1]]:
            names[best[1]] = nm
            added += 1
    # una placa por nombre y grupo (el edificio de mayor planta)
    groups = {}
    for i, nm in enumerate(names):
        if not nm or len(nm) < 2:
            continue
        c = polys[i].centroid
        key = None
        for k, lst in groups.get(nm.lower(), {}).items():
            if math.hypot(lst[0][1] - c.x, lst[0][2] - c.y) < 90:
                key = k
                break
        g = groups.setdefault(nm.lower(), {})
        if key is None:
            key = len(g)
            g[key] = []
        g[key].append((i, c.x, c.y))
    # puntos de las vías (para elegir la fachada)
    rp = np.concatenate([e["xz"] for e in edges])
    from scipy.spatial import cKDTree
    rtree = cKDTree(rp)
    n_pl = 0
    for nm_l, g in groups.items():
        for lst in g.values():
            i = max(lst, key=lambda q: polys[q[0]].area)[0]
            b = buildings[i]
            if b.get("l", 0) > 0 or b.get("k", 0) != 0:
                continue
            v = np.array(b["v"]).reshape(-1, 2)[:b["r"][0]]
            res = plate_facade(v, rtree, rp, Hs, float(b["h"]))
            if res is None:
                continue
            b["pn"] = names[i][:26]
            b["ps"] = res
            n_pl += 1
    log(f"Placas con nombre de edificio: {n_pl} (nombres de TOPON/OSM añadidos: {added})")
    return n_pl


# --------------------------------------------------------------------------- términos municipales y barrios
def _rel_polys(rel, T):
    outer = [m for m in rel.get("members", []) if m.get("type") == "way" and m.get("role") in ("outer", "")
             and "geometry" in m]
    inner = [m for m in rel.get("members", []) if m.get("type") == "way" and m.get("role") == "inner"
             and "geometry" in m]
    def lines(ms):
        out = []
        for m in ms:
            xy = [T.transform(p["lon"], p["lat"]) for p in m["geometry"] if p]
            if len(xy) >= 2:
                out.append(LineString(xy))
        return out
    po = list(polygonize(unary_union(lines(outer))))
    if not po:
        return None
    g = unary_union(po)
    pi = list(polygonize(unary_union(lines(inner)))) if inner else []
    if pi:
        g = g.difference(unary_union(pi))
    return g


def admin_areas(cache, bbox, origin, log=_log):
    """Lista de áreas: {name, level (8 municipio, 9/10 barrio), prov, rings: [[x,z,...], ...]} recortadas."""
    path = os.path.join(cache, OSM_ADMIN)
    if os.path.exists(path):
        with open(path, encoding="utf8") as f:
            areas = json.load(f)
    else:
        s, w, n, e = ll_bbox(bbox)
        q = (f'[out:json][timeout:180];relation["boundary"="administrative"]["admin_level"~"^(6|8|9|10)$"]'
             f'({s:.4f},{w:.4f},{n:.4f},{e:.4f});out geom;')
        d = overpass(q, log)
        T = _tr()
        clipb = box(*bbox).buffer(200)
        provs = []
        raw = []
        for r in d["elements"]:
            if r["type"] != "relation":
                continue
            tg = r.get("tags", {})
            lvl = int(tg.get("admin_level", "0"))
            g = _rel_polys(r, T)
            if g is None:
                continue
            g = g.intersection(clipb).simplify(6.0)
            if g.is_empty:
                continue
            name = tg.get("name:es") if lvl == 6 and tg.get("name:es") else tg.get("name", "")
            if lvl == 6:
                provs.append((name, g))
            else:
                raw.append((name, lvl, g))
        areas = []
        for name, lvl, g in raw:
            c = g.representative_point()
            prov = next((pn for pn, pg in provs if pg.contains(c)), "")
            prov = "Navarra" if prov.startswith("Navarra") else prov
            rings = []
            for pg in (g.geoms if isinstance(g, MultiPolygon) else [g]):
                if not isinstance(pg, Polygon):
                    continue
                rings.append([round(v, 1) for xy in pg.exterior.coords for v in xy])
            areas.append({"name": name, "level": lvl, "prov": prov, "rings": rings})
        with open(path, "w", encoding="utf8") as f:
            json.dump(areas, f, ensure_ascii=False, separators=(",", ":"))
    out = []
    for a in areas:
        rings = [[round(v - origin[k % 2], 1) for k, v in enumerate(r)] for r in a["rings"]]
        # JsonUtility de Unity no lee listas de listas: anillos concatenados + nº de vértices de cada uno
        out.append({"name": a["name"].replace("Donostia / San Sebastián", "Donostia"), "level": a["level"],
                    "prov": a["prov"], "r": [len(r) // 2 for r in rings], "p": [v for r in rings for v in r]})
    log(f"Términos municipales: {sum(1 for a in out if a['level'] == 8)}, barrios: {sum(1 for a in out if a['level'] > 8)}")
    return out
