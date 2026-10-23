#!/usr/bin/env python3
"""
Coches de Hernani Racing: modelos 3D propios generados por código (cargen.py), sin marcas ni logotipos.
Las formas se inspiran en coches clásicos conocidos (medidas reales aproximadas) para que se reconozcan.

Escribe Resources/HernaniCars/<id>.json (formato que monta CarModels.cs en tiempo de ejecución):
  body / wheelL / wheelR          modelo detallado
  bodyLod / wheelLodL / wheelLodR versión ligera para cuando el coche se ve de lejos (LODGroup)
Cada submalla lleva su material: kind (paint, glass, chrome, light...), color, suavidad y si es metálico.
La submalla 'paint' es la que toma el color que elige el jugador.

Uso:  python build_cars.py [id ...]       (sin argumentos: todos)
      python preview_cars.py furgoneta,utilitario preview.png
"""
import json
import math
import os
import sys

import numpy as np

from cargen import (Body, BodySpec, Mesh, box, cylinder, disc, end_factor, extrude_poly, loft_ellipses, norm, patch, plate,
                    prof, rot_x, rot_y, rounded_poly, rrect, smoothstep, superellipsoid, sweep, wheel)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Assets", "HernaniRacing", "Resources", "HernaniCars")

CARS = {}


def car(fn):
    CARS[fn.__name__.replace("build_", "")] = fn
    return fn


# ------------------------------------------------------------------ utilidades comunes
def mirror_pair(fn, *a, **k):
    """Llama fn(sign) para el lado derecho (+1) y el izquierdo (-1)."""
    for s in (1, -1):
        fn(s, *a, **k)


def front_light(m, body, x, y, r, kind="light", ring="chrome", out=0.0, segs=20, front=True, ellipse=1.0):
    z = body.front_z(x, y, front)
    if z is None:
        return
    n = body.front_normal(x, y, front)
    c = np.array([x, y, z]) + n * out
    if ring:
        disc(m, c, n, r * 1.18, ring, None, segs, thick=0.018, ellipse=ellipse)
        disc(m, c + n * 0.012, n, r, kind, None, segs, thick=0.012, ellipse=ellipse)
    else:
        disc(m, c, n, r, kind, None, segs, thick=0.015, ellipse=ellipse)


def front_plate(m, body, y, w, h, kind, color=None, front=True, x=0.0, out=0.004, corner=0.0, thick=0.01):
    z = body.front_z(x, y, front)
    if z is None:
        return
    n = body.front_normal(x, y, front)
    plate(m, np.array([x, y, z]) + n * out, n, w, h, kind, color, thick=thick, corner=corner)


def side_plate(m, body, z, y, w, h, kind, color=None, side=1, out=0.004, thick=0.008, corner=0.0):
    x = body.side_x(z, y)
    n = body.side_normal(z, y)
    c = np.array([x, y, z]) + n * out
    if side < 0:
        c[0] = -c[0]; n = n * np.array([-1, 1, 1])
    plate(m, c, n, w, h, kind, color, thick=thick, up=(0, 1, 0), corner=corner)


def side_disc(m, body, z, y, r, kind, color=None, side=1, out=0.004, thick=0.008, ellipse=1.0, segs=18):
    x = body.side_x(z, y)
    n = body.side_normal(z, y)
    c = np.array([x, y, z]) + n * out
    if side < 0:
        c[0] = -c[0]; n = n * np.array([-1, 1, 1])
    disc(m, c, n, r, kind, color, segs, thick=thick, ellipse=ellipse)


def number_plates(m, body, yf, yr, front=True, rear=True, wide=True):
    """Matrículas españolas (blancas con la franja azul a la izquierda)."""
    w, h = (0.52, 0.11) if wide else (0.34, 0.2)
    for fr, y, on in ((True, yf, front), (False, yr, rear)):
        if not on:
            continue
        z = body.front_z(0.0, y, fr)
        if z is None:
            continue
        n = body.front_normal(0.0, y, fr)
        c = np.array([0.0, y, z])
        plate(m, c + n * 0.006, n, w, h, "plate", None, thick=0.008)
        # franja azul (a la izquierda mirando la placa)
        side = np.cross(np.array([0, 1.0, 0]), n)
        side = side / np.linalg.norm(side)
        plate(m, c + n * 0.016 + side * (w / 2 - 0.022), n, 0.04, h * 0.92, "blue", None, thick=0.004)


LOD = {"on": False}


def side_windows(m, body, wins, r=0.06, kind="glass", both=True, color=None, offset=0.007):
    """wins: [(z0, z1, y0, y1)] o polígonos [[(z, y), ...]] en el lateral."""
    lod = LOD["on"]
    import cargen
    for s in ((1, -1) if both else (1,)):
        f = body.map_side(s)
        for w in wins:
            cargen.SIDE_MISS[0] = 0
            n = 2 if lod else 4
            out = rrect(*w, r, n) if len(w) == 4 and np.isscalar(w[0]) else rounded_poly(w, r, n)
            patch(m, f, out, kind, color, offset=offset * (1.6 if lod else 1), rings=1 if lod else 3, subdiv=1 if lod else 2)
            if cargen.SIDE_MISS[0] and s == 1 and not lod:
                print("   aviso: ventana fuera del lateral", w if len(w) == 4 else w[:2], cargen.SIDE_MISS[0])


def front_window(m, body, poly, r=0.06, kind="glass", front=True, color=None, offset=0.007, rings=3):
    import cargen
    lod = LOD["on"]
    cargen.FRONT_MISS[0] = 0
    patch(m, body.map_front(front), rounded_poly(poly, r, 2 if lod else 4), kind, color, offset=offset * (1.6 if lod else 1),
          rings=1 if lod else rings, subdiv=1 if lod else 2)
    if cargen.FRONT_MISS[0] and not lod:
        print("   aviso: luna fuera de la superficie", poly[:2], cargen.FRONT_MISS[0])


def mirror_on_stalk(m, base, side, length=0.12, head=(0.1, 0.07, 0.035), kind="paint", stalk="black", tilt=0.0):
    b = np.array(base, float); b[0] *= side
    tip = b + np.array([side * length, 0.02, -0.02])
    cylinder(m, b, tip, 0.012, stalk, None, segs=8)
    c = tip + np.array([side * head[0] * 0.35, 0.0, -0.005])
    superellipsoid(m, c, (head[0] / 2, head[1] / 2, head[2] / 2), 3, 3, kind, None, segs=(12, 8))
    disc(m, c + np.array([0, 0, -head[2] / 2]), (0, 0, -1), head[1] * 0.42, "chrome", None, 14, thick=0.004,
         ellipse=head[0] / head[1])


def bumper_bar(m, body, y, out, hw, hh, kind, front=True, xmax=None, wrap=0.25, steps=24, n_exp=4.0, color=None):
    """Parachoques que sigue la forma del morro (o de la zaga) a la altura y."""
    sgn = 1 if front else -1
    pts = []
    xm = xmax if xmax is not None else body.side_x(body.zs[-1 if front else 0] - sgn * 0.3, y) + out
    for k in range(steps + 1):
        x = -xm + 2 * xm * k / steps
        z = body.front_z(min(abs(x), xm - 0.01), y, front)
        if z is None:
            z = body.zs[-1 if front else 0]
        n = body.front_normal(min(abs(x), xm - 0.01), y, front)
        if x < 0:
            n = n * np.array([-1, 1, 1])
        p = np.array([x, y, z]) + n * out
        pts.append(p)
    pts = np.array(pts)
    # remates hacia atrás en las esquinas
    if wrap > 0:
        a = pts[0] + np.array([0, 0, -sgn * wrap]); b = pts[-1] + np.array([0, 0, -sgn * wrap])
        pts = np.r_[[a], pts, [b]]
    sweep(m, pts, hw, hh, kind, color, n_exp=n_exp, up=(0, 1, 0), segs=12)


def export(cid, meta, body_m, lod_m, wheel_m, wheel_lod, R, wheels, phys, paint_default, colors, pbounds):
    V, _ = body_m.arrays()
    lo, hi = V.min(0), V.max(0)
    plo, phi = pbounds
    # incluir las ruedas en los límites
    for c in wheels:
        lo = np.minimum(lo, [c[0] - 0.12, 0.0, c[2] - R]); hi = np.maximum(hi, [c[0] + 0.12, 2 * R, c[2] + R])
    wl = wheel_m.mirrored_x(); wr = wheel_m
    wll = wheel_lod.mirrored_x(); wlr = wheel_lod
    data = {
        "id": cid, "name": meta["name"], "category": meta["category"], "selectable": meta["selectable"],
        "source": "Modelo propio generado por DataPipeline/build_cars.py (inspirado en: " + meta["inspired"] + ")",
        "description": meta.get("desc", ""),
        "size": np.round(hi - lo, 3).tolist(), "boundsMin": np.round(plo, 3).tolist(), "boundsMax": np.round(phi, 3).tolist(),
        "wheelRadius": round(R, 3), "wheels": [round(float(v), 4) for c in wheels for v in c],
        "body": body_m.to_json(colors), "wheelL": wl.to_json(colors), "wheelR": wr.to_json(colors),
        "bodyLod": lod_m.to_json(colors), "wheelLodL": wll.to_json(colors), "wheelLodR": wlr.to_json(colors),
        "paintColor": [round(float(x), 3) for x in paint_default],
        "trafficWeight": meta.get("traffic", 0.0),
        **{k: float(v) for k, v in phys.items()},
    }
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, cid + ".json"), "w") as f:
        json.dump(data, f, separators=(",", ":"))
    tris = body_m.tri_count() + 4 * wheel_m.tri_count()
    tl = lod_m.tri_count() + 4 * wheel_lod.tri_count()
    print(f"{cid:11s} {meta['name']:28s} {np.round(hi - lo, 2)} m  {tris:6d} triángulos (LOD {tl})")
    return data


