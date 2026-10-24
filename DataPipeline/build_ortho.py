#!/usr/bin/env python3
"""
Hernani Racing - ortofoto para el suelo del juego.

Descarga la ortofoto más reciente de b5m (Diputación Foral de Gipuzkoa, servicio WMS gipuzkoa_ort_wms,
capa orto2025: vuelo de julio-agosto de 2025, píxel de 15 cm) y la guarda en teselas de 1024 x 1024 m,
una por cada tesela de terreno del juego, en
    Assets/HernaniRacing/Resources/HernaniOrtho/orto_<tx>_<tz>.jpg
La resolución por defecto es 0,5 m por píxel (2048 x 2048 px por tesela): la de 15 cm ocuparía unos 4.700
megapíxeles para todo el área, demasiado para un juego (y más para el navegador).

Uso:
    python build_ortho.py                 # orto2025, 0,5 m/px
    python build_ortho.py --layer orto2024 --res 1.0
    python build_ortho.py --force         # vuelve a descargar aunque ya existan

Licencia de la ortofoto: CC BY 4.0, © Diputación Foral de Gipuzkoa (b5m) / Gobierno Vasco.
Sólo necesita Python 3 y Pillow (pip install pillow).
"""
import argparse
import io
import json
import os
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

try:
    from PIL import Image
except ImportError:
    sys.exit("Falta Pillow: pip install pillow")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DATA = os.path.join(ROOT, "Assets", "HernaniRacing", "Resources", "HernaniData")
OUT = os.path.join(ROOT, "Assets", "HernaniRacing", "Resources", "HernaniOrtho")
WMS = "https://b5m.gipuzkoa.eus/ogc/wms/gipuzkoa_ort_wms"
UA = "HernaniRacing/1.0 (data pipeline)"


def fetch(url, tries=6):
    last = None
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=180) as r:
                data = r.read()
                ctype = r.headers.get("Content-Type", "")
            if not ctype.startswith("image/"):
                raise RuntimeError("respuesta no es una imagen: " + ctype + " " + data[:200].decode("utf-8", "replace"))
            return data
        except Exception as e:  # noqa: BLE001
            last = e
            time.sleep(3 + 4 * i)
    raise RuntimeError(str(last))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--layer", default="orto2025")
    ap.add_argument("--res", type=float, default=0.5, help="metros por píxel (0.5 = 2048 px por tesela)")
    ap.add_argument("--quality", type=int, default=85)
    ap.add_argument("--workers", type=int, default=5)
    ap.add_argument("--force", action="store_true")
    a = ap.parse_args()

    meta = json.load(open(os.path.join(DATA, "meta.json"), encoding="utf-8"))
    e0, n0 = meta["originE"], meta["originN"]
    tile = meta["tileCells"] * meta["cell"]                       # 1024 m
    tx_n = int((meta["gridX"] - 1) // meta["tileCells"])
    tz_n = int((meta["gridZ"] - 1) // meta["tileCells"])
    px = int(round(tile / a.res))
    if px > 4096:
        sys.exit("El WMS admite como máximo 4096 px por petición (res mínima %.2f m)" % (tile / 4096))
    os.makedirs(OUT, exist_ok=True)

    jobs = []
    for tz in range(tz_n):
        for tx in range(tx_n):
            path = os.path.join(OUT, "orto_%d_%d.jpg" % (tx, tz))
            if os.path.exists(path) and not a.force:
                continue
            minx, miny = e0 + tx * tile, n0 + tz * tile
            url = (WMS + "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=" + a.layer + "&STYLES="
                   "&CRS=EPSG:25830&BBOX=%.1f,%.1f,%.1f,%.1f&WIDTH=%d&HEIGHT=%d&FORMAT=image/jpeg"
                   % (minx, miny, minx + tile, miny + tile, px, px))
            jobs.append((tx, tz, path, url))

    print("Ortofoto %s a %.2f m/px: %d x %d teselas de %d px (%d por descargar)" % (a.layer, a.res, tx_n, tz_n, px, len(jobs)))

    def work(j):
        tx, tz, path, url = j
        raw = fetch(url)
        img = Image.open(io.BytesIO(raw)).convert("RGB")
        if img.size != (px, px):
            img = img.resize((px, px), Image.LANCZOS)
        tmp = path + ".tmp"
        img.save(tmp, "JPEG", quality=a.quality, optimize=True, progressive=False)
        os.replace(tmp, path)
        return tx, tz, os.path.getsize(path)

    done = 0
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        futs = [ex.submit(work, j) for j in jobs]
        for f in as_completed(futs):
            tx, tz, size = f.result()
            done += 1
            print("  [%2d/%d] orto_%d_%d.jpg  %4d KB" % (done, len(jobs), tx, tz, size // 1024), flush=True)

    info = {
        "source": "b5m - Diputación Foral de Gipuzkoa (WMS gipuzkoa_ort_wms)",
        "layer": a.layer,
        "metersPerPixel": a.res,
        "pixels": px,
        "tileMeters": tile,
        "tilesX": tx_n,
        "tilesZ": tz_n,
        "license": "CC BY 4.0 © Diputación Foral de Gipuzkoa (b5m) / Gobierno Vasco",
        "generated": time.strftime("%Y-%m-%dT%H:%M:%S"),
    }
    with open(os.path.join(OUT, "ortho.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, ensure_ascii=False, indent=1)
    print("Listo:", OUT)


if __name__ == "__main__":
    main()
