#!/usr/bin/env python3
"""Convergence / validation of the regional sample manifests against the real providers (one-off, operator-run).

For each manifest directory given, every sample is evaluated with PVGIS 5.3 PVcalc for a common 1 kWp reference system
(and optionally NASA POWER climatology), the area-weighted monthly specific yield is computed exactly as the API does
(Y_HCM[m] = sum_i w_i * E_m[i] / 1 kWp), and the result of every coarser grid is compared with the finest one.

Responses are cached on disk (``--cache-dir``) so a re-run does not call the providers again; failures are never cached.
Concurrency is bounded (``--concurrency``, default 4; PVGIS allows at most 30 requests/s per IP).

Usage:
  python3 -I convergence_check.py --cache-dir <dir> --out report.json m5 m10 m15 m20 [--nasa]
"""

from __future__ import annotations

import argparse
import concurrent.futures as cf
import csv
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

PVGIS = "https://re.jrc.ec.europa.eu/api/v5_3/PVcalc"
NASA = "https://power.larc.nasa.gov/api/temporal/climatology/point"
UA = "SmartSolar-regional-baseline-check/1.0"


def fetch(url: str, cache_dir: str, attempts: int = 4) -> dict:
    key = hashlib.sha256(url.encode()).hexdigest()
    path = os.path.join(cache_dir, key + ".json")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    delay = 1.0
    last = None
    for _ in range(attempts):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=60) as r:
                body = json.loads(r.read().decode("utf-8"))
            with open(path + ".tmp", "w", encoding="utf-8") as f:
                json.dump(body, f)
            os.replace(path + ".tmp", path)
            return body
        except (OSError, json.JSONDecodeError) as exc:  # URLError/HTTPError/reset/timeout: retry with backoff, never cache failure
            last = exc
            time.sleep(delay)
            delay *= 2
    raise RuntimeError(f"{url}: {last}")


def samples(directory: str):
    with open(os.path.join(directory, "samples.csv"), encoding="utf-8") as f:
        return list(csv.DictReader(f))


def pvgis_url(lat: str, lon: str, tilt: float, aspect: float, loss: float, place: str) -> str:
    return (f"{PVGIS}?lat={float(lat):.4f}&lon={float(lon):.4f}&peakpower=1&loss={loss:g}&angle={tilt:g}&aspect={aspect:g}"
            f"&mountingplace={place}&pvtechchoice=crystSi&usehorizon=1&outputformat=json")


def nasa_url(lat: str, lon: str) -> str:
    return (f"{NASA}?parameters=ALLSKY_SFC_SW_DWN,T2M,PRECTOTCORR&community=RE&longitude={float(lon):.2f}&latitude={float(lat):.2f}"
            f"&start=2001&end=2020&format=JSON")


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("dirs", nargs="+")
    p.add_argument("--cache-dir", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--concurrency", type=int, default=4)
    p.add_argument("--tilt", type=float, default=10)
    p.add_argument("--aspect", type=float, default=0)
    p.add_argument("--loss", type=float, default=14)
    p.add_argument("--place", default="free")
    p.add_argument("--nasa", action="store_true")
    a = p.parse_args()
    os.makedirs(a.cache_dir, exist_ok=True)

    report = {"reference": {"peakpowerKwp": 1, "tilt": a.tilt, "aspect": a.aspect, "loss": a.loss, "mountingplace": a.place,
                            "pvtechchoice": "crystSi", "usehorizon": 1}, "grids": {}}
    for d in a.dirs:
        rows = samples(d)
        urls = [pvgis_url(r["latitude"], r["longitude"], a.tilt, a.aspect, a.loss, a.place) for r in rows]
        with cf.ThreadPoolExecutor(max_workers=a.concurrency) as pool:
            bodies = list(pool.map(lambda u: fetch(u, a.cache_dir), urls))
        monthly = [0.0] * 12
        per_point_annual = []
        dbs = set()
        years = set()
        for r, b in zip(rows, bodies):
            w = float(r["areaWeight"])
            ms = sorted(b["outputs"]["monthly"]["fixed"], key=lambda m: m["month"])
            for m in ms:
                monthly[m["month"] - 1] += w * m["E_m"]
            per_point_annual.append(sum(m["E_m"] for m in ms))
            meteo = b["inputs"]["meteo_data"]
            dbs.add(meteo.get("radiation_db"))
            years.add((meteo.get("year_min"), meteo.get("year_max")))
        entry = {
            "sampleCount": len(rows),
            "monthlySpecificYieldKwhPerKwp": [round(v, 4) for v in monthly],
            "annualSpecificYieldKwhPerKwp": round(sum(monthly), 3),
            "pointAnnualMin": round(min(per_point_annual), 2),
            "pointAnnualMax": round(max(per_point_annual), 2),
            "unweightedMeanAnnual": round(sum(per_point_annual) / len(per_point_annual), 3),
            "radiationDatabases": sorted(x for x in dbs if x),
            "yearRanges": sorted([list(y) for y in years]),
        }
        if a.nasa:
            nurls = sorted({nasa_url(r["latitude"], r["longitude"]) for r in rows})
            with cf.ThreadPoolExecutor(max_workers=min(a.concurrency, 3)) as pool:
                nb = dict(zip(nurls, pool.map(lambda u: fetch(u, a.cache_dir), nurls)))
            keys = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]
            ghi = [0.0] * 12
            distinct = set()
            for r in rows:
                body = nb[nasa_url(r["latitude"], r["longitude"])]
                par = body["properties"]["parameter"]["ALLSKY_SFC_SW_DWN"]
                distinct.add(tuple(par[k] for k in keys))
                for i, k in enumerate(keys):
                    ghi[i] += float(r["areaWeight"]) * par[k]
            entry["nasaGhiKwhPerM2PerDay"] = [round(v, 4) for v in ghi]
            entry["nasaDistinctGridSeries"] = len(distinct)
            entry["nasaRequests"] = len(nurls)
        report["grids"][os.path.basename(os.path.normpath(d))] = entry
        print(d, entry["sampleCount"], entry["annualSpecificYieldKwhPerKwp"], file=sys.stderr)

    names = list(report["grids"].keys())
    ref = report["grids"][names[0]]
    for n in names[1:]:
        g = report["grids"][n]
        g["annualRelativeDifferenceVsFinest"] = round(abs(g["annualSpecificYieldKwhPerKwp"] - ref["annualSpecificYieldKwhPerKwp"]) / ref["annualSpecificYieldKwhPerKwp"], 6)
        g["maxMonthlyRelativeDifferenceVsFinest"] = round(max(abs(x - y) / y for x, y in zip(g["monthlySpecificYieldKwhPerKwp"], ref["monthlySpecificYieldKwhPerKwp"])), 6)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    print(json.dumps({n: {k: report["grids"][n].get(k) for k in ("sampleCount", "annualSpecificYieldKwhPerKwp", "annualRelativeDifferenceVsFinest",
                                                               "maxMonthlyRelativeDifferenceVsFinest", "pointAnnualMin", "pointAnnualMax",
                                                               "radiationDatabases", "yearRanges", "nasaDistinctGridSeries")} for n in names}, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
