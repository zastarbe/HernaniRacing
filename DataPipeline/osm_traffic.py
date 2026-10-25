#!/usr/bin/env python3
"""
Regulación del tráfico a partir de OpenStreetMap (© colaboradores de OpenStreetMap, licencia ODbL).

b5m da la geometría real de los viales pero no su sentido de circulación ni la señalización. Este paso
descarga (Overpass API) las vías y nodos de OSM de la zona y los empareja con los tramos de b5m:

  * sentido de cada tramo: ow = 0 doble sentido, 1 sólo a->b, -1 sólo b->a (oneway, rotondas, autovías)
  * carriles por sentido (lanes) y si el tramo es parte de una rotonda (rb)
  * nodos con semáforo (nsig = 1) -> controlador de fases en Unity
  * señales STOP / CEDA EL PASO en la entrada del tramo al cruce (signs: tipo, tramo, extremo)
  * pasos de peatones (crossings: tramo, distancia desde a, semáforo)

Se puede ejecutar solo, sobre un roads.json ya generado:
    python osm_traffic.py [--roads ../Assets/HernaniRacing/Resources/HernaniData/roads.json] [--refresh]
o desde build_hernani_data.py (se llama al final).
"""
import argparse
import json
import math
import os
import sys
import urllib.parse
import urllib.request

import numpy as np
from scipy.spatial import cKDTree

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
DEFAULT_ROADS = os.path.join(HERE, "..", "Assets", "HernaniRacing", "Resources", "HernaniData", "roads.json")
OVERPASS = ["https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter",
            "https://overpass.private.coffee/api/interpreter", "https://maps.mail.ru/osm/tools/overpass/api/interpreter"]
DRIVABLE = {"motorway", "trunk", "primary", "secondary", "tertiary", "unclassified", "residential", "living_street",
            "service", "road", "motorway_link", "trunk_link", "primary_link", "secondary_link", "tertiary_link"}
ATTRIBUTION = "Sentidos de circulación, semáforos, señales y pasos de peatones: © colaboradores de OpenStreetMap (ODbL)"

SIGN_STOP, SIGN_GIVEWAY = 1, 2


def log(*a):
    print("[osm]", *a, flush=True)


def overpass_query(s, w, n, e):
    bb = f"{s},{w},{n},{e}"
    return (f"[out:json][timeout:180];("
            f"way[highway~\"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|road)(_link)?$\"]({bb});"
            f"node[highway~\"^(traffic_signals|stop|give_way|crossing|mini_roundabout)$\"]({bb});"
            f");out body;>;out skel qt;")


def fetch_osm(bbox_ll, refresh=False):
    tag = "_".join(f"{v:.4f}" for v in bbox_ll)
    path = os.path.join(CACHE, f"osm_hernani_{tag}.json")
    if os.path.exists(path) and not refresh and os.path.getsize(path) > 1000:
        return json.load(open(path, encoding="utf8"))
    q = overpass_query(*bbox_ll)
    last = None
    import time
    for url in OVERPASS * 2:
        try:
            log("Descargando OSM:", url)
            data = urllib.parse.urlencode({"data": q}).encode()
            req = urllib.request.Request(url, data=data, headers={"User-Agent": "HernaniRacing/1.0 (data pipeline)"})
            with urllib.request.urlopen(req, timeout=180) as r:
                raw = r.read()
            os.makedirs(CACHE, exist_ok=True)
            with open(path, "wb") as f:
                f.write(raw)
            return json.loads(raw)
        except Exception as ex:  # noqa: BLE001
            last = ex
            log("  fallo:", ex)
            time.sleep(5)
    raise RuntimeError(f"No se pudo descargar OSM: {last}")


def oneway_of(tags):
    hw = tags.get("highway", "")
    ow = tags.get("oneway", "")
    if ow in ("yes", "true", "1"):
        return 1
    if ow == "-1" or ow == "reverse":
        return -1
    if ow == "no":
        return 0
    if tags.get("junction") in ("roundabout", "circular") or hw in ("motorway", "motorway_link"):
        return 1
    return 0