def make(cid, meta, spec_fn, material, details, R, track, axles, tyre_w, wheel_style, phys, paint_default,
         colors=None, n_st=96, dense_extra=(), whitewall=False, rim_kind="metal", rim_color=None, wheel_x=None,
         splits=None, bounds_x=None):
    colors = dict(colors or {})
    colors.setdefault("paint", paint_default)
    spec = spec_fn()
    dense = [(axles[0], R * 1.3), (axles[1], R * 1.3)] + list(dense_extra)
    body = Body(spec, n_st=n_st, dense=dense)
    m = Mesh()
    body.build(m, material, splits)
    Vb, _ = m.arrays()
    plo, phi = Vb.min(0), Vb.max(0)
    details(m, body, False)
    Va, _ = m.arrays()
    plo = np.array([plo[0], plo[1], max(Va[:, 2].min(), plo[2] - 0.12)]); phi = np.array([phi[0], phi[1], min(Va[:, 2].max(), phi[2] + 0.12)])
    if bounds_x:
        plo[0], phi[0] = -bounds_x, bounds_x
    lb = Body(spec, n_st=30, Kb=3, Kt=4, Kc=6, dense=dense)
    lm = Mesh()
    lb.build(lm, material, splits)
    import cargen
    LOD["on"] = True
    cargen.PATCH_MAXLEN[0] = 0.3
    details(lm, lb, True)
    LOD["on"] = False
    cargen.PATCH_MAXLEN[0] = 0.08
    wx = track / 2 if wheel_x is None else wheel_x
    wheels = [(-wx, R, axles[0]), (wx, R, axles[0]), (-wx, R, axles[1]), (wx, R, axles[1])]
    wm = wheel(R, tyre_w, wheel_style, rim_kind, rim_color, whitewall)
    wlod = wheel(R, tyre_w, wheel_style, rim_kind, rim_color, False, lod=True)
    return export(cid, meta, m, lm, wm, wlod, R, wheels, phys, paint_default, colors, (plo, phi))


def arches(spec, axles, R, factor=1.13, shape="round"):
    spec.arches = [(z, R, factor, R) for z in axles]
    spec.arch_shape = shape


# =================================================================== FURGONETA HIPPIE (tipo T1 "Samba")
@car
def build_furgoneta():
    L = 4.28; zf, zr = 2.14, -2.14
    axles = (1.22, -1.18)
    R = 0.335
    WHITE = (0.94, 0.92, 0.85)

    def spec_fn():
        zfe = zf - 0.01; zre = zr + 0.01
        def w(z):
            return 0.875 * end_factor(z, zf - 0.42, zf + 0.02, 2.3, 0.0) * end_factor(z, zr + 0.36, zr - 0.02, 2.6, 0.0)
        s = BodySpec(zre, zfe,
                     yb=prof([(zr, 0.42), (zr + 0.3, 0.36), (0, 0.33), (zf - 0.3, 0.36), (zf, 0.46)]),
                     w=lambda z: max(float(w(z)), 0.30),
                     yw=prof([(zr, 0.86), (0, 0.84), (zf, 0.86)]),
                     yd=prof([(zr, 1.08), (0, 1.11), (zf, 1.10)]),
                     crown=0.02, nb=3.2, nt=5.0,
                     cab=prof([(zr, 1.30), (zr + 0.05, 1.42), (zr + 0.14, 1.64), (zr + 0.3, 1.84), (zr + 0.55, 1.93),
                               (0, 1.96), (zf - 0.38, 1.94), (zf - 0.2, 1.88), (zf - 0.1, 1.72), (zf - 0.04, 1.45), (zf, 1.22)]),
                     wr=lambda z: max(float(w(z)), 0.30) * 0.84,
                     wbelt=lambda z: max(float(w(z)), 0.30) * 0.975,
                     nc=4.2, rcrown=0.03)
        arches(s, axles, R, 1.16)
        return s

    def material(z, x, y, n, region, inf):
        if n[1] < -0.55 and y < 0.6:
            return ("under", None)
        return ("paint", None)

    def two_tone(p):            # > 0 por encima de la línea del bitono (con la "V" del morro)
        x, y, z = p
        split = 1.085
        if z > zf - 0.6:
            split = min(1.085, 0.74 + 0.5 * abs(x) + max(0.0, (zf - 0.25 - z)) * 1.2)
        return y - split

    def glass(m, body, lod):
        # parabrisas partido, luneta y las ventanillas laterales (Samba)
        for s in (1, -1):
            front_window(m, body, [(0.04 * s, 1.21), (0.72 * s, 1.21), (0.70 * s, 1.70), (0.04 * s, 1.73)], 0.07, offset=0.013)
        front_window(m, body, [(-0.5, 1.3), (0.5, 1.3), (0.48, 1.6), (-0.48, 1.6)], 0.08, front=False)
        wins = [(1.04, 1.70, 1.2, 1.72), (0.30, 0.92, 1.2, 1.72), (-0.44, 0.18, 1.2, 1.72), (-1.18, -0.56, 1.2, 1.72),
                (-1.84, -1.32, 1.24, 1.68)]
        side_windows(m, body, wins, 0.07)
        if not lod:
            sky = [(z0, z0 + 0.22, 1.835, 1.885) for z0 in np.arange(-1.25, 0.9, 0.3)]
            side_windows(m, body, sky, 0.02)

    def details(m, body, lod):
        glass(m, body, lod)
        # faros redondos con aro cromado
        for s in (1, -1):
            front_light(m, body, 0.63 * s, 0.97, 0.088, "light", "chrome", segs=20 if not lod else 10)
            if not lod:
                front_light(m, body, 0.64 * s, 1.18, 0.03, "amber", "chrome", segs=12)
                # pilotos traseros
                front_light(m, body, 0.68 * s, 0.98, 0.045, "tail", "chrome", front=False, segs=14, ellipse=0.7)
        # emblema redondo genérico (sin letras) en la V
        z = body.front_z(0, 0.86)
        if z is not None:
            disc(m, np.array([0, 0.86, z]), body.front_normal(0, 0.86), 0.125, "chrome", None, 28 if not lod else 12, thick=0.02)
            if not lod:
                disc(m, np.array([0, 0.86, z + 0.014]), body.front_normal(0, 0.86), 0.098, "paint2", None, 28, thick=0.012)
                disc(m, np.array([0, 0.86, z + 0.022]), body.front_normal(0, 0.86), 0.04, "chrome", None, 16, thick=0.01)
        # parachoques cromados
        bumper_bar(m, body, 0.42, 0.07, 0.03, 0.045, "chrome", True, xmax=0.86, wrap=0.18)
        bumper_bar(m, body, 0.44, 0.07, 0.03, 0.045, "chrome", False, xmax=0.86, wrap=0.18)
        if lod:
            return
        number_plates(m, body, 0.58, 0.72)
        # retrovisores redondos en brazo largo
        for s in (1, -1):
            x = body.side_x(1.62, 1.2)
            mirror_on_stalk(m, (x, 1.2, 1.62), s, 0.16, (0.1, 0.1, 0.03), "chrome", "chrome")
        # rejillas de ventilación del motor (arriba, detrás)
        for s in (1, -1):
            for k in range(6):
                side_plate(m, body, -1.95 + k * 0.045, 1.42, 0.03, 0.22, "black", None, side=s, out=0.003)
        # flores hippies en los costados
        flowers = [(0.55, 0.70, 0.16, (1.0, 0.55, 0.08)), (0.08, 0.86, 0.1, (1.0, 0.85, 0.15)),
                   (-0.32, 0.6, 0.12, (1.0, 0.42, 0.72)), (-1.9, 0.78, 0.1, (0.45, 0.85, 0.3)),
                   (1.85, 0.72, 0.07, (1.0, 0.85, 0.15))]
        for s in (1, -1):
            for z, y, r, col in flowers:
                for k in range(5):
                    a = 2 * math.pi * k / 5 + 0.3
                    side_disc(m, body, z + math.cos(a) * r * 0.62, y + math.sin(a) * r * 0.62, r * 0.42, "white", col,
                              side=s, out=0.004, thick=0.004, ellipse=1.0, segs=10)
                side_disc(m, body, z, y, r * 0.33, "white", (1.0, 0.95, 0.75), side=s, out=0.009, thick=0.004)
        # baca con tabla de surf
        top = 1.96
        for s in (1, -1):
            pts = [np.array([0.6 * s, top + 0.09, zz]) for zz in np.linspace(-1.35, 1.05, 10)]
            sweep(m, pts, 0.014, 0.014, "chrome", None, n_exp=2, segs=8)
            for zz in (-1.3, -0.15, 1.0):
                cylinder(m, (0.6 * s, top - 0.02, zz), (0.6 * s, top + 0.09, zz), 0.012, "chrome", None, segs=6)
        for zz in (-1.3, -0.15, 1.0):
            cylinder(m, (-0.6, top + 0.09, zz), (0.6, top + 0.09, zz), 0.011, "chrome", None, segs=6)
        secs = []
        for k in range(21):
            u = k / 20
            zz = -1.25 + 2.35 * u
            wdt = 0.27 * (math.sin(math.pi * min(1, u * 1.08)) ** 0.55 if u < 0.92 else 0.27 * (1 - (u - 0.92) / 0.08) * 0 + 0.0)
            wdt = 0.27 * max(0.05, math.sin(math.pi * (0.04 + 0.92 * u)) ** 0.5) * (1 - 0.6 * u ** 4)
            secs.append(((0, top + 0.135 + 0.06 * u ** 6, zz), wdt, 0.032 * (0.6 + 0.4 * math.sin(math.pi * u))))
        loft_ellipses(m, secs, "white", (0.2, 0.72, 0.78), segs=14, n_exp=2.6)
        # raya de la tabla
        loft_ellipses(m, [((0, s[0][1] + 0.002, s[0][2]), 0.03, s[2] * 1.02) for s in secs[2:-2]], "white",
                      (1.0, 0.82, 0.2), segs=8, n_exp=2.0)

    meta = dict(name="Furgoneta hippie", category="Furgoneta clásica", selectable=True, traffic=0.5,
                inspired="furgoneta de los años 60 tipo 'T1'",
                desc="Bitono, flores, baca y tabla de surf. Lenta pero muy maja.")
    phys = dict(mass=1150, maxSpeed=32, engine=6200, brake=9500, grip=1.0, downforce=0.8)
    return make("furgoneta", meta, spec_fn, material, details, R, 1.37, axles, 0.16, "hubcap", phys,
                (0.12, 0.42, 0.62), {"paint2": WHITE}, whitewall=True, rim_kind="paint2",
                splits=[(two_tone, "paint", "paint2")])


