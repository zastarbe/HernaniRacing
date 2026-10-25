#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Hernani Racing - datos de Navarra para la parte del rectángulo que cae fuera de Gipuzkoa
=====================================================================================

b5m sólo tiene datos de Gipuzkoa. Desde la v15 el mapa llega hasta el límite sur de Hernani y dentro del rectángulo
entra un trozo de Navarra (Arano y una esquina de Goizueta). Para esa parte se usan los datos abiertos del
Gobierno de Navarra, servidos por IDENA (la IDE del SITNA) por WFS:

  * DIADMI_Pol_Municipio         municipios de Navarra  -> zona de Navarra dentro del rectángulo
  * CARBTA_Lin_RedViaria         red viaria BTA 1:5.000 (ejes: carreteras, vías urbanas, pistas)
  * CARBTA_Pol_EdiPobCons        edificaciones BTA 1:5.000 (huella, tipo y nombre)
  * DIRECC_Pol_Edifaltura        alturas de edificación (catastro de Navarra) -> altura de cada edificio
  * HIDROG_Lin_CorrienteNatural  ríos y regatas 1:5.000
  * TOPONI_Txt_Toponimos         toponimia oficial (núcleos, barrios, montes...)

Licencia: CC BY 4.0. "Fuente de los datos: SITNA – Gobierno de Navarra" (https://idena.navarra.es).

Cada capa se convierte a las mismas columnas que los GPKG de b5m (TYPE_ES, NAME, BRIDGE_TUNNEL, AVERAGE_HEIGHT...),
se recorta a la zona de Navarra y se une a los datos de b5m. Las calles se empalman en la muga: cada eje de Navarra que
llega al límite se engancha al final de calle de b5m más cercano (o, si no hay, se parte la calle de b5m en ese punto).
"""
import json
import os
import re
import urllib.parse
import urllib.request

import numpy as np
import pandas as pd
import geopandas as gpd
import shapely
from shapely.geometry import box, LineString, Point
from shapely.ops import substring

WFS = "https://idena.navarra.es/ogc/wfs"
UA = "HernaniRacing/1.0 (data pipeline)"
CRS = "EPSG:25830"
ATTRIBUTION = "Navarra (Arano, Goizueta): viales, edificios, alturas, hidrografía y toponimia - Fuente de los datos: SITNA – Gobierno de Navarra (IDENA, CC BY 4.0)"

ROAD_TYPES = {
    "Carretera de calzada única convencional": "Carretera",
    "Carretera de calzada única": "Carretera",
    "Carretera de calzadas separadas": "Carretera",
    "Vía urbana genérica": "Calle",
    "Pista pavimentada": "Calle",
    "Pista no pavimentada": "Calle",
}
GENERIC_NAMES = re.compile(r"^(Carretera calzada|CALLE DESCONOCIDA)", re.I)
SNAP_DIST = 30.0     # m: distancia máxima para empalmar en la muga con una calle de b5m


def _log(*a):
    print("[navarra]", *a, flush=True)


def fetch_layer(layer, bbox, cache_dir, cql=None, refresh=False):
    """GetFeature (GeoJSON) por páginas de 1000. Se guarda en cache/idena_<capa>_<bbox>.json."""
    os.makedirs(cache_dir, exist_ok=True)
    tag = "_".join(str(int(v)) for v in bbox)
    path = os.path.join(cache_dir, f"idena_{layer}_{tag}.json")
    if os.path.exists(path) and not refresh:
        with open(path, encoding="utf-8") as f:
            feats = json.load(f)
    else:
        flt = f"BBOX(the_geom,{bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]},'{CRS}')"
        if cql:
            flt += " AND " + cql
        feats, start = [], 0
        while True:
            q = urllib.parse.urlencode({
                "service": "WFS", "version": "2.0.0", "request": "GetFeature", "typeNames": "IDENA:" + layer,
                "outputFormat": "application/json", "srsName": CRS, "CQL_FILTER": flt,
                "count": 1000, "startIndex": start, "sortBy": "id"})
            last = None
            for _ in range(4):
                try:
                    req = urllib.request.Request(WFS + "?" + q, headers={"User-Agent": UA})
                    with urllib.request.urlopen(req, timeout=180) as r:
                        d = json.loads(r.read().decode("utf-8"))
                    break
                except Exception as ex:  # noqa: BLE001
                    last = ex
                    d = None
            if d is None:
                # algunas capas no admiten sortBy por "id": sin ordenar
                q = q.replace("&sortBy=id", "")
                req = urllib.request.Request(WFS + "?" + q, headers={"User-Agent": UA})
                with urllib.request.urlopen(req, timeout=180) as r:
                    d = json.loads(r.read().decode("utf-8"))
            page = d.get("features", [])
            feats += page
            if len(page) < 1000:
                break
            start += len(page)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(feats, f)
    if not feats:
        return gpd.GeoDataFrame(geometry=[], crs=CRS)
    g = gpd.GeoDataFrame.from_features(feats, crs=CRS)
    return g


class Navarra:
    """Datos de Navarra dentro del rectángulo (area = polígono de Navarra recortado al rectángulo)."""

    def __init__(self, area, roads, bld, hgt, hyd, topo):
        self.area = area
        self.border = area.boundary
        self.roads, self.bld, self.hgt, self.hyd, self.topo = roads, bld, hgt, hyd, topo


def load(bbox, cache_dir, log=_log, refresh=False):
    """Descarga (o lee de la caché) las capas de IDENA. Devuelve None si el rectángulo no toca Navarra."""
    rect = box(*bbox)
    muni = fetch_layer("DIADMI_Pol_Municipio", bbox, cache_dir, refresh=refresh)
    if muni.empty:
        return None
    area = shapely.make_valid(muni.geometry.union_all()).intersection(rect)
    if area.is_empty or area.area < 1e4:
        return None
    nb = tuple(round(v) for v in area.buffer(60).intersection(rect).bounds)
    log(f"Navarra dentro del área: {area.area / 1e6:.1f} km² ({', '.join(sorted(muni.MUNICIPIO.astype(str)))}); datos de IDENA")
    roads = fetch_layer("CARBTA_Lin_RedViaria", nb, cache_dir, cql="COMPONEN1D='EJE'", refresh=refresh)
    bld = fetch_layer("CARBTA_Pol_EdiPobCons", nb, cache_dir, refresh=refresh)
    hgt = fetch_layer("DIRECC_Pol_Edifaltura", nb, cache_dir, refresh=refresh)
    hyd = fetch_layer("HIDROG_Lin_CorrienteNatural", nb, cache_dir, refresh=refresh)
    topo = fetch_layer("TOPONI_Txt_Toponimos", nb, cache_dir, refresh=refresh)
    return Navarra(area, roads, bld, hgt, hyd, topo)


# --------------------------------------------------------------------------- helpers
def _lines(geom):
    if geom is None or geom.is_empty:
        return []
    if geom.geom_type == "LineString":
        return [geom]
    if hasattr(geom, "geoms"):
        out = []
        for g in geom.geoms:
            out += _lines(g)
        return out
    return []


def _nice(s):
    s = (s or "").strip()
    if s.isupper():
        s = " ".join(w.capitalize() if not re.match(r"^[IVX]+$", w) else w for w in s.split())
    return s


def _road_name(r):
    code = (r.get("CODIGOC") or "").strip()
    if code:
        return code
    n = (r.get("NOMBRE") or "").strip()
    if not n or GENERIC_NAMES.match(n):
        return ""
    n = re.sub(r"^CALLE\s+", "", n, flags=re.I)
    return _nice(n)


def _split_at(line, dists):
    """Parte una línea en las distancias dadas (sobre la línea)."""
    L = line.length
    ds = sorted(d for d in set(round(d, 3) for d in dists) if 0.05 < d < L - 0.05)
    if not ds:
        return [line]
    out, prev = [], 0.0
    for d in ds + [L]:
        seg = substring(line, prev, d)
        if seg.geom_type == "LineString" and seg.length > 0.01:
            out.append(seg)
        prev = d
    return out


def _replace_end(line, at_start, xy):
    c = list(line.coords)
    if at_start:
        c[0] = (xy[0], xy[1])
    else:
        c[-1] = (xy[0], xy[1])
    return LineString(c)


# --------------------------------------------------------------------------- calles
def merge_roads(v, nav, log=_log):
    """v: viales de b5m (ya filtrados). Devuelve b5m + Navarra, empalmados en la muga."""
    if nav is None or nav.roads.empty:
        return v
    r = nav.roads
    r = r[r.TIPO.isin(ROAD_TYPES.keys())]
    rows = []
    for _, x in r.iterrows():
        rtype = ROAD_TYPES[x.TIPO]
        bt = "3" if str(x.get("SITUACION")) == "ELE" else "0"
        name = _road_name(x)
        for l in _lines(x.geometry.intersection(nav.area)):
            if l.length >= 1.0:
                rows.append({"geom": LineString([(p[0], p[1]) for p in l.coords]), "TYPE_ES": rtype, "NAME": name, "BRIDGE_TUNNEL": bt})
    if not rows:
        return v
    # b5m en líneas sueltas, para poder partirlas
    b = v.explode(index_parts=False)
    b = b[b.geom_type == "LineString"].reset_index(drop=True)
    blines = list(b.geometry)
    btree = shapely.STRtree(blines)
    bends = np.array([c for l in blines for c in (l.coords[0][:2], l.coords[-1][:2])]) if blines else np.zeros((0, 2))
    from scipy.spatial import cKDTree
    etree = cKDTree(bends) if len(bends) else None

    nlines = [x["geom"] for x in rows]
    # 1) cruces en T dentro de Navarra: un final que toca el interior de otro eje parte ese eje
    ntree = shapely.STRtree(nlines)
    splits = {}
    for i, l in enumerate(nlines):
        for at_start in (True, False):
            p = Point(l.coords[0] if at_start else l.coords[-1])
            for j in ntree.query(p.buffer(0.5)):
                if j == i:
                    continue
                o = nlines[j]
                if o.distance(p) < 0.5:
                    d = o.project(p)
                    if 0.05 < d < o.length - 0.05:
                        splits.setdefault(j, []).append(d)
                        q = o.interpolate(d)
                        nlines[i] = l = _replace_end(l, at_start, (q.x, q.y))
    # 2) empalmes en la muga con b5m
    bsplits = {}
    joined = 0
    for i, l in enumerate(nlines):
        for at_start in (True, False):
            p = Point(l.coords[0] if at_start else l.coords[-1])
            if nav.border.distance(p) > 1.0:
                continue
            target = None
            if etree is not None:
                d, k = etree.query([p.x, p.y])
                if d < SNAP_DIST:
                    target = tuple(bends[k])
            if target is None:
                best = None
                for j in btree.query(p.buffer(SNAP_DIST)):
                    dd = blines[j].distance(p)
                    if dd < SNAP_DIST and (best is None or dd < best[0]):
                        best = (dd, j)
                if best is not None:
                    j = best[1]
                    d = blines[j].project(p)
                    q = blines[j].interpolate(d)
                    bsplits.setdefault(j, []).append(d)
                    target = (q.x, q.y)
            if target is not None:
                nlines[i] = l = _replace_end(l, at_start, target)
                joined += 1
    # aplicar los cortes
    out_n = []
    for i, l in enumerate(nlines):
        for seg in (_split_at(l, splits[i]) if i in splits else [l]):
            out_n.append({**{k: rows[i][k] for k in ("TYPE_ES", "NAME", "BRIDGE_TUNNEL")}, "geometry": seg})
    if bsplits:
        extra = []
        for j, ds in bsplits.items():
            parts = _split_at(blines[j], ds)
            for seg in parts:
                rr = b.iloc[j].copy()
                rr["geometry"] = seg
                extra.append(rr)
        b = pd.concat([b.drop(index=list(bsplits.keys())), gpd.GeoDataFrame(extra, crs=b.crs)], ignore_index=True)
    n = gpd.GeoDataFrame(out_n, geometry="geometry", crs=v.crs)
    log(f"Viales de Navarra: {len(n)} tramos, {n.length.sum() / 1000:.1f} km ({n[n.TYPE_ES == 'Carretera'].length.sum() / 1000:.1f} km de carretera); "
        f"empalmes en la muga: {joined}")
    return gpd.GeoDataFrame(pd.concat([b, n], ignore_index=True), geometry="geometry", crs=v.crs)


# --------------------------------------------------------------------------- edificios
def merge_buildings(b, nav, log=_log):
    """b: edificios de b5m. Añade los de Navarra con su altura (catastro) y su nombre."""
    if nav is None or nav.bld.empty:
        return b
    g = nav.bld.explode(index_parts=False)
    g = g[g.geom_type == "Polygon"]
    tipo = g.TIPO.astype(str)
    keep = tipo.str.startswith("Edificación") | tipo.str.startswith("Depósito de agua") | tipo.str.startswith("Depósito genérico")
    g = g[keep]
    g = g[g.geometry.representative_point().within(nav.area)]
    # quitar lo que ya esté en b5m
    if len(b):
        bt = shapely.STRtree(list(b.geometry))
        g = g[[len(bt.query(x, predicate="intersects")) == 0 for x in g.geometry]]
    # altura: media (por superficie) de las alturas catastrales que caen dentro + el tejado
    ht = nav.hgt[nav.hgt.ALTURA.astype(float) > 0] if not nav.hgt.empty else nav.hgt
    htree = shapely.STRtree(list(ht.geometry)) if len(ht) else None
    hvals = ht.ALTURA.astype(float).values if len(ht) else None
    rows, with_h = [], 0
    for _, x in g.iterrows():
        t = str(x.TIPO)
        light = t.startswith("Edificación ligera") or t.startswith("Depósito") or str(x.get("ESTADOSERV")) == "RUI"
        hgt = np.nan
        if htree is not None and not light:
            idx = htree.query(x.geometry, predicate="intersects")
            if len(idx):
                areas = np.array([ht.geometry.iloc[k].intersection(x.geometry).area for k in idx])
                if areas.sum() > 0.3 * x.geometry.area:
                    hgt = float((areas * hvals[idx]).sum() / areas.sum()) + 2.5
                    with_h += 1
        name = (x.get("NOMBRE") or "").strip()
        rows.append({"geometry": x.geometry, "TYPE_ES": "Otro" if light else "Edificio",
                     "AVERAGE_HEIGHT": hgt, "NAME": "", "NAME_BUILDING_ES": name, "NAME_BUILDING_EU": name,
                     "DATE_CONSTRUCTION": ""})
    if not rows:
        return b
    n = gpd.GeoDataFrame(rows, geometry="geometry", crs=b.crs)
    log(f"Edificios de Navarra: {len(n)} (con altura del catastro: {with_h})")
    return gpd.GeoDataFrame(pd.concat([b, n], ignore_index=True), geometry="geometry", crs=b.crs)


# --------------------------------------------------------------------------- ríos
def merge_rivers(h, nav, log=_log):
    """h: hidrografía de b5m. Añade ríos y regatas de Navarra que no estén ya (el Urumea hace de muga)."""
    if nav is None or nav.hyd.empty:
        return h
    g = nav.hyd[nav.hyd.DESCRIPCIO.astype(str) != "Corriente natural no permanente seco"]
    near = h[h.geometry.intersects(nav.area.buffer(100))] if len(h) else h
    have = near.geometry.union_all() if len(near) else None
    rows = []
    for _, x in g.iterrows():
        name = (x.get("NOMBRE") or "").strip()
        if re.match(r"^ZZ\d+$", name):
            name = ""
        urumea = name.lower().startswith("urumea")
        geom = x.geometry.intersection(nav.area)
        if have is not None:
            geom = geom.difference(have.buffer(45 if urumea else 20))
        for l in _lines(geom):
            if l.length >= 60:
                rows.append({"geometry": l, "NAME": "Urumea" if urumea else name})
    if not rows:
        return h
    n = gpd.GeoDataFrame(rows, geometry="geometry", crs=h.crs)
    log(f"Cursos de agua de Navarra: {len(n)} tramos, {n.length.sum() / 1000:.1f} km")
    return gpd.GeoDataFrame(pd.concat([h, n], ignore_index=True), geometry="geometry", crs=h.crs)


# --------------------------------------------------------------------------- toponimia
def merge_places(t, nav, log=_log):
    """t: toponimia de b5m (sin geometría). Añade núcleos, barrios, montes y ermitas de Navarra."""
    if nav is None or nav.topo.empty:
        return t
    g = nav.topo[nav.topo.geometry.within(nav.area)].drop_duplicates("CTOPONIMO")
    have = set((t.NAME.fillna("").str.split(" / ").str[0]).str.strip()) if len(t) else set()
    rows = []
    for _, x in g.iterrows():
        name = (x.TOPONIMO or "").strip()
        ty, muni = str(x.TIPO), str(x.MUNICIPIO)
        out = None
        if ty == "Espacio urbano" and name == muni:
            out = "núcleo urbano"
        elif ty == "Espacio urbano" and re.match(r"^(Beheko|Goiko) Herri", name):
            out = "barrio"
        elif ty == "Orografía":
            out = "monte"
        elif ty == "Servicios comunitarios" and name.startswith("San "):
            out = "ermita"
        if out and name and name not in have:
            have.add(name)
            rows.append({"NAME": name, "TYPE_ES": out, "X_CEN_ETRS89": x.geometry.x, "Y_CEN_ETRS89": x.geometry.y})
    if not rows:
        return t
    log(f"Topónimos de Navarra: {len(rows)} ({', '.join(r['NAME'] for r in rows if r['TYPE_ES'] != 'monte')})")
    return pd.concat([t, pd.DataFrame(rows)], ignore_index=True)
