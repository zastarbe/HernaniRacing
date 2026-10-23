#!/usr/bin/env python3
"""
Generador procedural de coches para Hernani Racing (modelos propios, sin marcas ni logotipos, CC0).

Coordenadas de Unity: x a la derecha, y hacia arriba, z hacia delante (morro en +Z), metros, origen en el
suelo bajo el centro del coche. Convenio de caras de Unity: la normal geométrica cross(b-a, c-a) apunta hacia
fuera (es la que calcula Mesh.RecalculateNormals), así que todos los triángulos se orientan con esa regla.

La carrocería se construye como un "lofting": una serie de secciones transversales a lo largo del coche.
Cada sección tiene la parte baja (superelipse: laterales, bajos y capó/maletero) y el habitáculo encima
(lunas, pilares y techo). Los pasos de rueda se recortan en los bajos. Después se añaden los detalles
(faros, parachoques, rejillas, retrovisores, matrículas, alerones, ...) como piezas sueltas.
"""
import math
import math

import numpy as np

# ---------------------------------------------------------------- materiales
# kind -> color por defecto, suavidad (0..1), metálico (0..1)
KINDS = {
    "paint":  ((0.8, 0.1, 0.1), 0.72, 0.0),
    "paint2": ((0.93, 0.92, 0.88), 0.65, 0.0),     # segundo color fijo (techo blanco, bitono, ...)
    "glass":  ((0.16, 0.2, 0.25), 0.96, 0.0),
    "chrome": ((0.86, 0.87, 0.9), 0.93, 1.0),
    "metal":  ((0.72, 0.73, 0.76), 0.65, 0.85),
    "gold":   ((0.78, 0.6, 0.22), 0.7, 0.9),
    "black":  ((0.06, 0.06, 0.07), 0.35, 0.0),     # plásticos negros, juntas
    "gloss":  ((0.03, 0.03, 0.035), 0.85, 0.0),    # negro brillante
    "rubber": ((0.07, 0.07, 0.075), 0.12, 0.0),
    "under":  ((0.05, 0.05, 0.055), 0.05, 0.0),    # bajos y pasos de rueda
    "grey":   ((0.35, 0.36, 0.38), 0.3, 0.0),
    "light":  ((1.0, 0.97, 0.88), 0.9, 0.0),       # faros (emisivos)
    "tail":   ((0.75, 0.04, 0.03), 0.85, 0.0),     # pilotos traseros (emisivos tenues)
    "amber":  ((1.0, 0.55, 0.05), 0.85, 0.0),      # intermitentes
    "white":  ((0.95, 0.95, 0.94), 0.4, 0.0),
    "plate":  ((0.96, 0.96, 0.96), 0.4, 0.0),
    "blue":   ((0.05, 0.2, 0.62), 0.5, 0.0),
    "fabric": ((0.16, 0.16, 0.17), 0.05, 0.0),
    "carbon": ((0.09, 0.09, 0.1), 0.6, 0.0),
    "beacon": ((0.1, 0.35, 1.0), 0.8, 0.0),        # rotativo azul (emisivo)
}


def norm(v):
    v = np.asarray(v, float)
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.maximum(n, 1e-12)


class Mesh:
    """Vértices + normales + triángulos agrupados por material (kind, color)."""

    def __init__(self):
        self.V, self.N = [], []
        self.groups = {}       # (kind, color tuple or None) -> list of index arrays
        self.nv = 0

    def add(self, V, N, T, kind, color=None):
        V = np.asarray(V, float).reshape(-1, 3)
        N = np.asarray(N, float).reshape(-1, 3)
        T = np.asarray(T, int).reshape(-1, 3)
        if len(T) == 0:
            return
        self.V.append(V); self.N.append(N)
        key = (kind, None if color is None else tuple(round(float(c), 3) for c in color))
        self.groups.setdefault(key, []).append(T + self.nv)
        self.nv += len(V)

    def add_tris_split(self, V, N, T, kinds):
        """Mismos vértices, triángulos con material propio cada uno (kinds: lista de (kind, color))."""
        V = np.asarray(V, float).reshape(-1, 3)
        N = np.asarray(N, float).reshape(-1, 3)
        T = np.asarray(T, int).reshape(-1, 3)
        self.V.append(V); self.N.append(N)
        by = {}
        for t, k in zip(T, kinds):
            by.setdefault(k, []).append(t)
        for (kind, color), ts in by.items():
            key = (kind, None if color is None else tuple(round(float(c), 3) for c in color))
            self.groups.setdefault(key, []).append(np.array(ts, int) + self.nv)
        self.nv += len(V)

    def merge(self, other, M=None, offset=(0, 0, 0)):
        if other.nv == 0:
            return
        V = np.concatenate(other.V); N = np.concatenate(other.N)
        if M is not None:
            V = V @ np.asarray(M, float).T
            N = norm(N @ np.linalg.inv(np.asarray(M, float)))
        V = V + np.asarray(offset, float)
        flip = M is not None and np.linalg.det(M) < 0
        for key, ts in other.groups.items():
            T = np.concatenate(ts)
            if flip:
                T = T[:, ::-1]
            self.groups.setdefault(key, []).append(T + self.nv)
        self.V.append(V); self.N.append(N)
        self.nv += len(V)

    def mirrored_x(self):
        m = Mesh()
        m.merge(self, np.diag([-1.0, 1.0, 1.0]))
        return m

    def arrays(self):
        V = np.concatenate(self.V) if self.V else np.zeros((0, 3))
        N = np.concatenate(self.N) if self.N else np.zeros((0, 3))
        return V, N

    def tri_count(self):
        return sum(len(t) for ts in self.groups.values() for t in ts)

    def to_json(self, colors):
        """colors: kind -> rgb (sobrescribe los de KINDS). paint=1 para la pintura elegible."""
        V, N = self.arrays()
        # quitar vértices no usados no hace falta; redondeo para ocupar menos
        subs = []
        for (kind, col), ts in sorted(self.groups.items(), key=lambda kv: (kv[0][0], str(kv[0][1]))):
            T = np.concatenate(ts)
            base, smooth, metal = KINDS.get(kind, KINDS["grey"])
            c = col if col is not None else colors.get(kind, base)
            subs.append({"color": [round(float(x), 3) for x in c], "paint": 1 if kind == "paint" else 0,
                         "kind": kind, "smooth": smooth, "metal": metal, "t": T.ravel().tolist()})
        return {"v": np.round(V, 4).ravel().tolist(), "n": np.round(N, 3).ravel().tolist(), "sub": subs}


