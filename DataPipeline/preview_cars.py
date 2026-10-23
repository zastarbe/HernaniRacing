#!/usr/bin/env python3
"""
Vista previa de los coches (render por software con numpy, sin Unity):
    python preview_cars.py turismo,berlina  salida.png  [--views 2] [--size 560x360]
Lee Resources/HernaniCars/<id>.json.
"""
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
CARS = os.path.join(HERE, "..", "Assets", "HernaniRacing", "Resources", "HernaniCars")


def look_at(eye, target, up=(0, 1, 0)):
    f = np.asarray(target, float) - eye; f /= np.linalg.norm(f)
    r = np.cross(f, up); r /= np.linalg.norm(r)
    u = np.cross(r, f)
    return r, u, f


def sky(dirs):
    """Entorno falso para reflejos: cielo azul claro arriba, horizonte claro, suelo gris."""
    y = dirs[..., 1]
    top = np.array([0.55, 0.68, 0.85]); hor = np.array([0.92, 0.93, 0.95]); gnd = np.array([0.32, 0.31, 0.3])
    t = np.clip(y, 0, 1)[..., None]
    g = np.clip(-y * 4, 0, 1)[..., None]
    c = hor * (1 - t) + top * t
    return c * (1 - g) + gnd * g


def render(parts, W=560, H=360, yaw=35.0, pitch=14.0, dist=None, paint=None, ss=2, fov=28.0, bg=True):
    """parts: lista de (V, N, subs, offset). Devuelve imagen PIL."""
    Ws, Hs = W * ss, H * ss
    allV = np.concatenate([p[0] + p[3] for p in parts])
    lo, hi = allV.min(0), allV.max(0)
    ctr = (lo + hi) / 2
    size = np.linalg.norm(hi - lo)
    if dist is None:
        dist = size * 1.0 / math.tan(math.radians(fov) / 2) * 0.52
    yr, pr = math.radians(yaw), math.radians(pitch)
    eye = ctr + dist * np.array([math.sin(yr) * math.cos(pr), math.sin(pr), math.cos(yr) * math.cos(pr)])
    r, u, f = look_at(eye, ctr)
    fpx = (Hs / 2) / math.tan(math.radians(fov) / 2)
    img = np.zeros((Hs, Ws, 3))
    if bg:
        yy = np.linspace(0, 1, Hs)[:, None, None]
        img[:] = np.array([0.80, 0.84, 0.9]) * (1 - yy) + np.array([0.55, 0.57, 0.6]) * yy
    zb = np.full((Hs, Ws), np.inf)
    L = np.array([-0.45, 0.85, 0.35]); L /= np.linalg.norm(L)

    def proj(P):
        d = P - eye
        x = d @ r; y = d @ u; z = d @ f
        return np.c_[Ws / 2 + fpx * x / z, Hs / 2 - fpx * y / z, z]

    # sombra en el suelo (elipse difuminada)
    gy = 0.0
    sh = np.zeros((Hs, Ws))
    gx = np.linspace(lo[0] - 0.3, hi[0] + 0.3, 40); gz = np.linspace(lo[2] - 0.3, hi[2] + 0.3, 60)
    GX, GZ = np.meshgrid(gx, gz)
    pts = np.c_[GX.ravel(), np.zeros(GX.size), GZ.ravel()]
    pp = proj(pts)
    wx = (hi[0] - lo[0]) / 2 + 0.05; wz = (hi[2] - lo[2]) / 2 + 0.05
    a = np.clip(1 - ((GX.ravel() - ctr[0]) / wx) ** 4 - ((GZ.ravel() - ctr[2]) / wz) ** 4, 0, 1) ** 0.6
    for (px, py, _), av in zip(pp, a):
        ix, iy = int(px), int(py)
        if 0 <= ix < Ws and 0 <= iy < Hs:
            rr = int(5 * ss)
            sh[max(0, iy - rr):iy + rr, max(0, ix - rr):ix + rr] = np.maximum(sh[max(0, iy - rr):iy + rr, max(0, ix - rr):ix + rr], av)
    img *= (1 - 0.55 * sh)[..., None]

    for V, N, subs, off in parts:
        Vw = V + off
        P = proj(Vw)
        for s in subs:
            T = np.asarray(s["t"]).reshape(-1, 3)
            if len(T) == 0:
                continue
            kind = s.get("kind", "")
            col = np.array(paint if (s.get("paint") and paint is not None) else s["color"], float)
            smooth = s.get("smooth", 0.3); metal = s.get("metal", 0.0)
            emis = kind in ("light", "tail", "amber", "beacon")
            for t in T:
                p = P[t]
                if (p[:, 2] <= 0.05).any():
                    continue
                # descartar caras traseras: normal geométrica (convenio Unity) frente a la cámara
                a3, b3, c3 = Vw[t]
                gn = np.cross(b3 - a3, c3 - a3)
                if np.dot(gn, eye - a3) <= 0:
                    continue
                x0, x1 = int(max(0, math.floor(p[:, 0].min()))), int(min(Ws - 1, math.ceil(p[:, 0].max())))
                y0, y1 = int(max(0, math.floor(p[:, 1].min()))), int(min(Hs - 1, math.ceil(p[:, 1].max())))
                if x1 < x0 or y1 < y0:
                    continue
                xs = np.arange(x0, x1 + 1) + 0.5; ys = np.arange(y0, y1 + 1) + 0.5
                X, Y = np.meshgrid(xs, ys)
                (ax, ay, az), (bx, by, bz), (cx, cy, cz) = p
                den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
                if abs(den) < 1e-9:
                    continue
                w0 = ((by - cy) * (X - cx) + (cx - bx) * (Y - cy)) / den
                w1 = ((cy - ay) * (X - cx) + (ax - cx) * (Y - cy)) / den
                w2 = 1 - w0 - w1
                ins = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
                if not ins.any():
                    continue
                # profundidad con corrección de perspectiva
                iz = w0 / az + w1 / bz + w2 / cz
                z = 1 / np.maximum(iz, 1e-9)
                sub = zb[y0:y1 + 1, x0:x1 + 1]
                ok = ins & (z < sub)
                if not ok.any():
                    continue
                n = (w0[..., None] / az * N[t[0]] + w1[..., None] / bz * N[t[1]] + w2[..., None] / cz * N[t[2]]) * z[..., None]
                n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-9)
                pw = (w0[..., None] / az * Vw[t[0]] + w1[..., None] / bz * Vw[t[1]] + w2[..., None] / cz * Vw[t[2]]) * z[..., None]
                vdir = eye - pw; vdir /= np.linalg.norm(vdir, axis=-1, keepdims=True)
                if emis:
                    c = np.clip(col * 1.15 + 0.15, 0, 1) * np.ones_like(n)
                else:
                    ndl = np.clip(n @ L, 0, 1)[..., None]
                    hvec = L + vdir; hvec /= np.linalg.norm(hvec, axis=-1, keepdims=True)
                    spec = np.clip((n * hvec).sum(-1), 0, 1)[..., None] ** (8 + 120 * smooth ** 2)
                    refl = 2 * (n * vdir).sum(-1, keepdims=True) * n - vdir
                    env = sky(refl)
                    fres = 0.04 + 0.96 * (1 - np.clip((n * vdir).sum(-1, keepdims=True), 0, 1)) ** 5
                    diff = col * (0.38 + 0.72 * ndl) * (1 - metal)
                    reflc = env * (col * metal + (1 - metal) * 1.0)
                    k = smooth * (metal + (1 - metal) * fres * 0.9)
                    c = diff * (1 - k) + reflc * k + spec * smooth * 0.6
                sub[ok] = z[ok]
                img[y0:y1 + 1, x0:x1 + 1][ok] = np.clip(c[ok], 0, 1)
    im = Image.fromarray((np.clip(img, 0, 1) ** (1 / 1.1) * 255).astype(np.uint8))
    if ss > 1:
        im = im.resize((W, H), Image.LANCZOS)
    return im