# =================================================================== UTILITARIO (tipo 500 clásico)
@car
def build_utilitario():
    zf, zr = 1.485, -1.485
    axles = (0.93, -0.91)
    R = 0.27

    def spec_fn():
        def w(z):
            return 0.66 * end_factor(z, 0.75, zf + 0.06, 2.2, 0.0) * end_factor(z, -1.05, zr - 0.05, 2.4, 0.0)
        s = BodySpec(zr + 0.01, zf - 0.01,
                     yb=prof([(zr, 0.36), (zr + 0.25, 0.26), (0, 0.23), (zf - 0.25, 0.27), (zf, 0.38)]),
                     w=lambda z: max(float(w(z)), 0.26),
                     yw=prof([(zr, 0.55), (0, 0.55), (zf, 0.5)]),
                     yd=prof([(zr, 0.66), (zr + 0.4, 0.76), (-0.85, 0.79), (0.4, 0.8), (0.9, 0.7), (zf - 0.1, 0.6), (zf, 0.55)]),
                     crown=prof([(zr, 0.06), (-0.9, 0.04), (0.45, 0.03), (1.0, 0.06), (zf, 0.04)]),
                     nb=3.0, nt=3.4,
                     cab=prof([(zr, 0.0), (-1.02, 0.7), (-0.9, 0.98), (-0.75, 1.14), (-0.55, 1.26), (-0.2, 1.32),
                               (0.05, 1.31), (0.18, 1.25), (0.32, 1.06), (0.46, 0.84), (0.55, 0.6), (zf, 0.0)]),
                     wr=lambda z: max(float(w(z)), 0.26) * 0.74,
                     wbelt=lambda z: max(float(w(z)), 0.26) * 0.9,
                     nc=3.0, rcrown=0.03)
        arches(s, axles, R, 1.2)
        return s

    def material(z, x, y, n, region, inf):
        if n[1] < -0.55 and y < 0.45:
            return ("under", None)
        return ("paint", None)

    def details(m, body, lod):
        # lunas
        side_windows(m, body, [[(-0.2, 0.86), (0.4, 0.86), (0.13, 1.14), (-0.2, 1.16)],
                               [(-0.72, 0.9), (-0.3, 0.88), (-0.3, 1.14), (-0.55, 1.12)]], 0.05)
        front_window(m, body, [(-0.48, 0.87), (0.48, 0.87), (0.38, 1.16), (-0.38, 1.16)], 0.07, offset=0.01)
        front_window(m, body, [(-0.32, 0.97), (0.32, 0.97), (0.26, 1.12), (-0.26, 1.12)], 0.06, front=False, offset=0.01)
        # techo de lona
        patch(m, body.map_top(), rrect(-0.34, 0.34, -0.5, 0.02, 0.08, 2 if lod else 4), "fabric", None, rings=1 if lod else 3,
              offset=0.008, subdiv=1 if lod else 2)
        for s in (1, -1):
            front_light(m, body, 0.43 * s, 0.64, 0.075, "light", "chrome", segs=10 if lod else 20)
        bumper_bar(m, body, 0.37, 0.05, 0.018, 0.03, "chrome", True, xmax=0.6, wrap=0.12)
        bumper_bar(m, body, 0.4, 0.05, 0.018, 0.03, "chrome", False, xmax=0.6, wrap=0.12)
        if lod:
            return
        for s in (1, -1):
            front_light(m, body, 0.47 * s, 0.5, 0.028, "amber", "chrome", segs=12)
            front_light(m, body, 0.5 * s, 0.6, 0.04, "tail", "chrome", front=False, segs=14, ellipse=0.6)
        # bigote cromado y emblema genérico en el morro
        pts = []
        for k in range(13):
            x = -0.22 + 0.44 * k / 12
            y = 0.6 - 0.05 * (abs(x) / 0.22) ** 1.5
            z = body.front_z(x, y)
            n = body.front_normal(x, y)
            pts.append(np.array([x, y, z]) + n * 0.012)
        sweep(m, pts, 0.008, 0.008, "chrome", None, n_exp=2, segs=8)
        front_plate(m, body, 0.66, 0.07, 0.06, "chrome", None, corner=0.02)
        # tira cromada en la cintura
        for s in (1, -1):
            pts = []
            for zz in np.linspace(-1.2, 1.05, 24):
                y = 0.72
                x = body.side_x(zz, y)
                pts.append(np.array([x * s + 0.006 * s, y, zz]))
            sweep(m, pts, 0.006, 0.007, "chrome", None, n_exp=2, segs=6)
        # rejillas del motor en la tapa trasera
        for k in range(7):
            front_plate(m, body, 0.74 + k * 0.022, 0.36, 0.009, "black", None, front=False, out=0.002)
        number_plates(m, body, 0.47, 0.5, wide=False)
        for s in (1, -1):
            x = body.side_x(0.38, 0.84)
            mirror_on_stalk(m, (x, 0.86, 0.38), s, 0.05, (0.07, 0.05, 0.025), "chrome", "chrome")

    meta = dict(name="Utilitario clásico (500)", category="Utilitario", selectable=True, traffic=0.6,
                inspired="utilitario italiano de 1957 tipo '500'",
                desc="Diminuto, ligero y con techo de lona. Ágil en el casco viejo.")
    phys = dict(mass=560, maxSpeed=34, engine=3700, brake=5800, grip=1.18, downforce=0.8)
    return make("utilitario", meta, spec_fn, material, details, R, 1.12, axles, 0.13, "hubcap", phys,
                (0.55, 0.74, 0.86), rim_kind="metal")