# ---------------------------------------------------------------- utilidades geométricas
def orient(V, T, outward):
    """Orienta cada triángulo para que cross(b-a, c-a) apunte como 'outward' (vector por triángulo)."""
    V = np.asarray(V); T = np.asarray(T).copy()
    a, b, c = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    n = np.cross(b - a, c - a)
    bad = (n * outward).sum(1) < 0
    T[bad] = T[bad][:, ::-1]
    return T


def smooth_normals(V, T):
    V = np.asarray(V); N = np.zeros_like(V)
    a, b, c = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    fn = np.cross(b - a, c - a)
    for k in range(3):
        np.add.at(N, T[:, k], fn)
    return norm(N)


def grid_tris(rows, cols, closed_cols=False, closed_rows=False):
    """Triángulos de una rejilla rows x cols (índice r*cols + c). Devuelve (T, quad_id por triángulo)."""
    T, Q = [], []
    rmax = rows if closed_rows else rows - 1
    cmax = cols if closed_cols else cols - 1
    for r in range(rmax):
        r2 = (r + 1) % rows
        for c in range(cmax):
            c2 = (c + 1) % cols
            a, b, cc, d = r * cols + c, r2 * cols + c, r2 * cols + c2, r * cols + c2
            T.append((a, b, cc)); T.append((a, cc, d)); Q += [(r, c), (r, c)]
    return np.array(T, int), Q


def rot_to(a, b):
    """Matriz de rotación que lleva el vector unitario a al b."""
    a = norm(a); b = norm(b)
    v = np.cross(a, b); c = float(np.dot(a, b))
    if c < -0.999999:
        p = np.array([1.0, 0, 0]) if abs(a[0]) < 0.9 else np.array([0, 1.0, 0])
        v = norm(np.cross(a, p))
        return 2 * np.outer(v, v) - np.eye(3)
    K = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + K + K @ K / (1 + c)


def rot_y(deg):
    r = math.radians(deg); c, s = math.cos(r), math.sin(r)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def rot_x(deg):
    r = math.radians(deg); c, s = math.cos(r), math.sin(r)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def rot_z(deg):
    r = math.radians(deg); c, s = math.cos(r), math.sin(r)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


# ---------------------------------------------------------------- primitivas
def box(m, center, size, kind, color=None, R=None, bevel=0.0):
    """Caja (con chaflán opcional en las aristas) centrada en 'center'."""
    hx, hy, hz = np.asarray(size, float) / 2
    R = np.eye(3) if R is None else np.asarray(R)
    if bevel <= 0:
        dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]
        V, N, T = [], [], []
        for d in dirs:
            d = np.array(d, float)
            ax = [i for i in range(3) if d[i] == 0]
            u = np.zeros(3); u[ax[0]] = 1; v = np.zeros(3); v[ax[1]] = 1
            h = np.array([hx, hy, hz])
            c0 = d * h
            quad = [c0 - u * h - v * h, c0 + u * h - v * h, c0 + u * h + v * h, c0 - u * h + v * h]
            k = len(V)
            V += quad; N += [d] * 4
            T += [(k, k + 1, k + 2), (k, k + 2, k + 3)]
        V = np.array(V) @ R.T + center; N = np.array(N) @ R.T
        T = orient(V, np.array(T), N[np.array(T)[:, 0]])
        m.add(V, N, T, kind, color)
        return
    # caja biselada: superelipsoide de exponente alto
    superellipsoid(m, center, (hx, hy, hz), 10, 10, kind, color, R, segs=(12, 8))


def superellipsoid(m, center, radii, e1, e2, kind, color=None, R=None, segs=(16, 10)):
    nu, nv = segs
    R = np.eye(3) if R is None else np.asarray(R)
    rx, ry, rz = radii
    V, N = [], []

    def sp(w, e):
        return np.sign(w) * np.abs(w) ** (2.0 / e)
    for i in range(nv + 1):
        ph = -math.pi / 2 + math.pi * i / nv
        for j in range(nu):
            th = 2 * math.pi * j / nu
            cp, sph, ct, st = math.cos(ph), math.sin(ph), math.cos(th), math.sin(th)
            x = rx * sp(cp, e1) * sp(ct, e2); y = ry * sp(sph, e1); z = rz * sp(cp, e1) * sp(st, e2)
            V.append((x, y, z))
            # normal aproximada (gradiente de la superelipse)
            nx = sp(cp, 4 - e1 if e1 < 4 else 2) * sp(ct, 4 - e2 if e2 < 4 else 2) / max(rx, 1e-6)
            N.append((x / rx ** 2, y / ry ** 2, z / rz ** 2))
    V = np.array(V)
    T, _ = grid_tris(nv + 1, nu, closed_cols=True)
    N = smooth_normals(V, T)
    Vw = V @ R.T + center
    out = (V[T].mean(1)) @ R.T
    T = orient(Vw, T, out)
    N = smooth_normals(Vw, T)
    m.add(Vw, N, T, kind, color)


def cylinder(m, p0, p1, r, kind, color=None, segs=16, caps=True, r1=None, smooth=True):
    p0 = np.asarray(p0, float); p1 = np.asarray(p1, float)
    r1 = r if r1 is None else r1
    ax = p1 - p0; L = np.linalg.norm(ax); d = ax / max(L, 1e-9)
    R = rot_to((0, 0, 1), d)
    ang = np.linspace(0, 2 * math.pi, segs, endpoint=False)
    ring = np.c_[np.cos(ang), np.sin(ang), np.zeros(segs)]
    V0 = (ring * r) @ R.T + p0; V1 = (ring * r1) @ R.T + p1
    V = np.r_[V0, V1]
    Nr = ring @ R.T
    N = np.r_[Nr, Nr]
    T = []
    for j in range(segs):
        j2 = (j + 1) % segs
        T += [(j, j2, segs + j2), (j, segs + j2, segs + j)]
    T = np.array(T)
    out = V[T].mean(1) - (p0 + p1) / 2 - d * ((V[T].mean(1) - p0) @ d)[:, None]
    T = orient(V, T, out)
    if not smooth:
        m.add(V, N, T, kind, color)
    else:
        m.add(V, N, T, kind, color)
    if caps:
        for pc, rr, sgn in ((p0, r, -1), (p1, r1, 1)):
            Vc = np.r_[(ring * rr) @ R.T + pc, [pc]]
            Tc = np.array([(j, (j + 1) % segs, segs) for j in range(segs)])
            Nc = np.tile(d * sgn, (segs + 1, 1))
            Tc = orient(Vc, Tc, Nc[:len(Tc)])
            m.add(Vc, Nc, Tc, kind, color)


