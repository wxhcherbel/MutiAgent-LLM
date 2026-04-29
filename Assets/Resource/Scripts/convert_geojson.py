"""
将 OpenStreetMap 导出的 GeoJSON (export.geojson)
转换为 CampusJsonMapLoader.cs 可解析的 JSON 格式 (campus_map.json)。

处理：
1. WGS84 经纬度 → 米制本地坐标
2. 旋转 -14° 使校园建筑对齐坐标轴
3. 裁剪超出校园范围的要素（高速公路等）
4. OSM 标签 → CampusFeatureKind

用法: python convert_geojson.py
"""

import json
import math
import os

# ─── 配置 ───────────────────────────────────────────────
INPUT_FILE = "export.geojson"
OUTPUT_FILE = "campus_map.json"

# 校园建筑主轴偏离正东约 14°，旋转使其对齐坐标轴
ROTATION_DEG = -14.0

# 以校园建筑区中心为参考点（而非全部要素质心，避免高速公路拉偏）
# 这些值由分析 export.geojson 中 building 要素得出
REF_LON = 118.786790
REF_LAT = 31.940134

# 裁剪半径（米）：只保留中心点在此范围内的要素
CLIP_RADIUS_M = 700.0

# 高速公路单独使用更小的裁剪半径（避免向校园外延伸太远）
EXPRESSWAY_CLIP_RADIUS_M = 450.0


# ─── 坐标转换 ──────────────────────────────────────────
def iter_coords(geometry):
    """遍历 geometry 中所有 [lon, lat] 点。"""
    gt = geometry["type"]
    coords = geometry["coordinates"]
    if gt == "Point":
        yield coords
    elif gt == "LineString":
        yield from coords
    elif gt == "Polygon":
        for ring in coords:
            yield from ring
    elif gt == "MultiPolygon":
        for polygon in coords:
            for ring in polygon:
                yield from ring


def make_projector(ref_lon, ref_lat, rotation_deg):
    """返回 (lon, lat) -> [x_m, y_m] 的投影函数（含旋转）。"""
    cos_lat = math.cos(math.radians(ref_lat))
    rad = math.radians(rotation_deg)
    cos_r = math.cos(rad)
    sin_r = math.sin(rad)

    def project(lon, lat):
        # 先投影到米
        mx = (lon - ref_lon) * cos_lat * 111320.0
        my = (lat - ref_lat) * 111320.0
        # 再绕原点旋转
        rx = mx * cos_r - my * sin_r
        ry = mx * sin_r + my * cos_r
        return [round(rx, 4), round(ry, 4)]
    return project


def project_ring(ring, proj):
    """将 [[lon,lat], ...] 转换为 [[x,y], ...]。"""
    return [proj(pt[0], pt[1]) for pt in ring]


# ─── 裁剪判定 ──────────────────────────────────────────
def feature_centroid(geometry):
    """计算要素所有坐标点的质心 (lon, lat)。"""
    s_lon, s_lat, n = 0.0, 0.0, 0
    for lon, lat in iter_coords(geometry):
        s_lon += lon
        s_lat += lat
        n += 1
    if n == 0:
        return None
    return s_lon / n, s_lat / n


def distance_m(lon1, lat1, lon2, lat2):
    """两经纬度点之间的近似距离（米）。"""
    cos_lat = math.cos(math.radians((lat1 + lat2) / 2))
    dx = (lon2 - lon1) * cos_lat * 111320.0
    dy = (lat2 - lat1) * 111320.0
    return math.sqrt(dx * dx + dy * dy)


def is_within_campus(geometry, ref_lon, ref_lat, radius_m):
    """判断要素质心是否在校园范围内。"""
    c = feature_centroid(geometry)
    if c is None:
        return False
    return distance_m(c[0], c[1], ref_lon, ref_lat) <= radius_m


# ─── 线段裁剪 ──────────────────────────────────────────
def clip_linestring_to_radius(coords, ref_lon, ref_lat, radius_m):
    """将 LineString 的坐标裁剪到校园半径内，只保留在范围内的连续段。
    返回在范围内的最长连续段，若全部在外则返回 None。"""
    segments = []
    current = []
    for pt in coords:
        d = distance_m(pt[0], pt[1], ref_lon, ref_lat)
        if d <= radius_m:
            current.append(pt)
        else:
            if len(current) >= 2:
                segments.append(current)
            current = []
    if len(current) >= 2:
        segments.append(current)
    if not segments:
        return None
    # 返回最长段
    return max(segments, key=len)