def car_parts(car, lod=False):
    body = car["bodyLod"] if lod and car.get("bodyLod") else car["body"]
    parts = [(np.array(body["v"]).reshape(-1, 3), np.array(body["n"]).reshape(-1, 3), body["sub"], np.zeros(3))]
    W = np.array(car["wheels"]).reshape(4, 3)
    for w in W:
        part = car["wheelL"] if w[0] < 0 else car["wheelR"]
        if lod and car.get("wheelLodL"):
            part = car["wheelLodL"] if w[0] < 0 else car["wheelLodR"]
        parts.append((np.array(part["v"]).reshape(-1, 3), np.array(part["n"]).reshape(-1, 3), part["sub"], w))
    # el render usa mano derecha: se refleja X (Unity es de mano izquierda) y se invierte el sentido
    out = []
    for V, N, subs, off in parts:
        V = V * [-1, 1, 1]; N = N * [-1, 1, 1]; off = off * [-1, 1, 1]
        subs2 = [dict(s, t=np.asarray(s["t"]).reshape(-1, 3)[:, ::-1].ravel()) for s in subs]
        out.append((V, N, subs2, off))
    return out


def sheet(ids, out, views=((35, 13), (215, 16)), W=520, H=330, lod=False, title=True):
    rows = []
    for cid in ids:
        car = json.load(open(os.path.join(CARS, cid + ".json")))
        ims = [render(car_parts(car, lod), W, H, yaw=yw, pitch=pt) for yw, pt in views]
        row = Image.new("RGB", (W * len(ims), H + (24 if title else 0)), (40, 42, 46))
        for k, im in enumerate(ims):
            row.paste(im, (k * W, 24 if title else 0))
        if title:
            d = ImageDraw.Draw(row)
            tris = sum(len(s["t"]) for s in car["body"]["sub"]) // 3 + 4 * sum(len(s["t"]) for s in car["wheelR"]["sub"]) // 3
            d.text((8, 5), f"{car['name']}  ({cid})  ·  {car['size'][2]:.2f} x {car['size'][0]:.2f} x {car['size'][1]:.2f} m  ·  {tris} triángulos"
                   + ("  · LOD" if lod else ""), fill=(235, 235, 235))
        rows.append(row)
    sheet_im = Image.new("RGB", (rows[0].width, sum(r.height for r in rows)))
    y = 0
    for r in rows:
        sheet_im.paste(r, (0, y)); y += r.height
    sheet_im.save(out)
    return out