# =================================================================== TURISMO GT (tipo Golf GTI de 2ª generación)
@car
def build_turismo():
    zf, zr = 2.0, -1.99
    axles = (1.27, -1.2)
    R = 0.30

    def spec_fn():
        def w(z):
            return 0.84 * end_factor(z, 1.72, zf + 0.04, 3.2, 0.0) * end_factor(z, -1.78, zr - 0.03, 3.4, 0.0)
        s = BodySpec(zr + 0.01, zf - 0.01,
                     yb=prof([(zr, 0.36), (zr + 0.2, 0.27), (0, 0.25), (zf - 0.2, 0.28), (zf, 0.36)]),
                     w=lambda z: max(float(w(z)), 0.5),
                     yw=prof([(zr, 0.62), (0, 0.62), (zf, 0.6)]),
                     yd=prof([(zr, 0.92), (-1.6, 0.94), (0, 0.93), (0.62, 0.92), (1.6, 0.85), (zf, 0.8)]),
                     crown=0.03, nb=3.0, nt=6.0,
                     cab=prof([(zr, 0.0), (-1.975, 0.97), (-1.93, 1.1), (-1.84, 1.26), (-1.74, 1.36), (-1.6, 1.4),
                               (-0.3, 1.41), (-0.02, 1.39), (0.25, 1.22), (0.5, 1.06), (0.66, 0.95), (0.8, 0.0)]),
                     wr=lambda z: max(float(w(z)), 0.5) * 0.79,
                     wbelt=lambda z: max(float(w(z)), 0.5) * 0.955,
                     nc=4.5, rcrown=0.025)
        arches(s, axles, R, 1.15)
        return s

    def material(z, x, y, n, region, inf):
        if n[1] < -0.55 and y < 0.5:
            return ("under", None)
        if region in ("cab",) and y > 0.99 and abs(n[0]) > 0.6 and -0.36 < z < -0.26:
            return ("black", None)     # pilar B negro
        return ("paint", None)

    def details(m, body, lod):
        side_windows(m, body, [[(-0.32, 0.99), (0.6, 0.99), (0.05, 1.33), (-0.32, 1.34)],
                               [(-1.12, 0.99), (-0.42, 0.99), (-0.42, 1.34), (-1.02, 1.34)]], 0.04)
        front_window(m, body, [(-0.67, 1.0), (0.67, 1.0), (0.54, 1.34), (-0.54, 1.34)], 0.06, offset=0.01)
        front_window(m, body, [(-0.6, 1.0), (0.6, 1.0), (0.55, 1.32), (-0.55, 1.32)], 0.05, front=False, offset=0.01)
        # parrilla negra con el filete rojo y los faros redondos dobles
        front_plate(m, body, 0.69, 1.3, 0.17, "gloss", None, out=0.003, corner=0.02)
        if not lod:
            front_plate(m, body, 0.605, 1.26, 0.012, "white", (0.85, 0.05, 0.05), out=0.016)
            for k in range(4):
                front_plate(m, body, 0.64 + k * 0.03, 0.28, 0.008, "grey", (0.3, 0.3, 0.32), out=0.016)
        for s in (1, -1):
            front_light(m, body, 0.5 * s, 0.69, 0.08, "light", "chrome", out=0.012, segs=10 if lod else 20)
            front_light(m, body, 0.3 * s, 0.69, 0.058, "light", "chrome", out=0.012, segs=10 if lod else 18)
        # paragolpes negros envolventes
        bumper_bar(m, body, 0.44, 0.035, 0.04, 0.09, "black", True, xmax=0.83, wrap=0.2, n_exp=5)
        bumper_bar(m, body, 0.47, 0.035, 0.04, 0.09, "black", False, xmax=0.83, wrap=0.2, n_exp=5)
        # pilotos traseros
        for s in (1, -1):
            front_plate(m, body, 0.83, 0.36, 0.15, "tail", None, front=False, x=0.6 * s, out=0.004, corner=0.015)
            if not lod:
                front_plate(m, body, 0.8, 0.12, 0.05, "amber", None, front=False, x=0.68 * s, out=0.012)
        if lod:
            return
        for s in (1, -1):
            front_plate(m, body, 0.45, 0.14, 0.05, "amber", None, x=0.66 * s, out=0.075, corner=0.01)
        # moldura lateral negra
        for s in (1, -1):
            pts = [np.array([(body.side_x(zz, 0.56) + 0.008) * s, 0.56, zz]) for zz in np.linspace(-0.88, 0.9, 20)]
            sweep(m, pts, 0.012, 0.03, "black", None, n_exp=4, segs=8)
        # alerón de techo
        z0 = -1.55
        y0 = body.top_y(z0, 0)
        loft_ellipses(m, [((0, y0 + 0.01, z0 + 0.12), 0.64, 0.012), ((0, y0 + 0.02, z0 - 0.05), 0.66, 0.02),
                          ((0, y0 - 0.04, z0 - 0.2), 0.66, 0.012)], "paint", None, segs=12, n_exp=6)
        number_plates(m, body, 0.44, 0.62)
        for s in (1, -1):
            x = body.side_x(0.55, 1.0)
            mirror_on_stalk(m, (x, 1.02, 0.55), s, 0.05, (0.16, 0.1, 0.06), "black", "black")
        # antena
        cylinder(m, (0.0, 1.4, 0.2), (0.0, 1.62, -0.05), 0.004, "black", None, segs=5)

    meta = dict(name="Turismo GT (Golf GTI)", category="Compacto deportivo", selectable=True, traffic=1.0,
                inspired="compacto deportivo de los 80 tipo 'GTI' de 2ª generación",
                desc="El clásico 'GTI': ligero, rápido y fácil de conducir.")
    phys = dict(mass=980, maxSpeed=52, engine=8000, brake=10500, grip=1.32, downforce=1.6)
    return make("turismo", meta, spec_fn, material, details, R, 1.43, axles, 0.19, "snow", phys, (0.78, 0.06, 0.05),
                rim_kind="metal")


# =================================================================== BERLINA DE RALLY (tipo Impreza WRX de 1ª generación)
@car
def build_berlina():
    zf, zr = 2.175, -2.175
    axles = (1.39, -1.13)
    R = 0.31

    def spec_fn():
        def w(z):
            return 0.86 * end_factor(z, 1.55, zf + 0.06, 2.4, 0.0) * end_factor(z, -1.9, zr - 0.04, 3.0, 0.0)
        s = BodySpec(zr + 0.01, zf - 0.01,
                     yb=prof([(zr, 0.33), (zr + 0.2, 0.24), (0, 0.22), (zf - 0.25, 0.25), (zf, 0.3)]),
                     w=lambda z: max(float(w(z)), 0.45),
                     yw=prof([(zr, 0.6), (0, 0.6), (zf, 0.52)]),
                     yd=prof([(zr, 0.92), (-1.5, 0.95), (0, 0.9), (0.72, 0.9), (1.6, 0.8), (zf - 0.05, 0.7), (zf, 0.66)]),
                     crown=prof([(zr, 0.02), (0.7, 0.03), (1.4, 0.05), (zf, 0.03)]),
                     nb=2.8, nt=3.8,
                     cab=prof([(-1.5, 0.0), (-1.45, 0.98), (-1.25, 1.12), (-1.0, 1.28), (-0.8, 1.37), (-0.5, 1.4),
                               (-0.05, 1.39), (0.2, 1.27), (0.45, 1.1), (0.72, 0.95), (0.85, 0.0)]),
                     wr=lambda z: max(float(w(z)), 0.45) * 0.74,
                     wbelt=lambda z: max(float(w(z)), 0.45) * 0.92,
                     nc=3.4, rcrown=0.03)
        arches(s, axles, R, 1.16)
        return s

    def material(z, x, y, n, region, inf):
        if n[1] < -0.55 and y < 0.5:
            return ("under", None)
        return ("paint", None)

    def details(m, body, lod):
        side_windows(m, body, [[(-0.42, 0.97), (0.68, 0.97), (0.12, 1.32), (-0.42, 1.34)],
                               [(-1.24, 0.99), (-0.5, 0.97), (-0.5, 1.34), (-0.86, 1.33)]], 0.04)
        front_window(m, body, [(-0.66, 0.97), (0.66, 0.97), (0.5, 1.33), (-0.5, 1.33)], 0.07, offset=0.01)
        front_window(m, body, [(-0.6, 1.0), (0.6, 1.0), (0.5, 1.34), (-0.5, 1.34)], 0.06, front=False, offset=0.01)
        # faros y parrilla
        for s in (1, -1):
            pz = body.front_z(0.55 * s, 0.7)
            n = body.front_normal(0.55 * s, 0.7)
            plate(m, np.array([0.55 * s, 0.7, pz]) + n * 0.004, n, 0.34, 0.12, "light", None, thick=0.012, corner=0.04)
        front_plate(m, body, 0.69, 0.42, 0.09, "gloss", None, corner=0.03)
        # toma de aire en el capó
        zc = 1.15
        yc = body.top_y(zc, 0)
        superellipsoid(m, (0, yc + 0.012, zc), (0.27, 0.06, 0.24), 4, 4, "paint", None, segs=(16, 8))
        front_plate(m, body, yc + 0.04, 0.4, 0.035, "black", None, x=0.0, out=0.0) if False else None
        plate(m, np.array([0, yc + 0.035, zc + 0.215]), (0, 0.15, 1), 0.38, 0.045, "black", None, thick=0.006)
        # alerón trasero grande
        zw, yw = -1.95, 1.2
        for s in (1, -1):
            yb_ = body.top_y(zw + 0.05, 0.55)
            box(m, np.array([0.55 * s, (yb_ + yw) / 2, zw + 0.03]), (0.03, yw - yb_, 0.16), "paint")
        loft_ellipses(m, [((0, yw, zw + 0.15), 0.72, 0.012), ((0, yw + 0.015, zw + 0.05), 0.74, 0.024),
                          ((0, yw + 0.03, zw - 0.1), 0.74, 0.012)], "paint", None, segs=12, n_exp=6)
        # parachoques: labio negro y antinieblas
        bumper_bar(m, body, 0.3, 0.02, 0.03, 0.03, "black", True, xmax=0.78, wrap=0.15)
        for s in (1, -1):
            front_light(m, body, 0.56 * s, 0.42, 0.055, "light", "black", segs=10 if lod else 18)
            front_plate(m, body, 0.86, 0.42, 0.15, "tail", None, front=False, x=0.56 * s, out=0.004, corner=0.03)
        if lod:
            return
        number_plates(m, body, 0.5, 0.72)
        for s in (1, -1):
            x = body.side_x(0.65, 0.98)
            mirror_on_stalk(m, (x, 1.0, 0.65), s, 0.05, (0.15, 0.1, 0.06), "paint", "black")
            # faldillas de barro (ambiente rally)
            box(m, np.array([0.7 * s, 0.24, axles[0] - R - 0.06]), (0.2, 0.26, 0.008), "fabric", None)
            box(m, np.array([0.72 * s, 0.24, axles[1] - R - 0.06]), (0.22, 0.26, 0.008), "fabric", None)
        front_plate(m, body, 0.5, 0.18, 0.03, "black", None, front=False, x=0.55, out=0.01) if False else None
        cylinder(m, (-0.42, 0.24, zr - 0.02), (-0.42, 0.24, zr - 0.12), 0.045, "chrome", None, segs=12)

    meta = dict(name="Berlina de rally (Impreza)", category="Berlina deportiva", selectable=True, traffic=0.6,
                inspired="berlina de rally de los 90 tipo 'Impreza WRX'",
                desc="Tracción total, alerón grande y llantas doradas: agarre de rally.")
    phys = dict(mass=1230, maxSpeed=54, engine=9900, brake=12500, grip=1.42, downforce=1.8)
    return make("berlina", meta, spec_fn, material, details, R, 1.46, axles, 0.2, "multi", phys, (0.08, 0.22, 0.62),
                rim_kind="gold")