def disc(m, center, normal, r, kind, color=None, segs=20, thick=0.012, rim_kind=None, ellipse=1.0, up=None):
    """Pieza plana redonda (faro, piloto, tapacubos) pegada a una superficie, algo saliente."""
    n = norm(normal)
    R = rot_to((0, 0, 1), n)
    if up is not None:
        # gira el disco alrededor de n para alinear su eje 'y' con 'up'
        yv = R @ np.array([0, 1.0, 0]); upp = norm(np.asarray(up, float) - n * np.dot(up, n))
        ang = math.atan2(np.dot(np.cross(yv, upp), n), np.dot(yv, upp))
        R = rot_axis(n, ang) @ R
    ang = np.linspace(0, 2 * math.pi, segs, endpoint=False)
    ring = np.c_[np.cos(ang) * ellipse, np.sin(ang), np.zeros(segs)] * r
    top = ring @ R.T + center + n * thick
    bot = ring @ R.T + center - n * 0.01
    Vt = np.r_[top, [center + n * (thick * 1.25)]]
    Tt = np.array([(j, (j + 1) % segs, segs) for j in range(segs)])
    Tt = orient(Vt, Tt, np.tile(n, (segs, 1)))
    m.add(Vt, np.tile(n, (segs + 1, 1)), Tt, kind, color)
    # canto
    Vs = np.r_[top, bot]
    Ts = []
    for j in range(segs):
        j2 = (j + 1) % segs
        Ts += [(j, j2, segs + j2), (j, segs + j2, segs + j)]
    Ts = np.array(Ts)
    Ns = np.r_[ring @ R.T, ring @ R.T]
    Ns = norm(Ns)
    Ts = orient(Vs, Ts, Ns[Ts[:, 0]])
    m.add(Vs, Ns, Ts, rim_kind or kind, color if rim_kind is None else None)


def rot_axis(axis, ang):
    axis = norm(axis); x, y, z = axis
    c, s = math.cos(ang), math.sin(ang); C = 1 - c
    return np.array([[c + x * x * C, x * y * C - z * s, x * z * C + y * s],
                     [y * x * C + z * s, c + y * y * C, y * z * C - x * s],
                     [z * x * C - y * s, z * y * C + x * s, c + z * z * C]])


def plate(m, center, normal, w, h, kind, color=None, thick=0.01, up=(0, 1, 0), corner=0.0):
    """Placa rectangular (matrícula, piloto, rejilla) pegada a una superficie."""
    n = norm(normal)
    upv = norm(np.asarray(up, float) - n * np.dot(up, n))
    rt = np.cross(upv, n)
    if corner > 0:
        pts = []
        for cx, cy, a0 in ((1, 1, 0), (-1, 1, 90), (-1, -1, 180), (1, -1, 270)):
            for k in range(5):
                a = math.radians(a0 + 90 * k / 4)
                pts.append((cx * (w / 2 - corner) + corner * math.cos(a), cy * (h / 2 - corner) + corner * math.sin(a)))
    else:
        pts = [(w / 2, h / 2), (-w / 2, h / 2), (-w / 2, -h / 2), (w / 2, -h / 2)]
    pts = np.array(pts)
    k = len(pts)
    top = center + n * thick + np.outer(pts[:, 0], rt) + np.outer(pts[:, 1], upv)
    bot = center - n * 0.01 + np.outer(pts[:, 0], rt) + np.outer(pts[:, 1], upv)
    Vt = np.r_[top, [center + n * thick]]
    Tt = np.array([(j, (j + 1) % k, k) for j in range(k)])
    Tt = orient(Vt, Tt, np.tile(n, (k, 1)))
    m.add(Vt, np.tile(n, (k + 1, 1)), Tt, kind, color)
    Vs, Ns, Ts = [], [], []
    for j in range(k):
        j2 = (j + 1) % k
        e = top[j2] - top[j]
        sn = norm(np.cross(e, n))
        if np.dot(sn, (top[j] + top[j2]) / 2 - center) < 0:
            sn = -sn
        b = len(Vs)
        Vs += [top[j], top[j2], bot[j2], bot[j]]; Ns += [sn] * 4
        Ts += [(b, b + 1, b + 2), (b, b + 2, b + 3)]
    Vs = np.array(Vs); Ns = np.array(Ns); Ts = np.array(Ts)
    Ts = orient(Vs, Ts, Ns[Ts[:, 0]])
    m.add(Vs, Ns, Ts, kind, color)


def revolve_x(m, prof, kind_fn, segs=32, center=(0, 0, 0)):
    """Revoluciona un perfil [(x, r), ...] alrededor del eje X. kind_fn(i) -> (kind, color) por tramo i."""
    prof = np.asarray(prof, float)
    ang = np.linspace(0, 2 * math.pi, segs, endpoint=False)
    rows = len(prof)
    V = np.zeros((rows, segs, 3))
    for i, (x, r) in enumerate(prof):
        V[i, :, 0] = x; V[i, :, 1] = r * np.cos(ang); V[i, :, 2] = r * np.sin(ang)
    V = V.reshape(-1, 3)
    T, Q = grid_tris(rows, segs, closed_cols=True)
    # orientación: hacia fuera del eje si el perfil avanza en +x con r>0 ... se decide por segmento
    out = []
    for (r_, c_), t in zip(Q, T):
        p = V[t].mean(0)
        (x0, r0), (x1, r1) = prof[r_], prof[r_ + 1]
        tng = np.array([x1 - x0, r1 - r0])
        nrm2 = np.array([-tng[1], tng[0]])   # normal del perfil (a la izquierda del avance)
        radial = norm(np.array([0, p[1], p[2]]))
        out.append(np.array([nrm2[0], 0, 0]) + radial * nrm2[1])
    out = np.array(out)
    T = orient(V, T, out)
    # normales: suaves dentro de cada tramo de mismo material, con aristas vivas entre tramos
    kinds = [kind_fn(r_) for (r_, c_) in Q]
    # partir por tramos para no suavizar entre materiales distintos
    by = {}
    for t, k, (r_, c_) in zip(T, kinds, Q):
        by.setdefault((k, r_), []).append(t)
    for ((kind, color), r_), ts in by.items():
        ts = np.array(ts)
        idx = np.unique(ts)
        remap = {v: i for i, v in enumerate(idx)}
        Vl = V[idx]; Tl = np.vectorize(remap.get)(ts)
        (x0, r0), (x1, r1) = prof[r_], prof[r_ + 1]
        tng = np.array([x1 - x0, r1 - r0]); nrm2 = norm(np.array([-tng[1], tng[0]]))
        radial = norm(np.c_[np.zeros(len(Vl)), Vl[:, 1], Vl[:, 2]])
        Nl = np.c_[np.full(len(Vl), nrm2[0]), radial[:, 1] * nrm2[1], radial[:, 2] * nrm2[1]]
        # orientar la normal como la cara
        a, b, c = Vl[Tl[:, 0]], Vl[Tl[:, 1]], Vl[Tl[:, 2]]
        fn = np.cross(b - a, c - a)
        if (fn * Nl[Tl[:, 0]]).sum() < 0:
            Nl = -Nl
        m.add(Vl + np.asarray(center), norm(Nl), Tl, kind, color)