if __name__ == "__main__":
    ids = sys.argv[1].split(",")
    out = sys.argv[2] if len(sys.argv) > 2 else "cars.png"
    sheet(ids, out, lod="--lod" in sys.argv)
    print(out)


def closeup(cid, out, yaw=20, pitch=8, W=700, H=500, fov=16):
    car = json.load(open(os.path.join(CARS, cid + ".json")))
    render(car_parts(car), W, H, yaw=yaw, pitch=pitch, fov=fov).save(out)


def gallery(ids, out, cols=4, W=480, H=300):
    """Cuadrícula con un dibujo de cada coche y su nombre (Docs/coches.png)."""
    from PIL import ImageFont
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", 17)
    except Exception:
        font = ImageFont.load_default()
    rows = (len(ids) + cols - 1) // cols
    sheet_img = Image.new("RGB", (cols * W, rows * (H + 30)), (32, 34, 40))
    for k, cid in enumerate(ids):
        car = json.load(open(os.path.join(CARS, cid + ".json")))
        img = render(car_parts(car), W, H, yaw=35, pitch=14)
        x, y = (k % cols) * W, (k // cols) * (H + 30)
        sheet_img.paste(img, (x, y))
        d = ImageDraw.Draw(sheet_img)
        d.text((x + 10, y + H + 6), car["name"], fill=(240, 240, 240), font=font)
    sheet_img.save(out)
    return out