# =================================================================== TODOTERRENO (tipo Land Rover "Serie" de los 60)
@car
def build_suv():
    zf, zr = 1.81, -1.81
    axles = (1.12, -1.11)
    R = 0.385

    def spec_fn():
        def w(z):
            return 0.84 * end_factor(z, 1.70, zf + 0.012, 6.0, 0.0) * end_factor(z, -1.74, zr - 0.012, 6.0, 0.0)
        s = BodySpec(zr + 0.005, zf - 0.005,
                     yb=prof([(zr, 0.58), (0, 0.55), (zf, 0.58)]),
                     w=lambda z: max(float(w(z)), 0.55),
                     yw=prof([(zr, 0.98), (0, 0.98), (zf, 0.96)]),
                     yd=prof([(zr, 1.2), (0.6, 1.2), (0.8, 1.16), (zf, 1.15)]),
                     crown=0.012, nb=9.0, nt=9.0,
                     cab=prof([(zr, 1.9), (zr + 0.04, 1.96), (-1.6, 1.98), (0.5, 1.98), (0.56, 1.94), (0.62, 1.78),
                               (0.68, 1.4), (0.72, 1.22), (0.78, 0.0)]),
                     wr=lambda z: max(float(w(z)), 0.55) * 0.93,
                     wbelt=lambda z: max(float(w(z)), 0.55) * 0.985,
                     nc=9.0, rcrown=0.015)
        arches(s, axles, R, 1.14, "square")
        return s

    def material(z, x, y, n, region, inf):
        if n[1] < -0.55 and y < 0.8:
            return ("under", None)
        return ("paint", None)

    def roof_split(p):
        return p[1] - 1.8

    def details(m, body, lod):
        # parabrisas partido (marco vertical), ventanillas, ventanas "alpinas" del techo
        for s in (1, -1):
            front_window(m, body, [(0.04 * s, 1.27), (0.74 * s, 1.27), (0.74 * s, 1.72), (0.04 * s, 1.72)], 0.03, offset=0.01)
        side_windows(m, body, [(0.08, 0.56, 1.27, 1.72), (-0.72, -0.04, 1.3, 1.72), (-1.6, -0.84, 1.3, 1.72)], 0.03)
        if not lod:
            side_windows(m, body, [(z0, z0 + 0.3, 1.84, 1.91) for z0 in (-1.55, -1.15, -0.75, -0.35)], 0.015)
        front_window(m, body, [(-0.32, 1.3), (0.32, 1.3), (0.32, 1.72), (-0.32, 1.72)], 0.04, front=False, offset=0.01)
        # frontal: panel de la parrilla con los faros
        front_plate(m, body, 0.93, 0.62, 0.38, "black", (0.12, 0.13, 0.12), out=0.004, corner=0.02)
        if not lod:
            for k in range(9):
                front_plate(m, body, 0.93, 0.018, 0.36, "metal", None, x=-0.24 + k * 0.06, out=0.014)
        for s in (1, -1):
            front_light(m, body, 0.5 * s, 0.93, 0.09, "light", "chrome", out=0.006, segs=10 if lod else 20)
        # paragolpes de chapa negro
        zfr = body.front_z(0, 0.6)
        box(m, np.array([0, 0.6, zfr + 0.1]), (1.72, 0.14, 0.1), "black", (0.08, 0.08, 0.08))
        zrr = body.front_z(0, 0.62, False)
        box(m, np.array([0, 0.62, zrr - 0.06]), (1.5, 0.1, 0.08), "black", (0.08, 0.08, 0.08))
        # rueda de repuesto en el capó
        sp = wheel(R * 0.93, 0.18, "steel", "white", (0.86, 0.84, 0.76), lod=lod)
        M = np.array([[0, 1.0, 0], [-1.0, 0, 0], [0, 0, 1.0]])        # eje X -> eje Y (tumbada)
        m.merge(sp, M, offset=(0, 1.16 + 0.1, 1.25))
        if lod:
            return
        for s in (1, -1):
            front_light(m, body, 0.72 * s, 1.0, 0.035, "amber", "chrome", segs=12)
            front_plate(m, body, 0.98, 0.07, 0.14, "tail", None, front=False, x=0.74 * s, out=0.006)
            # retrovisor sobre la aleta
            x = 0.78
            cylinder(m, (x * s, 1.16, 1.55), (x * s, 1.42, 1.5), 0.012, "metal", None, segs=6)
            disc(m, np.array([x * s, 1.45, 1.48]), (0, 0, -1), 0.065, "metal", None, 14, thick=0.02)
            # bisagras de las puertas
            for yy in (0.82, 1.1):
                side_plate(m, body, 0.6, yy, 0.05, 0.06, "metal", None, side=s, out=0.01)
            # rejillas en el salpicadero
        for s in (1, -1):
            yy = body.top_y(0.8, 0.4 * s)
            plate(m, np.array([0.4 * s, yy + 0.004, 0.8]), (0, 1, 0), 0.22, 0.05, "black", None, thick=0.008, up=(0, 0, 1))
        number_plates(m, body, 0.75, 0.82)

    meta = dict(name="Todoterreno clásico (Land Rover)", category="Todoterreno", selectable=True, traffic=0.5,
                inspired="todoterreno británico de los 60 tipo 'Serie IIA'",
                desc="Cuadrado, alto y duro. Lento en asfalto, pero no se arruga ante nada.")
    phys = dict(mass=1400, maxSpeed=34, engine=7600, brake=10500, grip=1.08, downforce=0.8)
    return make("suv", meta, spec_fn, material, details, R, 1.31, axles, 0.19, "steel", phys, (0.42, 0.52, 0.4),
                {"paint2": (0.92, 0.91, 0.86)}, rim_kind="white", rim_color=(0.86, 0.84, 0.76),
                splits=[(roof_split, "paint", "paint2")])