def loft_ellipses(m, sections, kind, color=None, segs=14, cap=True, n_exp=2.0):
    """Tubo por secciones elípticas/superelípticas: sections = [(center xyz, half_w (x), half_h (y)), ...] a lo largo de z."""
    rows = len(sections)
    ang = np.linspace(0, 2 * math.pi, segs, endpoint=False)
    c, s = np.cos(ang), np.sin(ang)
    e = 2.0 / n_exp
    cx = np.sign(c) * np.abs(c) ** e; sy = np.sign(s) * np.abs(s) ** e
    V = []
    for (ctr, hw, hh) in sections:
        ctr = np.asarray(ctr, float)
        for k in range(segs):
            V.append(ctr + np.array([cx[k] * hw, sy[k] * hh, 0]))
    V = np.array(V)
    T, Q = grid_tris(rows, segs, closed_cols=True)
    out = np.array([V[t].mean(0) - np.asarray(sections[r][0], float) for t, (r, _) in zip(T, Q)])
    out[:, 2] = 0
    T = orient(V, T, out)
    N = smooth_normals(V, T)
    m.add(V, N, T, kind, color)
    if cap:
        for r, sgn in ((0, -1), (rows - 1, 1)):
            ring = V[r * segs:(r + 1) * segs]
            ctr = np.asarray(sections[r][0], float)
            Vc = np.r_[ring, [ctr]]
            Tc = np.array([(j, (j + 1) % segs, segs) for j in range(segs)])
            nn = np.array([0, 0, sgn], float)
            Tc = orient(Vc, Tc, np.tile(nn, (segs, 1)))
            m.add(Vc, np.tile(nn, (segs + 1, 1)), Tc, kind, color)


def extrude_poly(m, poly2d, axis_u, axis_v, origin, depth_dir, depth, kind, color=None):
    """Extruye un polígono 2D (convexo o en estrella desde su centroide) a lo largo de depth_dir."""
    P = np.asarray(poly2d, float)
    u = np.asarray(axis_u, float); v = np.asarray(axis_v, float); o = np.asarray(origin, float)
    d = norm(depth_dir) * depth
    k = len(P)
    A = o + np.outer(P[:, 0], u) + np.outer(P[:, 1], v)
    B = A + d
    cA = A.mean(0); cB = B.mean(0)
    for ring, cc, nn in ((A, cA, -norm(d)), (B, cB, norm(d))):
        Vc = np.r_[ring, [cc]]
        Tc = np.array([(j, (j + 1) % k, k) for j in range(k)])
        Tc = orient(Vc, Tc, np.tile(nn, (k, 1)))
        m.add(Vc, np.tile(nn, (k + 1, 1)), Tc, kind, color)
    Vs, Ns, Ts = [], [], []
    for j in range(k):
        j2 = (j + 1) % k
        e = A[j2] - A[j]
        sn = norm(np.cross(e, d))
        if np.dot(sn, (A[j] + A[j2]) / 2 - cA) < 0:
            sn = -sn
        b = len(Vs)
        Vs += [A[j], A[j2], B[j2], B[j]]; Ns += [sn] * 4
        Ts += [(b, b + 1, b + 2), (b, b + 2, b + 3)]
    Vs = np.array(Vs); Ns = np.array(Ns); Ts = np.array(Ts)
    Ts = orient(Vs, Ts, Ns[Ts[:, 0]])
    m.add(Vs, Ns, Ts, kind, color)


# ---------------------------------------------------------------- perfiles
def pchip(xs, ys):
    """Interpolador cúbico monótono (sin oscilaciones) por tramos."""
    xs = np.asarray(xs, float); ys = np.asarray(ys, float)
    h = np.diff(xs); d = np.diff(ys) / h
    m = np.zeros_like(ys)
    for k in range(1, len(xs) - 1):
        if d[k - 1] * d[k] > 0:
            w1 = 2 * h[k] + h[k - 1]; w2 = h[k] + 2 * h[k - 1]
            m[k] = (w1 + w2) / (w1 / d[k - 1] + w2 / d[k])
    m[0] = d[0]; m[-1] = d[-1]

    def f(x):
        x = np.clip(np.asarray(x, float), xs[0], xs[-1])
        k = np.clip(np.searchsorted(xs, x) - 1, 0, len(xs) - 2)
        t = (x - xs[k]) / h[k]
        h00 = 2 * t ** 3 - 3 * t ** 2 + 1; h10 = t ** 3 - 2 * t ** 2 + t
        h01 = -2 * t ** 3 + 3 * t ** 2; h11 = t ** 3 - t ** 2
        return h00 * ys[k] + h10 * h[k] * m[k] + h01 * ys[k + 1] + h11 * h[k] * m[k + 1]
    return f


def prof(table):
    """table: [(z, valor), ...] -> función continua."""
    t = sorted(table)
    return pchip([a for a, _ in t], [b for _, b in t])


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


# ---------------------------------------------------------------- carrocería por secciones
class BodySpec:
    """
    Funciones de z (todas en metros):
      yb   bajos (altura del fondo)       w    media anchura máxima de la parte baja
      yw   altura de la anchura máxima    yd   altura de la línea de cintura (hombro) en el lateral
      crown abombado del capó/techo del maletero en el centro sobre yd
      nb, nt exponentes de superelipse de la parte baja (abajo / arriba): 2 redondo, 6+ cuadrado
      cab  altura del techo en el centro (si <= yd+crown no hay habitáculo en esa sección)
      wr   media anchura del techo   wbelt media anchura del habitáculo en la cintura   nc exponente del techo
    wheels: [(z, radio, ancho)] para recortar pasos de rueda (arco = radio * arch).
    """

    def __init__(self, z0, z1, **f):
        self.z0, self.z1 = z0, z1
        self.f = f
        self.arches = []
        self.arch_shape = "round"

    def val(self, name, z, default=None):
        g = self.f.get(name, default)
        if callable(g):
            return float(g(z))
        return float(g)


