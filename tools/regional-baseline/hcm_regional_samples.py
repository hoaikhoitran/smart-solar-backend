#!/usr/bin/env python3
"""Deterministic equal-area sample manifest for the 2025 Ho Chi Minh City (province code 79) regional solar baseline.

Input: the OCHA HDX COD-AB Viet Nam ADM1 GeoJSON (``vnm_admin1.geojson`` inside ``vnm_admin_boundaries.geojson.zip``),
source General Statistics Office of Vietnam (GSO), valid_on 2025-09-25, licence CC BY-IGO. The file hash is checked against
``--expected-sha256`` so a different download never silently produces a different manifest.

Method (no third-party packages, so the result can be reproduced anywhere with CPython 3.9+):

1. Take the feature with ``adm1_pcode == VN79`` (every polygon of the MultiPolygon, islands included; holes respected).
2. Project WGS84 (CRS84 lon/lat) to an ellipsoidal Lambert Azimuthal Equal-Area projection centred on the city
   (EPSG method 9820, Snyder 1987 eq. 3-12..3-13, 24-..); equal-area means planar areas are true ellipsoidal areas.
3. Lay a square grid of ``--cell-km`` cells over the projected bounding box (cells anchored at projected (0, 0)).
4. Clip every polygon ring to every candidate cell (Sutherland-Hodgman against the convex cell; signed shoelace area,
   so holes subtract). The clipped area of a cell is its raw weight.
5. Cells whose clipped area is below ``--min-cell-fraction`` of a full cell are not sampled: their area is added to the
   nearest retained cell (great-circle distance between representative points), so the total area is preserved and no
   sliver costs a provider call. Every merge is listed in the manifest.
6. The sample point of a retained cell is a point INSIDE the clipped land/water area of that cell (the centroid when it is
   inside, otherwise the midpoint of the widest scanline interval through the largest clipped piece), then inverse-projected.
7. Weights = area / total area, rounded to 9 decimals and adjusted so they sum to exactly 1 (the residual goes to the
   largest weight). The manifest records the hashes of the source, the sample CSV and the canonical sample content.

Usage:
  python3 -I hcm_regional_samples.py --admin1 vnm_admin1.geojson --expected-sha256 <hex> --cell-km 15 --out-dir <dir>
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import math
import os
import sys
from typing import List, Optional, Sequence, Tuple

TOOL_VERSION = "hcm-regional-samples-v1"
PCODE = "VN79"

# WGS84
A = 6378137.0
F = 1 / 298.257223563
E2 = F * (2 - F)
E = math.sqrt(E2)

Point = Tuple[float, float]
Ring = List[Point]


def _q(phi: float) -> float:
    s = math.sin(phi)
    return (1 - E2) * (s / (1 - E2 * s * s) - (1 / (2 * E)) * math.log((1 - E * s) / (1 + E * s)))


class Laea:
    """Ellipsoidal Lambert Azimuthal Equal-Area, oblique aspect (EPSG 9820)."""

    def __init__(self, lat0: float, lon0: float):
        self.lon0 = math.radians(lon0)
        phi0 = math.radians(lat0)
        self.qp = _q(math.pi / 2)
        self.rq = A * math.sqrt(self.qp / 2)
        self.beta0 = math.asin(_q(phi0) / self.qp)
        self.d = A * (math.cos(phi0) / math.sqrt(1 - E2 * math.sin(phi0) ** 2)) / (self.rq * math.cos(self.beta0))

    def forward(self, lon: float, lat: float) -> Point:
        lam = math.radians(lon) - self.lon0
        beta = math.asin(_q(math.radians(lat)) / self.qp)
        b = self.rq * math.sqrt(2 / (1 + math.sin(self.beta0) * math.sin(beta) + math.cos(self.beta0) * math.cos(beta) * math.cos(lam)))
        x = b * self.d * math.cos(beta) * math.sin(lam)
        y = (b / self.d) * (math.cos(self.beta0) * math.sin(beta) - math.sin(self.beta0) * math.cos(beta) * math.cos(lam))
        return x, y

    def inverse(self, x: float, y: float) -> Point:
        rho = math.hypot(x / self.d, self.d * y)
        if rho == 0:
            return math.degrees(self.lon0), math.degrees(self._phi(self.beta0))
        c = 2 * math.asin(rho / (2 * self.rq))
        beta = math.asin(math.cos(c) * math.sin(self.beta0) + (self.d * y * math.sin(c) * math.cos(self.beta0)) / rho)
        lam = math.atan2(x * math.sin(c), self.d * rho * math.cos(self.beta0) * math.cos(c) - self.d * self.d * y * math.sin(self.beta0) * math.sin(c))
        return math.degrees(self.lon0 + lam), math.degrees(self._phi(beta))

    def _phi(self, beta: float) -> float:
        # Authalic -> geodetic latitude, iterated to convergence (Snyder eq. 3-16).
        q = self.qp * math.sin(beta)
        phi = math.asin(q / 2)
        for _ in range(50):
            s = math.sin(phi)
            nxt = phi + ((1 - E2 * s * s) ** 2 / (2 * math.cos(phi))) * (
                q / (1 - E2) - s / (1 - E2 * s * s) + (1 / (2 * E)) * math.log((1 - E * s) / (1 + E * s)))
            if abs(nxt - phi) < 1e-13:
                return nxt
            phi = nxt
        return phi


def ring_area(ring: Sequence[Point]) -> float:
    n = len(ring)
    if n < 3:
        return 0.0
    s = 0.0
    for i in range(n):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % n]
        s += x1 * y2 - x2 * y1
    return s / 2


def ring_centroid(ring: Sequence[Point]) -> Optional[Point]:
    a = ring_area(ring)
    if abs(a) < 1e-9:
        return None
    cx = cy = 0.0
    n = len(ring)
    for i in range(n):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % n]
        cr = x1 * y2 - x2 * y1
        cx += (x1 + x2) * cr
        cy += (y1 + y2) * cr
    return cx / (6 * a), cy / (6 * a)


def clip(ring: Sequence[Point], xmin: float, ymin: float, xmax: float, ymax: float) -> Ring:
    """Sutherland-Hodgman clip of any simple ring against an axis-aligned rectangle (convex clip window)."""
    out: Ring = list(ring)
    for edge in range(4):
        if not out:
            break
        src, out = out, []
        def inside(p: Point) -> bool:
            return (p[0] >= xmin, p[0] <= xmax, p[1] >= ymin, p[1] <= ymax)[edge]
        def cross(p: Point, q: Point) -> Point:
            (x1, y1), (x2, y2) = p, q
            if edge < 2:
                xe = xmin if edge == 0 else xmax
                t = (xe - x1) / (x2 - x1)
                return xe, y1 + t * (y2 - y1)
            ye = ymin if edge == 2 else ymax
            t = (ye - y1) / (y2 - y1)
            return x1 + t * (x2 - x1), ye
        prev = src[-1]
        for cur in src:
            if inside(cur):
                if not inside(prev):
                    out.append(cross(prev, cur))
                out.append(cur)
            elif inside(prev):
                out.append(cross(prev, cur))
            prev = cur
    return out


def point_in_ring(p: Point, ring: Sequence[Point]) -> bool:
    x, y = p
    inside = False
    n = len(ring)
    j = n - 1
    for i in range(n):
        xi, yi = ring[i]
        xj, yj = ring[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def in_polygons(p: Point, polygons: Sequence[Sequence[Ring]]) -> bool:
    for poly in polygons:
        if point_in_ring(p, poly[0]) and not any(point_in_ring(p, h) for h in poly[1:]):
            return True
    return False


def scanline_point(ring: Sequence[Point]) -> Optional[Point]:
    ys = [p[1] for p in ring]
    y = (min(ys) + max(ys)) / 2
    xs = []
    n = len(ring)
    for i in range(n):
        (x1, y1), (x2, y2) = ring[i], ring[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            xs.append(x1 + (y - y1) * (x2 - x1) / (y2 - y1))
    xs.sort()
    best = None
    for i in range(0, len(xs) - 1, 2):
        if best is None or xs[i + 1] - xs[i] > best[1] - best[0]:
            best = (xs[i], xs[i + 1])
    return None if best is None else ((best[0] + best[1]) / 2, y)


def simplify(ring: Sequence[Point], tolerance: float) -> Ring:
    """Douglas-Peucker on a closed ring (projected metres), iterative; keeps at least 4 vertices."""
    pts = list(ring)
    if len(pts) <= 4:
        return pts
    keep = [False] * len(pts)
    # Split the closed ring at its first vertex and the vertex farthest from it.
    far = max(range(len(pts)), key=lambda k: (pts[k][0] - pts[0][0]) ** 2 + (pts[k][1] - pts[0][1]) ** 2)
    keep[0] = keep[far] = keep[-1] = True
    stack = [(0, far), (far, len(pts) - 1)]
    while stack:
        s, e = stack.pop()
        if e <= s + 1:
            continue
        (x1, y1), (x2, y2) = pts[s], pts[e]
        dx, dy = x2 - x1, y2 - y1
        norm = math.hypot(dx, dy)
        best, idx = -1.0, -1
        for k in range(s + 1, e):
            px, py = pts[k]
            d = math.hypot(px - x1, py - y1) if norm == 0 else abs(dy * px - dx * py + x2 * y1 - y2 * x1) / norm
            if d > best:
                best, idx = d, k
        if best > tolerance:
            keep[idx] = True
            stack.append((s, idx))
            stack.append((idx, e))
    out = [p for p, k in zip(pts, keep) if k]
    return out if len(out) >= 4 else pts


def haversine_km(a: Point, b: Point) -> float:
    lon1, lat1, lon2, lat2 = map(math.radians, (a[0], a[1], b[0], b[1]))
    h = math.sin((lat2 - lat1) / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin((lon2 - lon1) / 2) ** 2
    return 2 * 6371.0088 * math.asin(math.sqrt(h))


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def build(admin1: str, expected_sha: str, cell_km: float, min_fraction: float, lat0: float, lon0: float,
          simplify_m: float = 100.0) -> Tuple[dict, List[dict]]:
    actual = sha256_file(admin1)
    if actual != expected_sha.lower():
        raise SystemExit(f"[BLOCKED: REGIONAL_BOUNDARY_DATA_UNVERIFIED] {admin1} sha256 {actual} != expected {expected_sha}")
    with open(admin1, encoding="utf-8") as f:
        data = json.load(f)
    crs = (data.get("crs") or {}).get("properties", {}).get("name")
    if crs not in ("urn:ogc:def:crs:OGC:1.3:CRS84", None):
        raise SystemExit(f"Unexpected CRS {crs}")
    features = [ft for ft in data["features"] if ft["properties"].get("adm1_pcode") == PCODE]
    if len(features) != 1:
        raise SystemExit(f"Expected exactly one {PCODE} feature, found {len(features)}")
    props = features[0]["properties"]
    geom = features[0]["geometry"]
    raw = geom["coordinates"] if geom["type"] == "MultiPolygon" else [geom["coordinates"]]

    proj = Laea(lat0, lon0)
    polygons: List[List[Ring]] = []
    for poly in raw:
        rings = []
        for ring in poly:
            pts = [proj.forward(lon, lat) for lon, lat in ring]
            if pts and pts[0] == pts[-1]:
                pts = pts[:-1]
            rings.append(pts)
        # Orientation-independent: outer counted positive, holes negative.
        polygons.append(rings)

    def poly_area(rings: List[Ring]) -> float:
        return abs(ring_area(rings[0])) - sum(abs(ring_area(h)) for h in rings[1:])

    total_m2 = sum(poly_area(p) for p in polygons)
    bboxes = []
    for rings in polygons:
        xs = [p[0] for p in rings[0]]
        ys = [p[1] for p in rings[0]]
        bboxes.append((min(xs), min(ys), max(xs), max(ys)))
    gx0 = min(b[0] for b in bboxes)
    gy0 = min(b[1] for b in bboxes)
    gx1 = max(b[2] for b in bboxes)
    gy1 = max(b[3] for b in bboxes)
    size = cell_km * 1000.0
    i0, i1 = math.floor(gx0 / size), math.floor(gx1 / size)
    j0, j1 = math.floor(gy0 / size), math.floor(gy1 / size)

    cells = []
    for j in range(j0, j1 + 1):
        for i in range(i0, i1 + 1):
            cx0, cy0, cx1, cy1 = i * size, j * size, (i + 1) * size, (j + 1) * size
            area = 0.0
            pieces = []
            for rings, bb in zip(polygons, bboxes):
                if bb[2] < cx0 or bb[0] > cx1 or bb[3] < cy0 or bb[1] > cy1:
                    continue
                outer = clip(rings[0], cx0, cy0, cx1, cy1)
                a = abs(ring_area(outer))
                if a <= 0:
                    continue
                holes = 0.0
                for h in rings[1:]:
                    holes += abs(ring_area(clip(h, cx0, cy0, cx1, cy1)))
                area += a - holes
                pieces.append((a - holes, outer))
            if area > 1e-6:
                cells.append({"i": i, "j": j, "area": area, "pieces": pieces})

    full = size * size
    retained = [c for c in cells if c["area"] >= min_fraction * full]
    dropped = [c for c in cells if c["area"] < min_fraction * full]
    if not retained:
        raise SystemExit("No cell reaches the minimum fraction; lower --min-cell-fraction or --cell-km.")

    def representative(c: dict) -> Point:
        # Area centroid of the clipped area when it lies inside the region; otherwise the inside point of a 64 x 64 sub-grid of the
        # cell closest to that centroid (deterministic: ties broken by grid order). Always verified against the full boundary.
        cx0, cy0 = c["i"] * size, c["j"] * size
        sx = sy = sa = 0.0
        for a, piece in c["pieces"]:
            cen = ring_centroid(piece)
            if cen is not None:
                sx += cen[0] * a
                sy += cen[1] * a
                sa += a
        target = (sx / sa, sy / sa) if sa > 0 else (cx0 + size / 2, cy0 + size / 2)
        if in_polygons(target, polygons):
            return target
        n = 64
        grid = [(cx0 + (u + 0.5) * size / n, cy0 + (v + 0.5) * size / n) for v in range(n) for u in range(n)]
        grid.sort(key=lambda q: ((q[0] - target[0]) ** 2 + (q[1] - target[1]) ** 2, q[1], q[0]))
        for cand in grid:
            if any(point_in_ring(cand, piece) for _, piece in c["pieces"]) and in_polygons(cand, polygons):
                return cand
        cand = scanline_point(max(c["pieces"], key=lambda p: p[0])[1])
        if cand is not None and in_polygons(cand, polygons):
            return cand
        raise SystemExit(f"No interior representative point for cell {c['i']},{c['j']}")

    for c in cells:
        x, y = representative(c)
        c["lonlat"] = proj.inverse(x, y)
        c["merged"] = []
        c["weight_area"] = c["area"]
    merges = []
    for c in dropped:
        target = min(retained, key=lambda r: (haversine_km(c["lonlat"], r["lonlat"]), r["j"], r["i"]))
        target["weight_area"] += c["area"]
        target["merged"].append(f"{c['i']}:{c['j']}")
        merges.append({"from": f"{c['i']}:{c['j']}", "to": f"{target['i']}:{target['j']}", "areaKm2": round(c["area"] / 1e6, 6),
                       "distanceKm": round(haversine_km(c["lonlat"], target["lonlat"]), 3)})

    retained.sort(key=lambda c: (c["j"], c["i"]))
    total_weight_area = sum(c["weight_area"] for c in retained)
    weights = [round(c["weight_area"] / total_weight_area, 9) for c in retained]
    residual = round(1 - sum(weights), 9)
    k = max(range(len(weights)), key=lambda n: weights[n])
    weights[k] = round(weights[k] + residual, 9)

    dataset_version = f"hdx-cod-ab-vnm-{props.get('version')}-{props.get('valid_on')}"
    rows = []
    for n, (c, w) in enumerate(zip(retained, weights), start=1):
        lon, lat = c["lonlat"]
        rows.append({
            "locationId": f"HCM79-{n:03d}",
            "latitude": f"{lat:.5f}",
            "longitude": f"{lon:.5f}",
            "areaWeight": f"{w:.9f}",
            "areaKm2": f"{c['weight_area'] / 1e6:.6f}",
            "cell": f"{c['i']}:{c['j']}",
            "sourceDatasetVersion": dataset_version,
        })

    # Coverage: farthest point of any clipped piece vertex from its own cell's sample (an upper bound of representativeness error in km).
    max_dist = 0.0
    for c in retained:
        for _, piece in c["pieces"]:
            for x, y in piece[:: max(1, len(piece) // 50)]:
                max_dist = max(max_dist, haversine_km(proj.inverse(x, y), c["lonlat"]))

    # Simplified boundary for coverage checks (sites without a province code) and map display. Not used for the weights.
    simplified = []
    for rings in polygons:
        outer = simplify(rings[0], simplify_m)
        if abs(ring_area(outer)) < 1e-6:
            continue
        simplified.append([outer] + [simplify(h, simplify_m) for h in rings[1:] if abs(ring_area(simplify(h, simplify_m))) > 0])
    simplified_m2 = sum(abs(ring_area(p[0])) - sum(abs(ring_area(h)) for h in p[1:]) for p in simplified)
    outside = [r["locationId"] for r, c in zip(rows, retained) if not in_polygons(proj.forward(float(r["longitude"]), float(r["latitude"])), simplified)]
    if outside:
        raise SystemExit(f"Samples outside the simplified boundary: {outside}; lower --simplify-m.")
    boundary_lonlat = []
    for p in simplified:
        rings_ll = []
        for ring in p:
            ll = [[round(v, 6) for v in proj.inverse(x, y)] for x, y in ring]
            ll.append(ll[0])
            rings_ll.append(ll)
        boundary_lonlat.append(rings_ll)

    # Display-only reference point: area centroid of the whole region (projected plane), inverse-projected.
    sx = sy = sa = 0.0
    for p in polygons:
        for k, ring in enumerate(p):
            a = ring_area(ring)
            cen = ring_centroid(ring)
            if cen is None:
                continue
            sign = 1 if k == 0 else -1
            sx += sign * cen[0] * abs(a)
            sy += sign * cen[1] * abs(a)
            sa += sign * abs(a)
    ref_lon, ref_lat = proj.inverse(sx / sa, sy / sa)

    lats = [float(r["latitude"]) for r in rows]
    stats = {
        "simplifyToleranceM": simplify_m,
        "simplifiedPolygonCount": len(simplified),
        "simplifiedVertexCount": sum(len(r) for p in simplified for r in p),
        "simplifiedAreaRelativeDifference": round(abs(simplified_m2 - total_m2) / total_m2, 6),
        "referencePointInsideBoundary": in_polygons((sx / sa, sy / sa), polygons),
        "boundaryAreaKm2Computed": round(total_m2 / 1e6, 3),
        "boundaryAreaKm2Declared": props.get("area_sqkm"),
        "areaRelativeDifference": round(abs(total_m2 / 1e6 - float(props.get("area_sqkm"))) / float(props.get("area_sqkm")), 6) if props.get("area_sqkm") else None,
        "polygonCount": len(polygons),
        "polygonsWithHoles": sum(1 for p in polygons if len(p) > 1),
        "vertexCount": sum(len(r) for p in polygons for r in p),
        "candidateCells": len(cells),
        "retainedCells": len(retained),
        "mergedCells": len(dropped),
        "mergedAreaKm2": round(sum(c["area"] for c in dropped) / 1e6, 6),
        "sampledAreaKm2": round(total_weight_area / 1e6, 3),
        "weightSum": round(sum(weights), 9),
        "minWeight": min(weights),
        "maxWeight": max(weights),
        "latitudeRange": [min(lats), max(lats)],
        "longitudeRange": [min(float(r["longitude"]) for r in rows), max(float(r["longitude"]) for r in rows)],
        "maxDistanceFromCellSampleKm": round(max_dist, 3),
        "weightSouthOf9N": round(sum(w for r, w in zip(rows, weights) if float(r["latitude"]) < 9.0), 9),
    }
    manifest = {
        "props": props,
        "sourceSha256": actual,
        "datasetVersion": dataset_version,
        "projection": {"method": "Lambert Azimuthal Equal-Area (ellipsoidal, EPSG method 9820)", "ellipsoid": "WGS84", "lat0": lat0, "lon0": lon0},
        "grid": {"cellKm": cell_km, "anchor": "projected origin (0, 0)", "minCellFraction": min_fraction},
        "merges": merges,
        "stats": stats,
        "boundaryLonLat": boundary_lonlat,
        "referencePoint": {"latitude": round(ref_lat, 5), "longitude": round(ref_lon, 5)},
    }
    return manifest, rows


def write_outputs(out_dir: str, manifest: dict, rows: List[dict], source_url: str, zip_sha: Optional[str]) -> None:
    os.makedirs(out_dir, exist_ok=True)
    buf = io.StringIO(newline="")
    writer = csv.DictWriter(buf, fieldnames=["locationId", "latitude", "longitude", "areaWeight", "areaKm2", "cell", "sourceDatasetVersion"], lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)
    csv_text = buf.getvalue()
    with open(os.path.join(out_dir, "samples.csv"), "w", encoding="utf-8", newline="") as f:
        f.write(csv_text)
    csv_sha = hashlib.sha256(csv_text.encode("utf-8")).hexdigest()
    canonical = "\n".join(f"{r['locationId']}|{r['latitude']}|{r['longitude']}|{r['areaWeight']}" for r in rows)
    props = manifest["props"]
    out = {
        "datasetKey": f"hcm-79-2025-equal-area-{manifest['grid']['cellKm']:g}km-v1",
        "regionCode": "79",
        "regionName": "Thành phố Hồ Chí Minh (sau sắp xếp 2025: TP.HCM, Bình Dương, Bà Rịa – Vũng Tàu cũ)",
        "estimateBasis": "HCMC_2025_REGIONAL_AREA_WEIGHTED",
        "weightMethod": "EQUAL_AREA_GRID_CLIPPED_AREA",
        "toolVersion": TOOL_VERSION,
        "boundary": {
            "publisher": "OCHA HDX COD-AB Viet Nam (source: General Statistics Office of Vietnam, GSO)",
            "datasetPage": "https://data.humdata.org/dataset/cod-ab-vnm",
            "downloadUrl": source_url,
            "downloadSha256": zip_sha,
            "file": "vnm_admin1.geojson",
            "fileSha256": manifest["sourceSha256"],
            "featurePcode": PCODE,
            "featureName": props.get("adm1_name1"),
            "featureVersion": props.get("version"),
            "validOn": props.get("valid_on"),
            "license": "CC BY-IGO (Creative Commons Attribution for Intergovernmental Organisations)",
            "crs": "OGC:CRS84 (WGS84 lon/lat)",
            "legalBasisNote": "Boundary alignments follow Decision 19/2025/QD-TTg (effective 2025-07-01) and GSO API data, per the dataset methodology.",
        },
        "projection": manifest["projection"],
        "grid": manifest["grid"],
        "stats": manifest["stats"],
        "merges": manifest["merges"],
        "sampleCount": len(rows),
        "samplesFile": "samples.csv",
        "samplesSha256": csv_sha,
        "samplesCanonicalSha256": hashlib.sha256(canonical.encode("utf-8")).hexdigest(),
        "sourceDatasetVersion": manifest["datasetVersion"],
        "notes": [
            "SmartSolar's derived regional estimate; neither PVGIS nor NASA POWER publishes an official 'HCMC average'.",
            "Weights are clipped-cell areas on an equal-area projection; slivers below minCellFraction are merged into the nearest retained cell (listed in merges).",
        ],
    }
    boundary = {"type": "FeatureCollection", "features": [{"type": "Feature", "properties": {
        "regionCode": "79", "pcode": PCODE, "source": "OCHA HDX COD-AB Viet Nam (GSO), CC BY-IGO", "validOn": props.get("valid_on"),
        "simplifyToleranceM": manifest["stats"]["simplifyToleranceM"], "note": "Simplified for coverage checks and display; weights use the full-resolution boundary."},
        "geometry": {"type": "MultiPolygon", "coordinates": manifest["boundaryLonLat"]}}]}
    boundary_text = json.dumps(boundary, ensure_ascii=False, separators=(",", ":")) + "\n"
    with open(os.path.join(out_dir, "boundary-simplified.geojson"), "w", encoding="utf-8", newline="") as f:
        f.write(boundary_text)
    out["boundaryFile"] = "boundary-simplified.geojson"
    out["boundaryFileSha256"] = hashlib.sha256(boundary_text.encode("utf-8")).hexdigest()
    out["referencePoint"] = {**manifest["referencePoint"], "displayOnly": True,
                             "method": "Area centroid of the full-resolution boundary on the equal-area plane; display only, never used for any calculation."}
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
        f.write("\n")


def main(argv: Optional[List[str]] = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--admin1", required=True)
    p.add_argument("--expected-sha256", required=True)
    p.add_argument("--cell-km", type=float, default=15.0)
    p.add_argument("--min-cell-fraction", type=float, default=0.10)
    p.add_argument("--lat0", type=float, default=10.75)
    p.add_argument("--lon0", type=float, default=106.75)
    p.add_argument("--simplify-m", type=float, default=100.0)
    p.add_argument("--source-url", default="")
    p.add_argument("--source-zip-sha256", default=None)
    p.add_argument("--out-dir", required=True)
    a = p.parse_args(argv)
    manifest, rows = build(a.admin1, a.expected_sha256, a.cell_km, a.min_cell_fraction, a.lat0, a.lon0, a.simplify_m)
    write_outputs(a.out_dir, manifest, rows, a.source_url, a.source_zip_sha256)
    json.dump({"sampleCount": len(rows), "referencePoint": manifest["referencePoint"], **manifest["stats"]}, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