# =================================================================== SUPERDEPORTIVO (tipo McLaren F1 de 1992)
@car
def build_f1futuro():
    zf, zr = 2.145, -2.145
    axles = (1.24, -1.48)
    R = 0.33

    def spec_fn():
        def w(z):
            return 0.91 * end_factor(z, 1.0, zf + 0.12, 2.0, 0.0) * end_factor(z, -1.95, zr - 0.04, 3.5, 0.0)
        s = BodySpec(zr + 0.01, zf - 0.01,
                     yb=prof([(zr, 0.2), (zr + 0.25, 0.13), (0, 0.12), (zf - 0.3, 0.13), (zf, 0.18)]),
                     w=lambda z: max(float(w(z)), 0.4),
                     yw=prof([(zr, 0.5), (-1.0, 0.47), (0.5, 0.45), (zf, 0.3)]),
                     yd=prof([(zr, 0.9), (-1.6, 0.86), (-0.9, 0.8), (0, 0.72), (0.75, 0.73), (1.4, 0.62), (zf - 0.08, 0.45),
                              (zf, 0.38)]),
                     crown=prof([(zr, 0.02), (-1.0, 0.04), (0.8, 0.03), (1.6, 0.06), (zf, 0.03)]),
                     nb=2.4, nt=2.6,
                     cab=prof([(-1.9, 0.0), (-1.75, 0.92), (-1.3, 1.0), (-0.75, 1.1), (-0.45, 1.135), (-0.2, 1.13),
                               (0.15, 1.04), (0.45, 0.92), (0.72, 0.8), (0.85, 0.0)]),
                     wr=lambda z: max(float(w(z)), 0.4) * 0.72,
                     wbelt=lambda z: max(float(w(z)), 0.4) * 0.86,
                     nc=3.0, rcrown=0.05)
        arches(s, axles, R, 1.12)
        return s

    def material(z, x, y, n, region, inf):
        if n[1] < -0.55 and y < 0.4:
            return ("under", None)
        return ("paint", None)

    def details(m, body, lod):
        front_window(m, body, [(-0.6, 0.8), (0.6, 0.8), (0.46, 1.07), (-0.46, 1.07)], 0.09, offset=0.01)
        side_windows(m, body, [[(-0.66, 0.82), (0.58, 0.8), (-0.1, 1.07), (-0.62, 1.09)]], 0.06)
        # ventanilla del motor (lamas) detrás de la cabina
        if not lod:
            for k in range(6):
                zz = -1.0 - k * 0.11
                yy = body.top_y(zz, 0)
                plate(m, np.array([0, yy + 0.004, zz]), body.top_normal(zz, 0), 0.5, 0.05, "black", None, thick=0.006, up=(0, 0, 1))
        # toma de aire en el techo (snorkel)
        secs = []
        for k in range(11):
            u = k / 10
            zz = -0.42 - 0.9 * u
            yy = body.top_y(zz, 0)
            secs.append(((0, yy + 0.05 * math.sin(math.pi * min(1, u * 1.6)) ** 0.5 - 0.01, zz), 0.1 * (1 - 0.4 * u), 0.06))
        loft_ellipses(m, secs, "paint", None, segs=12 if not lod else 6, n_exp=3)
        disc(m, np.array([0, secs[0][0][1] + 0.01, -0.41]), (0, 0, 1), 0.06, "black", None, 14, thick=0.004, ellipse=1.5)
        # tomas laterales negras
        side_windows(m, body, [[(-1.04, 0.36), (-0.72, 0.36), (-0.7, 0.66), (-0.96, 0.68)]], 0.05, kind="black", offset=0.006)
        # faros alargados y pilotos redondos
        for s in (1, -1):
            front_light(m, body, 0.5 * s, 0.47, 0.06, "light", "gloss", segs=10 if lod else 20, ellipse=2.0)
            for xx in (0.5, 0.7):
                front_light(m, body, xx * s, 0.72, 0.05, "tail", "gloss", front=False, segs=10 if lod else 16)
        # toma frontal y difusor
        front_plate(m, body, 0.3, 0.8, 0.09, "black", None, corner=0.04)
        front_plate(m, body, 0.3, 1.2, 0.12, "black", None, front=False, corner=0.02)
        if lod:
            return
        # alerón trasero (labio)
        zz = -2.0
        yy = body.top_y(zz, 0)
        loft_ellipses(m, [((0, yy + 0.005, zz + 0.1), 0.72, 0.006), ((0, yy + 0.03, zz - 0.04), 0.74, 0.012),
                          ((0, yy + 0.035, zz - 0.13), 0.74, 0.006)], "paint", None, segs=12, n_exp=6)
        for s in (1, -1):
            x = body.side_x(0.6, 0.78)
            mirror_on_stalk(m, (x, 0.8, 0.6), s, 0.08, (0.13, 0.07, 0.05), "paint", "black")
        number_plates(m, body, 0.4, 0.55, front=False)
        for s in (-0.12, 0.12):
            cylinder(m, (s, 0.32, zr + 0.05), (s, 0.32, zr - 0.08), 0.045, "chrome", None, segs=12)

    meta = dict(name="Superdeportivo (McLaren F1)", category="Superdeportivo", selectable=True, traffic=0.08,
                inspired="superdeportivo británico de 1992 tipo 'F1' de tres plazas",
                desc="El coche de calle más rápido de los 90: motor central y toma de aire en el techo.")
    phys = dict(mass=1140, maxSpeed=67, engine=14200, brake=15500, grip=1.62, downforce=2.8)
    return make("f1futuro", meta, spec_fn, material, details, R, 1.54, axles, 0.24, "race5", phys, (1.0, 0.45, 0.05),
                rim_kind="metal", rim_color=(0.62, 0.62, 0.64))


# =================================================================== FÓRMULA 1 (monoplaza moderno genérico)
@car
def build_f1():
    zf, zr = 2.62, -2.3
    axles = (1.75, -1.85)
    R = 0.36
    WX = 0.8

    def spec_fn():
        w = prof([(zr, 0.2), (-2.0, 0.24), (-1.5, 0.32), (-1.0, 0.5), (-0.5, 0.62), (0.0, 0.62), (0.3, 0.42), (0.7, 0.27),
                  (1.4, 0.2), (2.0, 0.13), (zf, 0.07)])
        s = BodySpec(zr + 0.01, zf - 0.01,
                     yb=prof([(zr, 0.1), (0, 0.06), (1.2, 0.1), (zf, 0.14)]),
                     w=w,
                     yw=prof([(zr, 0.3), (0, 0.3), (1.0, 0.36), (zf, 0.22)]),
                     yd=prof([(zr, 0.4), (-1.5, 0.42), (-0.6, 0.58), (0.0, 0.6), (0.5, 0.62), (1.2, 0.56), (2.0, 0.42),
                              (zf, 0.3)]),
                     crown=prof([(zr, 0.02), (-0.4, 0.05), (0.6, 0.06), (zf, 0.03)]),
                     nb=2.6, nt=2.2,
                     cab=prof([(-2.15, 0.0), (-2.05, 0.52), (-1.4, 0.62), (-0.7, 0.82), (-0.3, 0.98), (-0.12, 1.0),
                               (0.0, 0.95), (0.06, 0.0)]),
                     wr=prof([(-2.2, 0.08), (-1.4, 0.13), (-0.6, 0.17), (-0.15, 0.17), (0.06, 0.15)]),
                     wbelt=prof([(-2.2, 0.16), (-1.4, 0.22), (-0.6, 0.3), (0.06, 0.3)]),
                     nc=2.4, rcrown=0.05)
        return s

    def material(z, x, y, n, region, inf):
        if n[1] < -0.55:
            return ("carbon", None)
        if region == "cab" and z < -0.3 and abs(x) > 0.05 and y > 0.7:
            return ("paint2", None)
        if region in ("low", "deck") and abs(x) > 0.45 and -1.0 < z < 0.05 and y > 0.4:
            return ("paint2", None)       # franja blanca en los pontones
        return ("paint", None)

    def details(m, body, lod):
        segs = 10 if lod else 18
        # fondo plano y difusor
        box(m, np.array([0, 0.045, -0.6]), (1.36, 0.02, 2.3), "carbon")
        box(m, np.array([0, 0.09, -2.05]), (0.9, 0.12, 0.35), "carbon")
        # habitáculo: abertura negra, casco y halo
        yy = body.top_y(0.35, 0)
        patch(m, body.map_top(), rrect(-0.2, 0.2, 0.08, 0.72, 0.12, 2 if lod else 5), "black", None,
              rings=1 if lod else 3, offset=0.006, subdiv=1 if lod else 2)
        superellipsoid(m, np.array([0, yy + 0.1, 0.32]), (0.12, 0.13, 0.14), 2.0, 2.0, "paint2", (0.95, 0.85, 0.1),
                       segs=(10, 6) if lod else (18, 10))
        plate(m, np.array([0, yy + 0.13, 0.43]), norm(np.array([0, 0.2, 1.0])), 0.18, 0.06, "glass", None, thick=0.01,
              corner=0.02)
        if not lod:
            hp = [np.array([0.0, 0.92, 0.95])]
            for a in np.linspace(0, math.pi, 13):
                hp.append(np.array([0.22 * math.cos(a), 0.84, 0.3 - 0.32 * math.sin(a)]))
            pts = [np.array([0.0, 0.84, 0.92])] + [np.array([0.24 * math.cos(a), 0.86, 0.58 - 0.4 * math.sin(a)])
                                                    for a in np.linspace(0.0, math.pi, 15)]
            # halo: arco + pilar central
            arc = [np.array([0.24 * math.cos(a), 0.86, 0.5 - 0.38 * math.sin(a)]) for a in np.linspace(-0.25, math.pi + 0.25, 17)]
            arc[0][1] = arc[-1][1] = 0.62
            sweep(m, arc, 0.02, 0.016, "black", None, n_exp=2.4, segs=8)
            cylinder(m, (0, 0.62, 0.9), (0, 0.87, 0.6), 0.02, "black", None, segs=8)
        # caja de aire sobre la cabeza
        disc(m, np.array([0, body.top_y(-0.1, 0) - 0.03, -0.06]), (0, 0, 1), 0.09, "black", None, segs, thick=0.004, ellipse=0.9)
        # alerón delantero (3 planos) y derivas
        for k, (yy, zz, ch) in enumerate([(0.1, 2.62, 0.28), (0.15, 2.52, 0.18), (0.2, 2.44, 0.12)]):
            plate(m, np.array([0, yy, zz]), norm(np.array([0, 1, 0.25 + 0.25 * k])), 1.86, ch, "paint" if k else "carbon",
                  None, thick=0.012, up=(0, 0, 1))
            if lod:
                break
        for s in (1, -1):
            plate(m, np.array([0.93 * s, 0.18, 2.52]), (s, 0, 0), 0.34, 0.2, "carbon", None, thick=0.01)
        cylinder(m, (0, 0.24, 2.45), (0, 0.11, 2.52), 0.03, "carbon", None, segs=6)
        # alerón trasero con derivas y DRS
        plate(m, np.array([0, 0.86, -2.3]), norm(np.array([0, 1, 0.35])), 0.98, 0.26, "paint", None, thick=0.014, up=(0, 0, 1))
        plate(m, np.array([0, 0.96, -2.42]), norm(np.array([0, 1, 0.9])), 0.98, 0.12, "carbon", None, thick=0.012, up=(0, 0, 1))
        for s in (1, -1):
            plate(m, np.array([0.5 * s, 0.7, -2.32]), (s, 0, 0), 0.44, 0.48, "paint", None, thick=0.012)
        cylinder(m, (0, 0.42, -2.1), (0, 0.84, -2.28), 0.03, "carbon", None, segs=6)
        plate(m, np.array([0, 0.36, -2.35]), (0, 0, -1), 0.06, 0.06, "tail", None, thick=0.01)
        # suspensiones (triángulos) y retrovisores
        for z0 in axles:
            for s in (1, -1):
                for (yb_, yw_) in ((0.2, 0.22), (0.44, 0.5)):
                    for dz in (-0.18, 0.18):
                        cylinder(m, (0.18 * s if z0 > 0 else 0.24 * s, yb_, z0 + dz), (WX * s - 0.13 * s, yw_ + 0.0, z0), 0.012,
                                 "carbon", None, segs=5 if lod else 7)
                if lod:
                    break
        if lod:
            return
        for s in (1, -1):
            mirror_on_stalk(m, (0.34 * s, 0.62, 0.55), s, 0.14, (0.12, 0.05, 0.04), "paint", "carbon")
            # bocas de los pontones
            plate(m, np.array([0.48 * s, 0.42, 0.06]), (0, 0, 1), 0.24, 0.16, "black", None, thick=0.01, corner=0.05)
        # número
        for s in (1, -1):
            side_disc(m, body, -1.0, 0.68, 0.0001, "white", None, side=s)

    meta = dict(name="Fórmula 1", category="F1", selectable=True, traffic=0.0,
                inspired="monoplaza de Fórmula 1 actual (genérico, sin equipo)",
                desc="Monoplaza de carreras: agarre y frenada brutales, pero muy bajo.")
    phys = dict(mass=760, maxSpeed=72, engine=15000, brake=17000, grip=2.1, downforce=6.0)
    return make("f1", meta, spec_fn, material, details, R, 1.6, axles, 0.34, "f1", phys, (0.85, 0.05, 0.05),
                {"paint2": (0.95, 0.95, 0.95)}, rim_kind="black", rim_color=(0.1, 0.1, 0.1), wheel_x=WX, bounds_x=1.0,
                dense_extra=((0.3, 0.6), (-0.3, 0.6)))