def section(spec, z, Kb=8, Kt=10, Kc=14):
    """Puntos de media sección (x>=0) desde el centro de los bajos hasta el centro del techo + etiquetas."""
    yb = spec.val("yb", z); w = spec.val("w", z); yw = spec.val("yw", z); yd = spec.val("yd", z)
    crown = spec.val("crown", z, 0.02)
    nb = spec.val("nb", z, 3.0); nt = spec.val("nt", z, 3.0)
    cab = spec.val("cab", z, 0.0); wr = spec.val("wr", z, 0.5); wbelt = spec.val("wbelt", z, w * 0.9)
    nc = spec.val("nc", z, 4.0); rcrown = spec.val("rcrown", z, 0.03)
    # recorte de pasos de rueda
    for (zw, R, arch, yc) in spec.arches:
        dz = z - zw
        ra = R * arch
        if abs(dz) < ra:
            if spec.arch_shape == "square":
                ya = yc + ra * 0.92 * (1 - (abs(dz) / ra) ** 6) ** 0.5
            else:
                ya = yc + math.sqrt(ra * ra - dz * dz)
            yb = max(yb, ya)
    yb = min(yb, yw - 0.03)
    ytop = yd + crown
    pts, lab = [], []
    # parte baja inferior: de (0, yb) a (w, yw)
    for k in range(Kb):
        t = -math.pi / 2 + (math.pi / 2) * k / Kb
        c, s = math.cos(t), math.sin(t)
        x = w * abs(c) ** (2 / nb); y = yw + (yw - yb) * (-(abs(s) ** (2 / nb)))
        pts.append((x, y)); lab.append("low")
    # parte baja superior: de (w, yw) hasta el hombro (x = wbelt) o hasta el centro si no hay habitáculo
    def top_pt(t):
        c, s = math.cos(t), math.sin(t)
        return (w * abs(c) ** (2 / nt), yw + (ytop - yw) * abs(s) ** (2 / nt))
    # t* donde x = wbelt
    wb = min(wbelt, w * 0.999)
    tstar = math.acos(min(1.0, (wb / w) ** (nt / 2)))
    for k in range(Kt):
        t = tstar * k / Kt
        pts.append(top_pt(t)); lab.append("low")
    # habitáculo (o resto del capó)
    xs0, ys0 = top_pt(tstar)
    hcab = cab - ys0
    blend = float(smoothstep(0.0, 0.10, hcab))
    for k in range(Kc + 1):
        u = k / Kc
        # capó/maletero: seguir la superelipse de t* a pi/2
        t = tstar + (math.pi / 2 - tstar) * u
        dx, dy = top_pt(t)
        # habitáculo: superelipse con cizalla (caída de lunas)
        tc = (math.pi / 2) * u
        cc, ss = math.cos(tc), math.sin(tc)
        a = wr; b = max(hcab, 0.0)
        cxv = a * abs(cc) ** (2 / nc) + (wb - wr) * (1 - abs(ss) ** (2 / nc))
        cyv = ys0 + b * abs(ss) ** (2 / nc) + rcrown * (1 - (cxv / max(wr, 1e-3)) ** 2 if cxv < wr else 0)
        x = dx * (1 - blend) + cxv * blend
        y = dy * (1 - blend) + cyv * blend
        pts.append((max(0.0, x), y))
        lab.append("cab" if blend > 0.02 else "deck")
    pts[-1] = (0.0, pts[-1][1])
    return np.array(pts), lab, dict(yb=yb, ytop=ytop, ys0=ys0, cab=cab, wr=wr, wb=wb, blend=blend)


def stations(z0, z1, n, dense=()):
    """Reparto de secciones a lo largo de z, más densas en los extremos y en las zonas 'dense' [(z, ancho)]."""
    u = np.linspace(0, 1, 4000)
    zz = z0 + (z1 - z0) * u
    dens = np.ones_like(zz)
    L = z1 - z0
    dens += 2.5 * np.exp(-((zz - z0) / (0.06 * L)) ** 2) + 2.5 * np.exp(-((zz - z1) / (0.06 * L)) ** 2)
    for zc, wd in dense:
        dens += 1.5 * np.exp(-((zz - zc) / wd) ** 2)
    cdf = np.cumsum(dens); cdf = (cdf - cdf[0]) / (cdf[-1] - cdf[0])
    return np.interp(np.linspace(0, 1, n), cdf, zz)