# ─── OSM 标签 -> kind 映射 ──────────────────────────────
def classify_kind(props):
    """根据 OSM properties 判断 CampusFeatureKind。"""
    if "building" in props:
        return "building"

    leisure = props.get("leisure", "")
    if leisure in ("pitch", "sports_centre", "sports_hall"):
        return "sports"

    natural = props.get("natural", "")
    if natural == "water" or "water" in props:
        return "water"

    highway = props.get("highway", "")
    if highway in ("motorway", "motorway_link"):
        return "expressway"
    if highway in ("footway", "path", "pedestrian", "service",
                    "residential", "tertiary", "secondary", "primary",
                    "trunk", "unclassified"):
        return "road"

    if props.get("bridge") == "yes":
        return "bridge"

    amenity = props.get("amenity", "")
    if amenity == "parking":
        return "parking"

    landuse = props.get("landuse", "")
    if landuse == "grass" or natural == "grassland":
        return "green"
    if landuse == "forest" or natural == "wood":
        return "forest"

    sport = props.get("sport", "")
    if sport:
        return "sports"

    return "other"


# ─── 名称解析 ───────────────────────────────────────────
def resolve_name(props):
    for key in ("name", "name:zh", "name:en"):
        val = props.get(key, "")
        if val and val.strip() and val.strip() != "-":
            return val.strip()
    return None


# ─── 几何转换 ───────────────────────────────────────────
def convert_feature(feat, proj):
    """将一个 GeoJSON Feature 转换为 CampusJsonMapLoader 格式。"""
    props = feat.get("properties", {})
    geom = feat["geometry"]
    gt = geom["type"]
    coords = geom["coordinates"]

    result = {
        "uid": props.get("@id", feat.get("id", "")),
        "name": resolve_name(props),
        "kind": classify_kind(props),
        "tags": {k: str(v) for k, v in props.items()
                 if k not in ("@id", "type", "@relations")},
    }

    if gt == "LineString":
        result["points_xy_m"] = project_ring(coords, proj)

    elif gt == "Polygon":
        outers = [project_ring(coords[0], proj)]
        inners = [project_ring(r, proj) for r in coords[1:]]
        result["rings"] = {"outer": outers, "inner": inners}

    elif gt == "MultiPolygon":
        outers = []
        inners = []
        for polygon in coords:
            outers.append(project_ring(polygon[0], proj))
            for hole in polygon[1:]:
                inners.append(project_ring(hole, proj))
        result["rings"] = {"outer": outers, "inner": inners}

    return result


# ─── 主流程 ─────────────────────────────────────────────
def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    input_path = os.path.join(script_dir, INPUT_FILE)
    output_path = os.path.join(script_dir, OUTPUT_FILE)

    with open(input_path, "r", encoding="utf-8") as f:
        geojson = json.load(f)

    features = geojson["features"]
    print(f"读取 {len(features)} 个要素")
    print(f"参考点: lon={REF_LON:.6f}, lat={REF_LAT:.6f}")
    print(f"旋转: {ROTATION_DEG}°")
    print(f"裁剪半径: {CLIP_RADIUS_M}m")

    proj = make_projector(REF_LON, REF_LAT, ROTATION_DEG)

    campus_features = []
    kind_counts = {}
    skipped = 0

    for feat in features:
        geom = feat["geometry"]
        gt = geom["type"]
        props = feat.get("properties", {})

        # 线要素：裁剪到校园范围（高速公路用更小半径）
        if gt == "LineString":
            kind = classify_kind(props)
            clip_r = EXPRESSWAY_CLIP_RADIUS_M if kind == "expressway" else CLIP_RADIUS_M
            clipped = clip_linestring_to_radius(
                geom["coordinates"], REF_LON, REF_LAT, clip_r)
            if clipped is None:
                skipped += 1
                continue
            # 用裁剪后的坐标替换
            feat = dict(feat)
            feat["geometry"] = dict(geom, coordinates=clipped)

        # 面要素：质心在校园范围外则跳过
        elif not is_within_campus(geom, REF_LON, REF_LAT, CLIP_RADIUS_M):
            skipped += 1
            continue

        cf = convert_feature(feat, proj)
        campus_features.append(cf)
        k = cf["kind"]
        kind_counts[k] = kind_counts.get(k, 0) + 1

    output = {"features": campus_features}

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    print(f"\n输出 {len(campus_features)} 个要素到 {OUTPUT_FILE}（跳过 {skipped} 个）")
    for k, v in sorted(kind_counts.items()):
        print(f"  {k}: {v}")


if __name__ == "__main__":
    main()