# =================================================================== COCHES MODERNOS DEL TRÁFICO
def modern_spec(L, W, H, axles, R, style="hatch", yb0=0.2):
    """Carrocería moderna genérica. style: hatch (5 puertas), sedan (3 volúmenes), crossover (alto), van."""
    zf, zr = L / 2, -L / 2
    hw = W / 2

    def w(z):
        return hw * end_factor(z, zf - 0.32, zf + 0.06, 2.6, 0.0) * end_factor(z, zr + 0.25, zr - 0.04, 3.0, 0.0)
    belt = {"hatch": 0.98, "sedan": 0.95, "crossover": 1.08, "van": 1.12}[style]
    hood = {"hatch": 0.88, "sedan": 0.86, "crossover": 0.98, "van": 1.0}[style]
    if style == "sedan":
        cab = [(zr + 0.18, 0.0), (zr + 0.2, belt + 0.02), (zr + 0.55, belt + 0.06), (zr + 1.05, H - 0.04), (zr + 1.4, H),
               (0.3, H), (0.62, H - 0.08), (1.05, belt + 0.06), (1.2, belt - 0.02), (1.3, 0.0)]
    elif style == "van":
        cab = [(zr, 0.0), (zr + 0.02, H - 0.15), (zr + 0.1, H - 0.02), (zr + 0.3, H), (0.9, H), (1.1, H - 0.05),
               (1.55, belt + 0.12), (1.75, belt - 0.02), (1.82, 0.0)]
    else:
        cab = [(zr + 0.02, 0.0), (zr + 0.06, belt + 0.06), (zr + 0.26, H - 0.2), (zr + 0.5, H - 0.06), (zr + 0.85, H),
               (0.25, H), (0.55, H - 0.07), (1.0, belt + 0.1), (1.18, belt + 0.0), (1.3, 0.0)]
    s = BodySpec(zr + 0.01, zf - 0.01,
                 yb=prof([(zr, yb0 + 0.14), (zr + 0.25, yb0 + 0.02), (0, yb0), (zf - 0.25, yb0 + 0.03), (zf, yb0 + 0.14)]),
                 w=lambda z: max(float(w(z)), hw * 0.55),
                 yw=prof([(zr, belt - 0.3), (0, belt - 0.32), (zf, belt - 0.4)]),
                 yd=prof([(zr, belt - 0.04), (zr + 0.4, belt), (0, belt), (1.0, hood + 0.02), (zf - 0.15, hood - 0.1), (zf, hood - 0.25)]),
                 crown=prof([(zr, 0.05), (0, 0.04), (1.0, 0.06), (zf, 0.04)]), nb=2.8, nt=3.4,
                 cab=prof(cab),
                 wr=lambda z: max(float(w(z)), hw * 0.55) * 0.82,
                 wbelt=lambda z: max(float(w(z)), hw * 0.55) * 0.95,
                 nc=3.6, rcrown=0.06)
    arches(s, axles, R, 1.14)
    return s


def modern_details(m, body, lod, L, W, H, style, belt_kind="black"):
    zf, zr = L / 2, -L / 2
    belt = {"hatch": 0.98, "sedan": 0.95, "crossover": 1.08, "van": 1.12}[style]
    hood = {"hatch": 0.88, "sedan": 0.86, "crossover": 0.98, "van": 1.0}[style]
    yt = H - 0.08
    yb = belt + 0.03
    if style == "sedan":
        wins = [[(-0.05, yb), (0.98, yb), (0.48, yt - 0.02), (-0.05, yt)],
                [(zr + 1.15, yb + 0.02), (-0.15, yb), (-0.15, yt), (zr + 1.5, yt - 0.02)]]
        rear = [(-0.5, belt + 0.14), (0.5, belt + 0.14), (0.42, H - 0.1), (-0.42, H - 0.1)]
    elif style == "van":
        wins = [[(0.55, yb + 0.02), (1.42, yb + 0.02), (1.08, H - 0.32), (0.55, H - 0.32)]]
        rear = [(-0.55, belt + 0.1), (0.55, belt + 0.1), (0.55, H - 0.25), (-0.55, H - 0.25)]
    else:
        wins = [[(-0.02, yb), (0.95, yb), (0.4, yt - 0.02), (-0.02, yt)],
                [(zr + 0.62, yb), (-0.12, yb), (-0.12, yt), (zr + 0.9, yt - 0.03)]]
        rear = [(-0.5, belt + 0.1), (0.5, belt + 0.1), (0.42, H - 0.14), (-0.42, H - 0.14)]
    side_windows(m, body, wins, 0.05)
    ws = [(-W * 0.4, hood + 0.08), (W * 0.4, hood + 0.08), (W * 0.31, H - 0.07), (-W * 0.31, H - 0.07)]
    if style == "van":
        ws = [(-W * 0.36, belt + 0.14), (W * 0.36, belt + 0.14), (W * 0.32, H - 0.3), (-W * 0.32, H - 0.3)]
    if style == "van":
        front_window(m, body, ws, 0.08, offset=0.01)
    else:
        # parabrisas muy inclinado: se proyecta desde arriba (u = x, v = z)
        zb, zt = (1.13, 0.6) if style != "sedan" else (1.12, 0.66)
        out = rounded_poly([(-W * 0.39, zb), (W * 0.39, zb), (W * 0.31, zt), (-W * 0.31, zt)], 0.08, 2 if lod else 5)
        patch(m, body.map_top(), out, "glass", None, rings=1 if lod else 3, offset=0.03 if lod else 0.02,
              subdiv=1 if lod else 2)
    front_window(m, body, rear, 0.06, front=False, offset=0.01)
    # faros rasgados, parrilla, pilotos
    yl = hood - 0.12
    for s in (1, -1):
        front_plate(m, body, yl, 0.34, 0.09, "light", None, x=(W / 2 - 0.27) * s, out=0.004, corner=0.035)
        front_plate(m, body, belt - 0.05, 0.36, 0.1, "tail", None, front=False, x=(W / 2 - 0.25) * s, out=0.004, corner=0.03)
    front_plate(m, body, yl - 0.2, 0.62, 0.2, "gloss", None, out=0.003, corner=0.06)
    front_plate(m, body, 0.3, W * 0.66, 0.1, "black", None, out=0.003, corner=0.04)
    front_plate(m, body, 0.36, W * 0.7, 0.12, "black", None, front=False, out=0.003, corner=0.04)
    if lod:
        return
    number_plates(m, body, 0.42, belt - 0.24)
    for s in (1, -1):
        x = body.side_x(0.95 if style != "van" else 1.45, belt + 0.03)
        mirror_on_stalk(m, (x, belt + 0.05, 0.95 if style != "van" else 1.45), s, 0.05, (0.18, 0.1, 0.06), "paint", "black")
        for zz in (0.5, -0.45) if style != "van" else (0.8,):
            side_plate(m, body, zz, belt - 0.12, 0.14, 0.025, "black", None, side=s, out=0.008, corner=0.01)