class Body:
    """Resultado del lofting: rejilla de puntos y funciones para colocar detalles sobre la superficie."""

    def __init__(self, spec, n_st=90, Kb=8, Kt=10, Kc=14, dense=()):
        self.spec = spec
        self.zs = stations(spec.z0, spec.z1, n_st, dense)
        self.secs = []
        self.info = []
        for z in self.zs:
            p, lab, inf = section(spec, z, Kb, Kt, Kc)
            self.secs.append(p); self.info.append(inf)
        self.lab = lab
        self.Kb, self.Kt, self.Kc = Kb, Kt, Kc

    def loop(self, i):
        """Sección completa (bucle cerrado) en la estación i: derecha y luego izquierda."""
        p = self.secs[i]
        right = p
        left = p[1:-1][::-1] * np.array([-1, 1])
        return np.r_[right, left]

    def build(self, m, material, splits=None):
        """material(z, x, y, n, region, info) -> (kind, color). region: low/deck/cab/cap."""
        S = len(self.zs)
        loops = [self.loop(i) for i in range(S)]
        P = len(loops[0])
        half = len(self.secs[0])
        V = np.zeros((S, P, 3))
        for i in range(S):
            V[i, :, 0] = loops[i][:, 0]; V[i, :, 1] = loops[i][:, 1]; V[i, :, 2] = self.zs[i]
        Vf = V.reshape(-1, 3)
        T, Q = grid_tris(S, P, closed_cols=True)
        # orientación: hacia fuera desde el eje (0, y medio)
        ymid = np.array([(inf["yb"] + inf["ytop"]) / 2 for inf in self.info])
        cen = Vf[T].mean(1)
        out = cen.copy(); out[:, 2] = 0
        out[:, 1] = cen[:, 1] - ymid[[q[0] for q in Q]]
        # la normal real decide, usamos la del quad para mayor robustez
        a, b, c = Vf[T[:, 0]], Vf[T[:, 1]], Vf[T[:, 2]]
        fn = np.cross(b - a, c - a)
        # signo global: el quad del techo en la mitad del coche debe mirar hacia arriba
        imid = S // 2
        jtop = half - 1
        k = [n for n, q in enumerate(Q) if q == (imid, jtop - 1)][0]
        sgn = 1 if fn[k][1] > 0 else -1
        if sgn < 0:
            T = T[:, ::-1]
        N = smooth_normals(Vf, T)
        kinds = []
        regions = []
        for t, (i, j) in zip(T, Q):
            jj = j if j < half else P - j       # índice simétrico en la media sección
            jj = min(jj, half - 1)
            region = self.lab[min(jj, len(self.lab) - 1)]
            pc = Vf[t].mean(0)
            a, b, c = Vf[t[0]], Vf[t[1]], Vf[t[2]]
            n = norm(np.cross(b - a, c - a))
            kinds.append(material(pc[2], pc[0], pc[1], n, region, self.info[i]))
        for fn, k_from, k_to in (splits or ()):
            Vf, N, T, kinds = split_by_levelset(Vf, N, T, kinds, fn, k_from, k_to)
        m.add_tris_split(Vf, N, T, kinds)
        # tapas delantera y trasera (planas, con normal propia)
        for i, sgn_z in ((0, -1), (S - 1, 1)):
            ring = V[i]
            ctr = np.array([0, ring[:, 1].mean(), self.zs[i]])
            Vc = np.r_[ring, [ctr]]
            Tc = np.array([(j, (j + 1) % P, P) for j in range(P)])
            nn = np.array([0, 0, sgn_z], float)
            Tc = orient(Vc, Tc, np.tile(nn, (P, 1)))
            kinds = [material(self.zs[i], Vc[t].mean(0)[0], Vc[t].mean(0)[1], nn, "cap", self.info[i]) for t in Tc]
            Nc = np.tile(nn, (P + 1, 1))
            for fn, k_from, k_to in (splits or ()):
                Vc, Nc, Tc, kinds = split_by_levelset(Vc, Nc, Tc, kinds, fn, k_from, k_to)
            m.add_tris_split(Vc, Nc, Tc, kinds)

    # ---- consultas de superficie analíticas (para colocar detalles): se evalúa la sección en cualquier z
    def sec_at(self, z):
        key = round(float(z), 5)
        c = self.__dict__.setdefault("_cache", {})
        if key not in c:
            p, _, _ = section(self.spec, key, 10, 14, 20)
            if len(c) > 20000:
                c.clear()
            c[key] = p
        return c[key]

    def loop_at(self, z):
        p = self.sec_at(z)
        return np.r_[p, p[1:-1][::-1] * np.array([-1, 1])]

    def front_z(self, x, y, front=True):
        """z de la superficie delantera (o trasera) en (x, y); None si (x, y) cae fuera del coche."""
        zend = self.zs[-1] if front else self.zs[0]
        if point_in_poly(self.loop_at(zend), x, y):
            return zend
        # buscar una z interior recorriendo hacia el centro
        zin = None
        for k in range(1, 41):
            zz = zend + (self.zs[len(self.zs) // 2] - zend) * k / 40
            if point_in_poly(self.loop_at(zz), x, y):
                zin = zz
                break
        if zin is None:
            return None
        a, b = zin, zend
        for _ in range(24):
            mid = (a + b) / 2
            if point_in_poly(self.loop_at(mid), x, y):
                a = mid
            else:
                b = mid
        return (a + b) / 2

    def side_x(self, z, y):
        p = self.sec_at(z)
        best = -1.0
        for a in range(len(p) - 1):
            (x0, y0), (x1, y1) = p[a], p[a + 1]
            if (y0 - y) * (y1 - y) <= 0 and abs(y1 - y0) > 1e-9:
                best = max(best, x0 + (x1 - x0) * (y - y0) / (y1 - y0))
        if best < 0:
            SIDE_MISS[0] += 1
            best = float(p[np.argmin(np.abs(p[:, 1] - y)), 0])
        return best

    def top_y(self, z, x):
        p = self.sec_at(z)
        x = min(abs(x), float(p[:, 0].max()) - 1e-4)
        best = -1.0
        for a in range(len(p) - 1):
            (x0, y0), (x1, y1) = p[a], p[a + 1]
            if (x0 - x) * (x1 - x) <= 0 and abs(x1 - x0) > 1e-9:
                best = max(best, y0 + (y1 - y0) * (x - x0) / (x1 - x0))
        return best

    def front_normal(self, x, y, front=True, h=0.02):
        z0 = self.front_z(x, y, front)
        if z0 is None:
            return np.array([0, 0, 1.0 if front else -1.0])
        zx = self.front_z(x + h, y, front); zy = self.front_z(x, y + h, front)
        if zx is None:
            zx2 = self.front_z(x - h, y, front)
            zx = None if zx2 is None else z0 + (z0 - zx2)
        if zy is None:
            zy2 = self.front_z(x, y - h, front)
            zy = None if zy2 is None else z0 + (z0 - zy2)
        if z0 is None or zx is None or zy is None:
            return np.array([0, 0, 1.0 if front else -1.0])
        tx = np.array([h, 0, zx - z0]); ty = np.array([0, h, zy - z0])
        n = norm(np.cross(tx, ty))
        if (n[2] > 0) != front:
            n = -n
        return n

    def side_normal(self, z, y, h=0.02):
        x0 = self.side_x(z, y)
        tz = np.array([self.side_x(z + h, y) - x0, 0, h]); ty = np.array([self.side_x(z, y + h) - x0, h, 0])
        n = norm(np.cross(ty, tz))
        if n[0] < 0:
            n = -n
        return n

    def top_normal(self, z, x, h=0.02):
        y0 = self.top_y(z, x)
        tx = np.array([h, self.top_y(z, x + h) - y0, 0]); tz = np.array([0, self.top_y(z + h, x) - y0, h])
        n = norm(np.cross(tz, tx))
        if n[1] < 0:
            n = -n
        return n

    # ---- mapas de superficie para parches (lunas, franjas, rótulos)
    def map_side(self, side=1):
        def f(u, v):            # u = z, v = y
            x = self.side_x(u, v); n = self.side_normal(u, v)
            p = np.array([x, v, u])
            if side < 0:
                p[0] = -p[0]; n = n * np.array([-1, 1, 1])
            return p, n
        return f

    def map_front(self, front=True):
        def f(u, v):            # u = x, v = y
            z = self.front_z(u, v, front)
            k = 0
            while z is None and k < 60:
                u *= 0.97; v -= 0.002; k += 1
                z = self.front_z(u, v, front)
                FRONT_MISS[0] += 1
            if z is None:
                z = self.zs[-1] if front else self.zs[0]
            return np.array([u, v, z]), self.front_normal(u, v, front)
        return f

    def map_top(self):
        def f(u, v):            # u = x, v = z
            return np.array([u, self.top_y(v, u), v]), self.top_normal(v, u)
        return f


def split_by_levelset(V, N, T, kinds, fn, k_from, k_to):
    """Corta los triángulos de material k_from por la curva fn(p)=0: donde fn>0 pasan a k_to (borde nítido)."""
    V = list(np.asarray(V)); N = list(np.asarray(N))
    s = np.array([fn(p) for p in V])
    newT, newK = [], []
    edge_cache = {}

    def cut(a, b):
        key = (min(a, b), max(a, b))
        if key in edge_cache:
            return edge_cache[key]
        t = s[a] / (s[a] - s[b])
        V.append(V[a] + (V[b] - V[a]) * t)
        nn = N[a] + (N[b] - N[a]) * t
        N.append(nn / max(np.linalg.norm(nn), 1e-9))
        edge_cache[key] = len(V) - 1
        return edge_cache[key]
    for t, k in zip(T, kinds):
        if k[0] != k_from:
            newT.append(tuple(t)); newK.append(k); continue
        sv = s[list(t)]
        pos = sv > 0
        if pos.all() or (~pos).all():
            newT.append(tuple(t)); newK.append((k_to, None) if pos.all() else k); continue
        # el vértice solitario
        idx = [0, 1, 2]
        lone = [i for i in idx if pos[i] != pos[(i + 1) % 3] and pos[i] != pos[(i + 2) % 3]][0]
        a, b, c = t[lone], t[(lone + 1) % 3], t[(lone + 2) % 3]
        pab, pac = cut(a, b), cut(a, c)
        k_lone = (k_to, None) if pos[lone] else k
        k_rest = k if pos[lone] else (k_to, None)
        newT += [(a, pab, pac), (pab, b, c), (pab, c, pac)]
        newK += [k_lone, k_rest, k_rest]
    return np.array(V), np.array(N), np.array(newT, int), newK


def rounded_poly(pts, r, n=5):
    """Contorno de un polígono (sentido cualquiera) con las esquinas redondeadas de radio r."""
    pts = np.asarray(pts, float)
    out = []
    k = len(pts)
    for i in range(k):
        p0, p1, p2 = pts[i - 1], pts[i], pts[(i + 1) % k]
        d0 = p0 - p1; d2 = p2 - p1
        l0, l2 = np.linalg.norm(d0), np.linalg.norm(d2)
        rr = min(r, l0 * 0.45, l2 * 0.45)
        a = p1 + d0 / l0 * rr; b = p1 + d2 / l2 * rr
        for t in np.linspace(0, 1, n):
            # Bézier cuadrática a-p1-b
            out.append((1 - t) ** 2 * a + 2 * (1 - t) * t * p1 + t ** 2 * b)
    return np.array(out)


def rrect(u0, u1, v0, v1, r, n=5):
    return rounded_poly([(u0, v0), (u1, v0), (u1, v1), (u0, v1)], r, n)


PATCH_MAXLEN = [0.08]


def patch(m, mapfn, outline, kind, color=None, rings=4, offset=0.007, subdiv=3):
    """Parche que sigue la superficie (luna, franja, rótulo): 'outline' en coordenadas (u, v) del mapa.
    Los triángulos se limitan a PATCH_MAXLEN para que no se 'hundan' bajo la chapa en superficies curvas."""
    outline = np.asarray(outline, float)
    ml = PATCH_MAXLEN[0]
    # subdividir el contorno para que siga bien las curvas
    pts = []
    for i in range(len(outline)):
        a, b = outline[i], outline[(i + 1) % len(outline)]
        nseg = max(subdiv, int(math.ceil(np.linalg.norm(b - a) / ml)))
        for t in np.linspace(0, 1, nseg, endpoint=False):
            pts.append(a + (b - a) * t)
    pts = np.array(pts)
    k = len(pts)
    ctr = pts.mean(0)
    rad = float(np.max(np.linalg.norm(pts - ctr, axis=1)))
    rings = max(rings, int(math.ceil(rad / ml)))
    uv = []
    for rI in range(rings):
        s = 1 - rI / rings
        uv += list(ctr + (pts - ctr) * s)
    uv.append(ctr)
    V, N = [], []
    for u, v in uv:
        p, n = mapfn(u, v)
        V.append(p + n * offset); N.append(n)
    V = np.array(V); N = np.array(N)
    T = []
    for rI in range(rings - 1):
        for j in range(k):
            a, b = rI * k + j, rI * k + (j + 1) % k
            c, d = (rI + 1) * k + (j + 1) % k, (rI + 1) * k + j
            T += [(a, b, c), (a, c, d)]
    cI = rings * k
    for j in range(k):
        T.append(((rings - 1) * k + j, (rings - 1) * k + (j + 1) % k, cI))
    T = np.array(T)
    T = orient(V, T, N[T[:, 0]])
    m.add(V, N, T, kind, color)


SIDE_MISS = [0]
FRONT_MISS = [0]


def point_in_poly(poly, x, y):
    c = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        xi, yi = poly[i]; xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi:
            c = not c
        j = i
    return c


# ---------------------------------------------------------------- ruedas
def wheel(R, width, style="alloy5", rim_kind="metal", rim_color=None, whitewall=False, segs=36, lod=False):
    """Rueda derecha (cara exterior hacia +x), centro en el origen, eje X."""
    m = Mesh()
    hw = width / 2
    rr = R * (0.62 if style not in ("hubcap", "steel") else 0.58)   # radio de la llanta
    if style == "f1":
        rr = R * 0.5
    if lod:
        segs = 14
    # neumático: perfil (x, r) desde la cara interior a la exterior pasando por la banda de rodadura
    side_bulge = 0.012
    prof_t = [(-hw * 0.9, rr * 0.98), (-hw - side_bulge * 0.4, rr + (R - rr) * 0.45), (-hw * 0.92, R - 0.02),
              (-hw * 0.7, R), (hw * 0.7, R), (hw * 0.92, R - 0.02), (hw + side_bulge * 0.4, rr + (R - rr) * 0.45),
              (hw * 0.9, rr * 0.98)]
    if lod:
        prof_t = [(-hw, rr), (-hw, R - 0.02), (-hw * 0.8, R), (hw * 0.8, R), (hw, R - 0.02), (hw, rr)]
    ww0 = None
    if whitewall and not lod:
        # banda blanca en el flanco exterior: se intercala un tramo
        a = (hw + side_bulge * 0.4, rr + (R - rr) * 0.45)
        prof_t = prof_t[:6] + [(hw + side_bulge * 0.4, rr + (R - rr) * 0.75), (hw + side_bulge * 0.5, rr + (R - rr) * 0.5),
                               (hw + side_bulge * 0.3, rr + (R - rr) * 0.22), (hw * 0.9, rr * 0.98)]
        ww0 = 7
    def tk(i):
        if ww0 is not None and i == ww0:
            return ("white", None)
        return ("rubber", None)
    revolve_x(m, prof_t, tk, segs)
    # llanta
    rk = (rim_kind, rim_color)
    face_x = hw * 0.78
    if style == "hubcap":
        # llanta de chapa pintada + tapacubos cromado abombado
        prof_r = [(face_x - 0.06, rr * 0.98), (face_x - 0.01, rr * 0.97), (face_x, rr * 0.9), (face_x - 0.015, rr * 0.75),
                  (face_x + 0.005, rr * 0.62), (face_x + 0.03, rr * 0.45), (face_x + 0.045, rr * 0.2), (face_x + 0.05, 0.0)]
        revolve_x(m, prof_r, lambda i: rk if i < 3 else ("chrome", None), segs)
    elif style == "steel":
        prof_r = [(face_x - 0.06, rr * 0.98), (face_x, rr * 0.95), (face_x - 0.01, rr * 0.8), (face_x - 0.05, rr * 0.7),
                  (face_x - 0.05, rr * 0.35), (face_x - 0.03, rr * 0.3), (face_x - 0.02, rr * 0.18), (face_x - 0.018, 0.0)]
        revolve_x(m, prof_r, lambda i: rk if i < 5 else ("metal", (0.35, 0.36, 0.37)), segs)
        if not lod:
            # tuercas
            for k in range(5):
                a = 2 * math.pi * k / 5
                c = np.array([face_x - 0.03, rr * 0.25 * math.cos(a), rr * 0.25 * math.sin(a)])
                cylinder(m, c, c + np.array([0.025, 0, 0]), 0.014, "metal", None, segs=6)
    else:
        # llanta de aleación: aro + cubo + radios
        prof_r = [(face_x - 0.07, rr * 0.99), (face_x, rr * 0.97), (face_x - 0.005, rr * 0.86), (face_x - 0.06, rr * 0.84)]
        revolve_x(m, prof_r, lambda i: rk, segs)
        # fondo oscuro
        prof_b = [(face_x - 0.07, rr * 0.84), (face_x - 0.075, 0.0)]
        revolve_x(m, prof_b, lambda i: ("black", (0.1, 0.1, 0.11)), segs)
        hub_r = rr * (0.22 if style != "f1" else 0.3)
        prof_h = [(face_x - 0.07, hub_r), (face_x - 0.005, hub_r), (face_x + 0.002, hub_r * 0.5), (face_x + 0.004, 0.0)]
        revolve_x(m, prof_h, lambda i: rk if i < 2 else ("metal", (0.55, 0.56, 0.58)), max(12, segs // 2))
        nsp = {"alloy5": 5, "mesh": 10, "multi": 8, "f1": 10, "race5": 5, "snow": 6}.get(style, 5)
        if lod:
            nsp = min(nsp, 5)
        spw = {"alloy5": 0.22, "mesh": 0.07, "multi": 0.12, "f1": 0.08, "race5": 0.16, "snow": 0.2}.get(style, 0.2)
        for k in range(nsp):
            a = 2 * math.pi * k / nsp
            d = np.array([0, math.cos(a), math.sin(a)])
            tng = np.array([0, -math.sin(a), math.cos(a)])
            r0, r1 = hub_r * 0.9, rr * 0.86
            wdt0 = rr * spw * 0.9; wdt1 = rr * spw * (0.55 if style != "mesh" else 1.0)
            x0, x1 = face_x - 0.045, face_x - 0.004
            # radio como prisma trapezoidal
            q = [(r0, -wdt0 / 2), (r1, -wdt1 / 2), (r1, wdt1 / 2), (r0, wdt0 / 2)]
            P2 = [d * r + tng * s for r, s in q]
            Vb = [np.array([x0, 0, 0]) + p for p in P2] + [np.array([x1, 0, 0]) + p for p in P2]
            Vb = np.array(Vb)
            faces = [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
            cen = Vb.mean(0)
            for f in faces:
                quad = Vb[list(f)]
                n = norm(np.cross(quad[1] - quad[0], quad[2] - quad[0]))
                if np.dot(n, quad.mean(0) - cen) < 0:
                    n = -n
                T = orient(quad, np.array([(0, 1, 2), (0, 2, 3)]), np.tile(n, (2, 1)))
                m.add(quad, np.tile(n, (4, 1)), T, rim_kind, rim_color)
            if style == "mesh" and not lod:
                # segundo cruce de radios (aspecto de malla)
                pass
        if not lod and style != "f1":
            for k in range(5 if style != "mesh" else 6):
                a = 2 * math.pi * (k + 0.5) / 5
                c = np.array([face_x - 0.004, hub_r * 0.62 * math.cos(a), hub_r * 0.62 * math.sin(a)])
                cylinder(m, c, c + np.array([0.012, 0, 0]), 0.011, "chrome", None, segs=6)
    # pinza de freno (sólo detrás de la llanta, se ve entre los radios)
    if not lod and style not in ("hubcap", "steel"):
        box(m, np.array([face_x - 0.1, rr * 0.55, -rr * 0.1]), (0.05, rr * 0.35, rr * 0.45), "paint2" if False else "grey", (0.6, 0.08, 0.06) if style in ("f1", "race5", "multi", "mesh") else (0.4, 0.4, 0.42))
    return m


def sweep(m, path, hw, hh, kind, color=None, n_exp=4.0, up=(0, 1, 0), segs=12, cap=True, scale=None):
    """Barra a lo largo de 'path' (N x 3) con sección superelíptica (hw horizontal, hh vertical)."""
    path = np.asarray(path, float)
    n = len(path)
    ang = np.linspace(0, 2 * math.pi, segs, endpoint=False)
    c, s = np.cos(ang), np.sin(ang)
    e = 2.0 / n_exp
    cx = np.sign(c) * np.abs(c) ** e; sy = np.sign(s) * np.abs(s) ** e
    V = []
    upv = np.asarray(up, float)
    for i in range(n):
        t = path[min(i + 1, n - 1)] - path[max(i - 1, 0)]
        t = norm(t)
        side = norm(np.cross(upv, t)) if abs(np.dot(upv, t)) < 0.99 else norm(np.cross((1, 0, 0), t))
        u2 = np.cross(t, side)
        k = 1.0 if scale is None else scale[i]
        for j in range(segs):
            V.append(path[i] + side * cx[j] * hw * k + u2 * sy[j] * hh * k)
    V = np.array(V)
    T, Q = grid_tris(n, segs, closed_cols=True)
    out = np.array([V[t].mean(0) - path[r] for t, (r, _) in zip(T, Q)])
    T = orient(V, T, out)
    N = smooth_normals(V, T)
    m.add(V, N, T, kind, color)
    if cap:
        for r, sgn in ((0, -1), (n - 1, 1)):
            ring = V[r * segs:(r + 1) * segs]
            t = norm(path[min(r + 1, n - 1)] - path[max(r - 1, 0)]) * sgn
            Vc = np.r_[ring, [path[r]]]
            Tc = np.array([(j, (j + 1) % segs, segs) for j in range(segs)])
            Tc = orient(Vc, Tc, np.tile(t, (segs, 1)))
            m.add(Vc, np.tile(t, (segs + 1, 1)), Tc, kind, color)


def end_factor(z, z_start, z_end, p=2.5, fmin=0.0):
    """Redondeo en planta/alzado: 1 antes de z_start, cae como superelipse hasta z_end."""
    L = z_end - z_start
    t = np.clip((z - z_start) / L, 0, 1)
    return np.maximum((1 - t ** p) ** (1 / p), fmin)