def lanes_of(tags, ow):
    try:
        n = int(str(tags.get("lanes", "")).split(";")[0])
    except ValueError:
        n = 0
    if n <= 0:
        return 2 if tags.get("highway") == "motorway" else 1
    return max(1, n if ow else n // 2)


def polyline(edge):
    p = np.array(edge["p"], float).reshape(-1, 3)
    return p[:, [0, 2]]


def sample_along(xz, step):
    seg = np.diff(xz, axis=0)
    ln = np.linalg.norm(seg, axis=1)
    cum = np.concatenate([[0], np.cumsum(ln)])
    L = cum[-1]
    out = []
    if L < 1e-3:
        return out, L
    for s in np.arange(step / 2, L, step):
        i = min(np.searchsorted(cum, s, side="right") - 1, len(seg) - 1)
        t = (s - cum[i]) / max(ln[i], 1e-9)
        out.append((s, xz[i] + seg[i] * t, seg[i] / max(ln[i], 1e-9)))
    return out, L


def project_s(xz, pt):
    """Distancia a lo largo de la polilínea del punto más cercano, y distancia lateral."""
    best = (1e18, 0.0)
    acc = 0.0
    for i in range(len(xz) - 1):
        a, b = xz[i], xz[i + 1]
        d = b - a
        L = float(np.hypot(*d))
        if L < 1e-9:
            continue
        t = float(np.clip(np.dot(pt - a, d) / (L * L), 0, 1))
        q = a + d * t
        dist = float(np.hypot(*(pt - q)))
        if dist < best[0]:
            best = (dist, acc + t * L)
        acc += L
    return best[1], best[0], acc


def augment(roads, osm, origin_e, origin_n):
    from pyproj import Transformer
    tr = Transformer.from_crs("EPSG:4326", "EPSG:25830", always_xy=True)
    nodes_ll = {}
    tagged = []
    ways = []
    for el in osm["elements"]:
        if el["type"] == "node":
            nodes_ll[el["id"]] = (el["lon"], el["lat"])
            if el.get("tags"):
                tagged.append(el)
        elif el["type"] == "way":
            ways.append(el)
    ids = list(nodes_ll)
    lon = np.array([nodes_ll[i][0] for i in ids])
    lat = np.array([nodes_ll[i][1] for i in ids])
    E, N = tr.transform(lon, lat)
    xy = {i: (e - origin_e, n - origin_n) for i, e, n in zip(ids, E, N)}

    # segmentos OSM conducibles
    segA, segB, segInfo = [], [], []
    for w in ways:
        tags = w.get("tags", {})
        if tags.get("highway") not in DRIVABLE or tags.get("area") == "yes":
            continue
        ow = oneway_of(tags)
        info = (ow, lanes_of(tags, ow), 1 if tags.get("junction") in ("roundabout", "circular") else 0)
        pts = [xy[n] for n in w["nodes"] if n in xy]
        for a, b in zip(pts[:-1], pts[1:]):
            segA.append(a); segB.append(b); segInfo.append(info)
    segA = np.array(segA); segB = np.array(segB)
    mids = (segA + segB) / 2
    seglen = np.linalg.norm(segB - segA, axis=1)
    tree = cKDTree(mids)
    maxhalf = float(seglen.max() / 2) if len(seglen) else 0
    log(f"{len(ways)} vías OSM, {len(segA)} segmentos conducibles")

    edges = roads["edges"]
    nodes = roads["nodes"]
    polys = [polyline(e) for e in edges]
    stats = {"ow": 0, "rb": 0, "matched": 0}
    for ei, e in enumerate(edges):
        if e.get("type") not in ("Calle", "Carretera"):
            # v16: caminos, bidegorris y tren no tienen sentido único, carriles ni rotondas
            e["ow"], e["lanes"], e["rb"] = 0, 1, 0
            continue
        xz = polys[ei]
        samples, L = sample_along(xz, 4.0)
        # se evitan los extremos (zona de cruce) salvo en tramos cortos
        inner = [s for s in samples if 5.0 < s[0] < L - 5.0] or samples
        votes, lanes, rbv, hits = [], [], [], 0
        for s, p, d in inner:
            cand = tree.query_ball_point(p, 8.0 + maxhalf)
            best, bd = None, 9.0
            for k in cand:
                a, b = segA[k], segB[k]
                v = b - a
                ll = seglen[k]
                if ll < 1e-6:
                    continue
                t = np.clip(np.dot(p - a, v) / (ll * ll), 0, 1)
                dist = float(np.hypot(*(p - (a + v * t))))
                c = float(np.dot(v / ll, d))
                if dist < bd and abs(c) > 0.8:
                    best, bd = (k, c), dist
            if best is None:
                continue
            hits += 1
            k, c = best
            ow, ln, rb = segInfo[k]
            votes.append(0 if ow == 0 else (ow if c > 0 else -ow))
            lanes.append(ln); rbv.append(rb)
        e_ow, e_ln, e_rb = 0, 1, 0
        if inner and hits >= max(1, 0.5 * len(inner)):
            stats["matched"] += 1
            vals, cnt = np.unique(votes, return_counts=True)
            e_ow = int(vals[np.argmax(cnt)])
            if (np.array(votes) == e_ow).mean() < 0.6:
                e_ow = 0
            e_ln = int(np.median(lanes))
            e_rb = int(np.mean(rbv) > 0.5)
        if e["width"] >= 10 and e_ow == 0 and e["type"] == "Carretera" and not inner:
            pass
        e["ow"] = e_ow
        e["lanes"] = max(1, min(3, e_ln))
        e["rb"] = e_rb
        stats["ow"] += e_ow != 0
        stats["rb"] += e_rb
    log(f"tramos emparejados {stats['matched']}/{len(edges)}, sentido único {stats['ow']}, rotonda {stats['rb']}")

    # ---- nodos etiquetados
    node_xy = np.array([[n["x"], n["z"]] for n in nodes])
    ntree = cKDTree(node_xy)
    deg = np.zeros(len(nodes), int)
    for e in edges:
        if e.get("type") in ("Calle", "Carretera"):
            deg[e["a"]] += 1; deg[e["b"]] += 1
    # tramos por proximidad (muestras cada 3 m)
    smp, smp_e = [], []
    for ei, xz in enumerate(polys):
        ss, _ = sample_along(xz, 3.0)
        for s, p, d in ss:
            smp.append(p); smp_e.append(ei)
        smp.append(xz[0]); smp_e.append(ei); smp.append(xz[-1]); smp_e.append(ei)
    etree = cKDTree(np.array(smp))

    def nearest_edge(p, rmax):
        best = None
        for k in etree.query_ball_point(p, rmax + 3):
            ei = smp_e[k]
            if edges[ei].get("type") not in ("Calle", "Carretera"):
                continue
            s, dist, L = project_s(polys[ei], p)
            if dist <= rmax and (best is None or dist < best[1]):
                best = (ei, dist, s, L)
        return best

    nsig = [0] * len(nodes)
    signs, crossings = [], []
    sig_junction, sig_cross = 0, 0
    for el in tagged:
        hw = el["tags"].get("highway")
        p = np.array(xy[el["id"]])
        if hw == "traffic_signals":
            d, ni = ntree.query(p)
            if d < 35 and deg[ni] >= 3:
                nsig[ni] = 1; sig_junction += 1
            else:
                ne = nearest_edge(p, 6.0)
                if ne:
                    crossings.append({"e": ne[0], "s": round(ne[2], 1), "sig": 1}); sig_cross += 1
        elif hw in ("stop", "give_way"):
            ne = nearest_edge(p, 7.0)
            if not ne:
                continue
            ei, dist, s, L = ne
            end = 0 if s < L / 2 else 1
            from_end = s if end == 0 else L - s
            ni = edges[ei]["a"] if end == 0 else edges[ei]["b"]
            if from_end > 45 or deg[ni] < 3:
                continue
            signs.append({"t": SIGN_STOP if hw == "stop" else SIGN_GIVEWAY, "e": ei, "end": end})
        elif hw == "crossing":
            ne = nearest_edge(p, 4.5)
            if not ne:
                continue
            ei, dist, s, L = ne
            if s < 3.0 or s > L - 3.0:
                continue
            sig = 1 if el["tags"].get("crossing") == "traffic_signals" else 0
            crossings.append({"e": ei, "s": round(s, 1), "sig": sig})
        elif hw == "mini_roundabout":
            d, ni = ntree.query(p)
            if d < 20:
                nsig[ni] = 2

    # deduplicación
    seen = set()
    signs_u = []
    for sg in signs:
        k = (sg["e"], sg["end"])
        if k in seen:
            continue
        seen.add(k); signs_u.append(sg)
    crossings.sort(key=lambda c: (c["e"], c["s"]))
    cross_u = []
    for c in crossings:
        if cross_u and cross_u[-1]["e"] == c["e"] and abs(cross_u[-1]["s"] - c["s"]) < 8:
            cross_u[-1]["sig"] |= c["sig"]
            continue
        cross_u.append(c)
    # en nodos de rotonda: marcados con 2 para que Unity dé prioridad al anillo
    for e in edges:
        if e.get("rb"):
            for ni in (e["a"], e["b"]):
                if nsig[ni] == 0:
                    nsig[ni] = 2
    roads["nsig"] = nsig
    roads["signs"] = signs_u
    roads["crossings"] = cross_u
    log(f"semáforos en cruce {sig_junction} (nodos {sum(1 for v in nsig if v == 1)}), en paso {sig_cross}; "
        f"señales {len(signs_u)} (STOP {sum(1 for s in signs_u if s['t'] == 1)}, CEDA {sum(1 for s in signs_u if s['t'] == 2)}); "
        f"pasos de peatones {len(cross_u)}")
    return roads


def bbox_ll(origin_e, origin_n, sx, sz=None):
    from pyproj import Transformer
    sz = sx if sz is None else sz
    tr = Transformer.from_crs("EPSG:25830", "EPSG:4326", always_xy=True)
    lons, lats = tr.transform([origin_e, origin_e + sx, origin_e, origin_e + sx],
                              [origin_n, origin_n, origin_n + sz, origin_n + sz])
    return (round(min(lats) - 0.001, 4), round(min(lons) - 0.001, 4), round(max(lats) + 0.001, 4), round(max(lons) + 0.001, 4))


def run(roads_path, refresh=False):
    meta_path = os.path.join(os.path.dirname(roads_path), "meta.json")
    meta = json.load(open(meta_path, encoding="utf8"))
    roads = json.load(open(roads_path, encoding="utf8"))
    sx = meta.get("sizeX", meta["size"]); sz = meta.get("sizeZ", meta["size"])
    osm = fetch_osm(bbox_ll(meta["originE"], meta["originN"], sx, sz), refresh)
    augment(roads, osm, meta["originE"], meta["originN"])
    with open(roads_path, "w", encoding="utf8") as f:
        json.dump(roads, f, ensure_ascii=False, separators=(",", ":"))
    att = meta.setdefault("attribution", [])
    if ATTRIBUTION not in att:
        att.append(ATTRIBUTION)
    with open(meta_path, "w", encoding="utf8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--roads", default=DEFAULT_ROADS)
    ap.add_argument("--refresh", action="store_true", help="volver a descargar OSM")
    a = ap.parse_args()
    run(os.path.abspath(a.roads), a.refresh)