def modern_car(cid, meta, phys, paint, L, W, H, axles, R, style="hatch", rims="alloy5", extra=None, colors=None, material=None,
               splits=None, mass_track=None):
    def spec_fn():
        return modern_spec(L, W, H, axles, R, style)

    def mat(z, x, y, n, region, inf):
        if material:
            r = material(z, x, y, n, region, inf)
            if r:
                return r
        if n[1] < -0.55 and y < 0.5:
            return ("under", None)
        if y < 0.4 and region in ("low", "deck") and abs(n[0]) > 0.5:
            return ("black", None) if style == "crossover" else ("paint", None)
        return ("paint", None)

    def details(m, body, lod):
        modern_details(m, body, lod, L, W, H, style)
        if extra:
            extra(m, body, lod)
    return make(cid, meta, spec_fn, mat, details, R, mass_track or W - 0.22, axles, 0.21, rims, phys, paint, colors,
                rim_kind="metal", splits=splits)


@car
def build_compacto():
    meta = dict(name="Compacto", category="Tráfico", selectable=False, traffic=5.0,
                inspired="compacto actual de 5 puertas", desc="")
    return modern_car("compacto", meta, dict(mass=1250, maxSpeed=48, engine=8200, brake=11500, grip=1.25, downforce=1.2),
                      (0.55, 0.57, 0.6), 4.15, 1.78, 1.48, (1.33, -1.28), 0.32, "hatch")


@car
def build_crossover():
    meta = dict(name="Crossover", category="Tráfico", selectable=False, traffic=4.0,
                inspired="SUV urbano actual", desc="")
    return modern_car("crossover", meta, dict(mass=1400, maxSpeed=46, engine=8600, brake=12000, grip=1.18, downforce=1.0),
                      (0.85, 0.85, 0.85), 4.3, 1.82, 1.62, (1.36, -1.3), 0.36, "crossover")


@car
def build_taxi():
    def extra(m, body, lod):
        yy = body.top_y(-0.3, 0)
        box(m, np.array([0, yy + 0.07, -0.3]), (0.5, 0.12, 0.16), "white", (0.95, 0.95, 0.95), bevel=0.02)
        if not lod:
            box(m, np.array([0, yy + 0.07, -0.3]), (0.42, 0.06, 0.168), "beacon", (0.1, 0.8, 0.2))
            for s in (1, -1):
                side_plate(m, body, 0.15, 0.62, 0.6, 0.1, "white", (0.1, 0.55, 0.25), side=s, out=0.006, corner=0.02)
    meta = dict(name="Taxi", category="Tráfico", selectable=False, traffic=1.0,
                inspired="taxi de Donostialdea (berlina blanca)", desc="")
    return modern_car("taxi", meta, dict(mass=1450, maxSpeed=48, engine=8400, brake=11500, grip=1.22, downforce=1.2),
                      (0.95, 0.95, 0.95), 4.7, 1.8, 1.47, (1.42, -1.38), 0.32, "sedan", extra=extra)


@car
def build_policia():
    def band(p):
        return min(0.07 - abs(p[1] - 0.68), abs(p[0]) - 0.75)

    def extra(m, body, lod):
        yy = body.top_y(-0.2, 0)
        box(m, np.array([0, yy + 0.05, -0.2]), (1.1, 0.08, 0.24), "black", (0.1, 0.1, 0.12), bevel=0.02)
        for s in (1, -1):
            box(m, np.array([0.3 * s, yy + 0.1, -0.2]), (0.42, 0.06, 0.2), "beacon", (0.1, 0.25, 1.0), bevel=0.02)
        if not lod:
            # damero azul marino en la banda roja
            for s in (1, -1):
                for k in range(14):
                    zz = -1.6 + k * 0.24
                    side_plate(m, body, zz, 0.68 + (0.035 if k % 2 else -0.035), 0.12, 0.07, "paint2", (0.05, 0.1, 0.35),
                               side=s, out=0.006)
    meta = dict(name="Ertzaintza", category="Tráfico", selectable=False, traffic=0.4,
                inspired="patrulla de la Ertzaintza (berlina blanca, banda roja y damero azul)", desc="")
    return modern_car("policia", meta, dict(mass=1500, maxSpeed=55, engine=10000, brake=12500, grip=1.3, downforce=1.3),
                      (0.95, 0.95, 0.95), 4.75, 1.84, 1.48, (1.44, -1.4), 0.33, "sedan", extra=extra,
                      colors={"paint2": (0.8, 0.05, 0.05)}, splits=[(band, "paint", "paint2")])


@car
def build_ambulancia():
    def extra(m, body, lod):
        yy = body.top_y(0.7, 0)
        box(m, np.array([0, yy + 0.06, 0.7]), (1.2, 0.1, 0.26), "beacon", (0.1, 0.3, 1.0), bevel=0.02)
        if not lod:
            for s in (1, -1):
                for k in range(10):
                    zz = -2.2 + k * 0.4
                    side_plate(m, body, zz, 0.85, 0.2, 0.18, "paint2", (0.1, 0.6, 0.25), side=s, out=0.006)
                side_plate(m, body, -0.6, 1.5, 0.4, 0.4, "white", (0.95, 0.95, 0.95), side=s, out=0.006)
                side_plate(m, body, -0.6, 1.5, 0.3, 0.08, "tail", (0.85, 0.05, 0.05), side=s, out=0.012)
                side_plate(m, body, -0.6, 1.5, 0.08, 0.3, "tail", (0.85, 0.05, 0.05), side=s, out=0.012)
    meta = dict(name="Ambulancia", category="Tráfico", selectable=False, traffic=0.25,
                inspired="ambulancia de Osakidetza (furgón amarillo y verde)", desc="")
    return modern_car("ambulancia", meta, dict(mass=2600, maxSpeed=42, engine=10500, brake=14500, grip=1.05, downforce=0.8),
                      (0.95, 0.85, 0.1), 5.6, 2.0, 2.45, (2.0, -1.6), 0.36, "van", rims="steel", extra=extra)


@car
def build_reparto():
    L = 5.2

    def extra(m, body, lod):
        # caja de carga blanca
        box(m, np.array([0, 1.6, -0.95]), (2.14, 2.2, 3.36), "paint2", (0.96, 0.96, 0.96), bevel=0.03)
        box(m, np.array([0, 0.45, -0.95]), (1.9, 0.18, 3.3), "under", None)
        if not lod:
            for s in (1, -1):
                box(m, np.array([0, 2.66, -0.95 + s * 1.62]), (2.16, 0.08, 0.06), "grey", (0.6, 0.6, 0.62))
            front_plate(m, body, 0.5, 1.9, 0.12, "black", None, front=False, out=0.02)
    meta = dict(name="Camión de reparto", category="Tráfico", selectable=False, traffic=1.0,
                inspired="camión ligero de reparto con caja", desc="")

    def mat(z, x, y, n, region, inf):
        return ("under", None) if z < 0.5 else None

    return modern_car("reparto", meta, dict(mass=3500, maxSpeed=36, engine=11000, brake=15000, grip=1.0, downforce=0.7),
                      (0.85, 0.25, 0.12), L, 2.0, 2.2, (1.6, -1.55), 0.38, "van", rims="steel", extra=extra, material=mat)


# =================================================================== exportación
ORDER = ["turismo", "berlina", "utilitario", "furgoneta", "f1", "f1futuro", "suv", "taxi", "policia", "ambulancia", "reparto",
         "compacto", "crossover"]     # el orden de los índices es parte del protocolo en red: añadir siempre al final


def write_index():
    cars = []
    for cid in ORDER:
        p = os.path.join(OUT, cid + ".json")
        if not os.path.exists(p):
            continue
        d = json.load(open(p))
        cars.append({"id": cid, "name": d["name"], "category": d["category"], "selectable": d["selectable"],
                     "trafficWeight": d.get("trafficWeight", 0.0), "description": d.get("description", "")})
    idx = {"cars": cars,
           "credits": "Modelos 3D propios generados por código (DataPipeline/build_cars.py), dominio público (CC0). "
                      "Inspirados en coches reales, sin marcas ni logotipos."}
    with open(os.path.join(OUT, "cars.json"), "w") as f:
        json.dump(idx, f, ensure_ascii=False, indent=1)
    print("cars.json:", len(cars), "coches")


def main(ids):
    os.makedirs(OUT, exist_ok=True)
    for cid in ids:
        CARS[cid]()
    write_index()


if __name__ == "__main__":
    main(sys.argv[1:] or list(CARS))
