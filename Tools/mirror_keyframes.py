#!/usr/bin/env python3
"""
Mirror animation keyframes across a center plane in a Unity .anim file.

Given a list of source properties (e.g. "RightArmTarget:Position") and a matching
list of destination properties (e.g. "LeftArmTarget:Position"), copies and mirrors
the source keyframes about a world-space plane and writes them to the destination.
Used to mirror right-side IK target animation onto the left side without manual
copy/paste/negate.

The mirror plane is defined by a world-space point (--centerAxisLocation) and an
axis-aligned normal (--centerAxisVector). Non-axis-aligned planes are rejected.

Hierarchical evaluation: positions and rotations are converted to world space
along the path's parent chain (sampling animated parents at the same time t),
mirrored, then converted back into the destination's local space.

Position curves: tangents are copied from the source with component-wise negation
on the mirror axis. Rotation curves: tangents are recomputed as auto (smooth)
because Euler tangents do not survive quaternion mirroring cleanly.

Usage:
    python mirror_keyframes.py --anim path/to/clip.anim \
        --centerAxisLocation 0 0 0 \
        --centerAxisVector 1 0 0 \
        --copySrc RightArmTarget:Position RightHandIndexTarget:Rotation \
        --copyDst LeftArmTarget:Position  LeftHandIndexTarget:Rotation
"""

import argparse
import io
import math
import sys
import zlib
from pathlib import Path

try:
    from ruamel.yaml import YAML
    from ruamel.yaml.comments import CommentedMap, CommentedSeq
except ImportError:
    sys.exit("ruamel.yaml is required: pip install ruamel.yaml")


# Snap near-zero values (computational noise from quat math) and round to the same
# precision Unity itself writes (~8 significant digits). Keeps the output diff small
# and visually clean.
NOISE_THRESHOLD = 1e-7

def fnum(v):
    f = float(v)
    if abs(f) < NOISE_THRESHOLD:
        return 0.0
    return round(f, 8)


# ---------------------------------------------------------------------------
# Vector / quaternion math (Unity conventions)
# ---------------------------------------------------------------------------

def quat_mul(a, b):
    aw, ax, ay, az = a
    bw, bx, by, bz = b
    return (
        aw * bw - ax * bx - ay * by - az * bz,
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
    )

def quat_conj(q):
    return (q[0], -q[1], -q[2], -q[3])

def quat_rotate(q, v):
    qw, qx, qy, qz = q
    vx, vy, vz = v
    cx = qy * vz - qz * vy
    cy = qz * vx - qx * vz
    cz = qx * vy - qy * vx
    tx = cx + qw * vx
    ty = cy + qw * vy
    tz = cz + qw * vz
    return (
        vx + 2.0 * (qy * tz - qz * ty),
        vy + 2.0 * (qz * tx - qx * tz),
        vz + 2.0 * (qx * ty - qy * tx),
    )

def euler_to_quat(x_deg, y_deg, z_deg):
    # Unity Quaternion.Euler(x,y,z) == Ry(y) * Rx(x) * Rz(z)  (intrinsic ZXY)
    rx, ry, rz = math.radians(x_deg), math.radians(y_deg), math.radians(z_deg)
    cx, sx = math.cos(rx / 2), math.sin(rx / 2)
    cy, sy = math.cos(ry / 2), math.sin(ry / 2)
    cz, sz = math.cos(rz / 2), math.sin(rz / 2)
    qx = (cx, sx, 0.0, 0.0)
    qy = (cy, 0.0, sy, 0.0)
    qz = (cz, 0.0, 0.0, sz)
    return quat_mul(quat_mul(qy, qx), qz)

def quat_to_euler(q):
    # Inverse of Unity's Ry*Rx*Rz; returns (x,y,z) in degrees
    qw, qx, qy, qz = q
    sin_x = max(-1.0, min(1.0, 2.0 * (qw * qx - qy * qz)))
    if abs(sin_x) > 0.99999:
        x = math.copysign(math.pi / 2, sin_x)
        y = math.atan2(-2.0 * (qx * qz - qw * qy), 1.0 - 2.0 * (qx * qx + qy * qy))
        z = 0.0
    else:
        x = math.asin(sin_x)
        y = math.atan2(2.0 * (qw * qy + qx * qz), 1.0 - 2.0 * (qx * qx + qy * qy))
        z = math.atan2(2.0 * (qw * qz + qx * qy), 1.0 - 2.0 * (qx * qx + qz * qz))
    return (math.degrees(x), math.degrees(y), math.degrees(z))


# ---------------------------------------------------------------------------
# Mirror operations
# ---------------------------------------------------------------------------

AXIS_INDEX = {'x': 0, 'y': 1, 'z': 2}

def mirror_pos(p, center, axis):
    i = AXIS_INDEX[axis]
    out = list(p)
    out[i] = 2.0 * center[i] - p[i]
    return tuple(out)

def mirror_quat(q, axis):
    w, x, y, z = q
    if axis == 'x': return (w,  x, -y, -z)
    if axis == 'y': return (w, -x,  y, -z)
    if axis == 'z': return (w, -x, -y,  z)
    raise ValueError(axis)


# ---------------------------------------------------------------------------
# Hermite interpolation (unweighted; this clip uses weightedMode = 0)
# ---------------------------------------------------------------------------

def hermite(t0, v0, out0, t1, v1, in1, t):
    dt = t1 - t0
    if dt <= 0:
        return v0
    s = (t - t0) / dt
    s2 = s * s
    s3 = s2 * s
    h00 = 2 * s3 - 3 * s2 + 1
    h10 = s3 - 2 * s2 + s
    h01 = -2 * s3 + 3 * s2
    h11 = s3 - s2
    return h00 * v0 + h10 * dt * out0 + h01 * v1 + h11 * dt * in1

def sample_vec3_curve(keys, t):
    if not keys:
        return None
    if t <= float(keys[0]['time']):
        v = keys[0]['value']
        return (float(v['x']), float(v['y']), float(v['z']))
    if t >= float(keys[-1]['time']):
        v = keys[-1]['value']
        return (float(v['x']), float(v['y']), float(v['z']))
    for i in range(len(keys) - 1):
        k0, k1 = keys[i], keys[i + 1]
        t0, t1 = float(k0['time']), float(k1['time'])
        if t0 <= t <= t1:
            v0, v1 = k0['value'], k1['value']
            o0, i1 = k0['outSlope'], k1['inSlope']
            return (
                hermite(t0, float(v0['x']), float(o0['x']), t1, float(v1['x']), float(i1['x']), t),
                hermite(t0, float(v0['y']), float(o0['y']), t1, float(v1['y']), float(i1['y']), t),
                hermite(t0, float(v0['z']), float(o0['z']), t1, float(v1['z']), float(i1['z']), t),
            )
    v = keys[-1]['value']
    return (float(v['x']), float(v['y']), float(v['z']))


# ---------------------------------------------------------------------------
# YAML helpers
# ---------------------------------------------------------------------------

def flow_xyz(x, y, z):
    m = CommentedMap()
    m['x'] = fnum(x)
    m['y'] = fnum(y)
    m['z'] = fnum(z)
    m.fa.set_flow_style()
    return m

def make_vec3_keyframe(time, value, in_slope, out_slope, in_weight=None, out_weight=None):
    """Build one keyframe in m_PositionCurves / m_EulerCurves format."""
    if in_weight is None:
        in_weight = (0.33333334, 0.33333334, 0.33333334)
    if out_weight is None:
        out_weight = (0.33333334, 0.33333334, 0.33333334)
    kf = CommentedMap()
    kf['serializedVersion'] = 3
    kf['time'] = fnum(time)
    kf['value'] = flow_xyz(*value)
    kf['inSlope'] = flow_xyz(*in_slope)
    kf['outSlope'] = flow_xyz(*out_slope)
    kf['tangentMode'] = 0
    kf['weightedMode'] = 0
    kf['inWeight'] = flow_xyz(*in_weight)
    kf['outWeight'] = flow_xyz(*out_weight)
    return kf

def make_scalar_keyframe(time, value, in_slope, out_slope, tangent_mode=136,
                         in_weight=0.33333334, out_weight=0.33333334):
    """Build one keyframe in m_EditorCurves (scalar component) format."""
    kf = CommentedMap()
    kf['serializedVersion'] = 3
    kf['time'] = fnum(time)
    kf['value'] = fnum(value)
    kf['inSlope'] = fnum(in_slope)
    kf['outSlope'] = fnum(out_slope)
    kf['tangentMode'] = tangent_mode
    kf['weightedMode'] = 0
    kf['inWeight'] = fnum(in_weight)
    kf['outWeight'] = fnum(out_weight)
    return kf

def make_curve_block(path, m_curve_keys):
    """Build a block matching the m_PositionCurves / m_EulerCurves entry shape."""
    block = CommentedMap()
    inner = CommentedMap()
    inner['serializedVersion'] = 2
    seq = CommentedSeq(m_curve_keys)
    inner['m_Curve'] = seq
    inner['m_PreInfinity'] = 2
    inner['m_PostInfinity'] = 2
    inner['m_RotationOrder'] = 4
    block['curve'] = inner
    block['path'] = path
    return block

def make_editor_curve_block(path, attribute, scalar_keys):
    """Build an entry for m_EditorCurves (one per axis component)."""
    block = CommentedMap()
    block['serializedVersion'] = 2
    inner = CommentedMap()
    inner['serializedVersion'] = 2
    seq = CommentedSeq(scalar_keys)
    inner['m_Curve'] = seq
    inner['m_PreInfinity'] = 2
    inner['m_PostInfinity'] = 2
    inner['m_RotationOrder'] = 4
    block['curve'] = inner
    block['attribute'] = attribute
    block['path'] = path
    block['classID'] = 4
    script_ref = CommentedMap()
    script_ref['fileID'] = 0
    script_ref.fa.set_flow_style()
    block['script'] = script_ref
    block['flags'] = 16
    return block

def make_binding(path, attribute_id):
    b = CommentedMap()
    b['serializedVersion'] = 2
    b['path'] = zlib.crc32(path.encode('utf-8')) & 0xFFFFFFFF
    b['attribute'] = attribute_id
    script_ref = CommentedMap()
    script_ref['fileID'] = 0
    script_ref.fa.set_flow_style()
    b['script'] = script_ref
    b['typeID'] = 4
    b['customType'] = 4 if attribute_id == 4 else 0
    b['isPPtrCurve'] = 0
    b['isIntCurve'] = 0
    b['isSerializeReferenceCurve'] = 0
    return b


# ---------------------------------------------------------------------------
# Anim document model
# ---------------------------------------------------------------------------

# Property type → (vector-curve list name, scalar attribute names, binding attribute id)
PROP_KINDS = {
    'Position': {
        'vec_list': 'm_PositionCurves',
        'attr_names': ('m_LocalPosition.x', 'm_LocalPosition.y', 'm_LocalPosition.z'),
        'binding_attr': 1,
    },
    'Rotation': {
        'vec_list': 'm_EulerCurves',
        'attr_names': ('localEulerAnglesRaw.x', 'localEulerAnglesRaw.y', 'localEulerAnglesRaw.z'),
        'binding_attr': 4,
    },
}


class AnimDoc:
    def __init__(self, clip):
        self.clip = clip
        # Ensure lists exist (some may be empty in the YAML)
        for k in ('m_PositionCurves', 'm_EulerCurves', 'm_EditorCurves'):
            if self.clip.get(k) is None or self.clip.get(k) == []:
                # keep empty as-is, but we'll need to replace if we add
                pass

    def _curves(self, list_name):
        v = self.clip.get(list_name)
        if v is None or v == []:
            return []
        return v

    def find_vec_curve(self, list_name, path):
        for c in self._curves(list_name):
            if str(c.get('path', '')) == path:
                return c
        return None

    def all_paths(self):
        paths = set()
        for k in ('m_PositionCurves', 'm_EulerCurves'):
            for c in self._curves(k):
                paths.add(str(c.get('path', '')))
        return paths

    def resolve_leaf(self, leaf):
        """Return all paths whose final segment equals `leaf`."""
        return sorted(p for p in self.all_paths() if p.split('/')[-1] == leaf)

    # ---- world-space evaluation ----

    def sample_local_pos(self, path, t):
        c = self.find_vec_curve('m_PositionCurves', path)
        if c is None:
            return (0.0, 0.0, 0.0)
        return sample_vec3_curve(c['curve']['m_Curve'], t)

    def sample_local_rot_quat(self, path, t):
        c = self.find_vec_curve('m_EulerCurves', path)
        if c is None:
            return (1.0, 0.0, 0.0, 0.0)
        eul = sample_vec3_curve(c['curve']['m_Curve'], t)
        return euler_to_quat(*eul)

    def world_transform_at(self, path, t):
        wp = (0.0, 0.0, 0.0)
        wr = (1.0, 0.0, 0.0, 0.0)
        segs = path.split('/')
        for i in range(len(segs)):
            prefix = '/'.join(segs[:i + 1])
            lp = self.sample_local_pos(prefix, t)
            lr = self.sample_local_rot_quat(prefix, t)
            wp = tuple(a + b for a, b in zip(wp, quat_rotate(wr, lp)))
            wr = quat_mul(wr, lr)
        return wp, wr

    def parent_world_transform_at(self, child_path, t):
        segs = child_path.split('/')
        if len(segs) == 1:
            return (0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0)
        parent_path = '/'.join(segs[:-1])
        return self.world_transform_at(parent_path, t)

    # ---- mutation ----

    def replace_or_insert_vec_curve(self, list_name, path, new_block):
        lst = self.clip.get(list_name)
        if lst is None or lst == []:
            seq = CommentedSeq()
            seq.append(new_block)
            self.clip[list_name] = seq
            return True
        for i, c in enumerate(lst):
            if str(c.get('path', '')) == path:
                lst[i] = new_block
                return False
        lst.append(new_block)
        return True

    def replace_or_insert_editor_curves(self, path, attr_names, scalar_curves_per_axis):
        """Replace editor curves for the three component attributes, or insert if missing."""
        lst = self.clip.get('m_EditorCurves')
        if lst is None or lst == []:
            self.clip['m_EditorCurves'] = CommentedSeq()
            lst = self.clip['m_EditorCurves']

        existing_idx = {a: None for a in attr_names}
        for i, c in enumerate(lst):
            if str(c.get('path', '')) == path and str(c.get('attribute', '')) in attr_names:
                existing_idx[str(c['attribute'])] = i

        for attr, keys in zip(attr_names, scalar_curves_per_axis):
            block = make_editor_curve_block(path, attr, keys)
            if existing_idx[attr] is not None:
                lst[existing_idx[attr]] = block
            else:
                lst.append(block)

    def ensure_binding(self, path, attribute_id):
        bindings = self.clip['m_ClipBindingConstant']['genericBindings']
        target_hash = zlib.crc32(path.encode('utf-8')) & 0xFFFFFFFF
        for b in bindings:
            if int(b.get('path', -1)) == target_hash and int(b.get('attribute', -1)) == attribute_id:
                return False
        bindings.append(make_binding(path, attribute_id))
        return True


# ---------------------------------------------------------------------------
# Mirror logic — the actual work
# ---------------------------------------------------------------------------

def mirror_position_curve(doc, src_path, dst_path, center, axis):
    src_curve = doc.find_vec_curve('m_PositionCurves', src_path)
    if src_curve is None:
        raise SystemExit(f"Source position curve not found: '{src_path}'")
    src_keys = src_curve['curve']['m_Curve']
    new_keys = []
    for k in src_keys:
        t = float(k['time'])
        # Source local → world
        src_world_pos, _ = doc.world_transform_at(src_path, t)
        # Mirror in world space
        mirrored_world = mirror_pos(src_world_pos, center, axis)
        # World → destination local
        parent_wp, parent_wr = doc.parent_world_transform_at(dst_path, t)
        delta = tuple(a - b for a, b in zip(mirrored_world, parent_wp))
        local_pos = quat_rotate(quat_conj(parent_wr), delta)

        # Tangents: copy from src and negate component on mirror axis
        v = lambda d: (float(d['x']), float(d['y']), float(d['z']))
        in_slope = list(v(k['inSlope']))
        out_slope = list(v(k['outSlope']))
        in_w = v(k['inWeight'])
        out_w = v(k['outWeight'])
        idx = AXIS_INDEX[axis]
        in_slope[idx] = -in_slope[idx]
        out_slope[idx] = -out_slope[idx]

        new_keys.append(make_vec3_keyframe(
            t, local_pos, tuple(in_slope), tuple(out_slope), in_w, out_w
        ))
    return new_keys


def euler_unwrap(prev_eul, new_eul):
    """Add ±360k per axis to keep new_eul within 180° of prev_eul."""
    out = list(new_eul)
    for i in range(3):
        while out[i] - prev_eul[i] > 180.0:
            out[i] -= 360.0
        while out[i] - prev_eul[i] < -180.0:
            out[i] += 360.0
    return tuple(out)

def auto_slopes_per_axis(times, values):
    """Catmull-Rom-style smooth slope at each keyframe, per axis. Returns list of (in, out) tuples of vec3."""
    n = len(values)
    slopes = []
    for i in range(n):
        s = [0.0, 0.0, 0.0]
        if 0 < i < n - 1:
            dt = times[i + 1] - times[i - 1]
            if dt > 0:
                for a in range(3):
                    s[a] = (values[i + 1][a] - values[i - 1][a]) / dt
        elif i == 0 and n >= 2:
            dt = times[1] - times[0]
            if dt > 0:
                for a in range(3):
                    s[a] = (values[1][a] - values[0][a]) / dt
        elif i == n - 1 and n >= 2:
            dt = times[-1] - times[-2]
            if dt > 0:
                for a in range(3):
                    s[a] = (values[-1][a] - values[-2][a]) / dt
        slopes.append(tuple(s))
    return slopes


def mirror_rotation_curve(doc, src_path, dst_path, axis):
    src_curve = doc.find_vec_curve('m_EulerCurves', src_path)
    if src_curve is None:
        raise SystemExit(f"Source rotation curve not found: '{src_path}'")
    src_keys = src_curve['curve']['m_Curve']

    times = []
    eulers = []
    prev = None
    for k in src_keys:
        t = float(k['time'])
        # Source world rotation
        _, src_world_rot = doc.world_transform_at(src_path, t)
        mirrored = mirror_quat(src_world_rot, axis)
        # World → destination local
        _, parent_wr = doc.parent_world_transform_at(dst_path, t)
        local_rot = quat_mul(quat_conj(parent_wr), mirrored)
        eul = quat_to_euler(local_rot)
        if prev is not None:
            eul = euler_unwrap(prev, eul)
        prev = eul
        times.append(t)
        eulers.append(eul)

    slopes = auto_slopes_per_axis(times, eulers)
    new_keys = []
    for t, val, slope in zip(times, eulers, slopes):
        new_keys.append(make_vec3_keyframe(t, val, slope, slope))
    return new_keys


def split_to_scalar_curves(vec_keys):
    """Given a list of vec3 keyframes, return three scalar key lists matching axes x/y/z for m_EditorCurves."""
    out = ([], [], [])
    for k in vec_keys:
        t = float(k['time'])
        for ai, a in enumerate('xyz'):
            sk = make_scalar_keyframe(
                t,
                float(k['value'][a]),
                float(k['inSlope'][a]),
                float(k['outSlope'][a]),
                tangent_mode=136,
                in_weight=float(k['inWeight'][a]),
                out_weight=float(k['outWeight'][a]),
            )
            out[ai].append(sk)
    return out


# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------

def parse_axis_vector(vec):
    """Validate axis-aligned unit vector. Return one of {'x','y','z'}."""
    nz = [(i, v) for i, v in enumerate(vec) if abs(v) > 1e-6]
    if len(nz) != 1:
        raise SystemExit(
            f"--centerAxisVector must be axis-aligned (one nonzero component). "
            f"Got {vec}"
        )
    return 'xyz'[nz[0][0]]


def parse_property(spec):
    if ':' not in spec:
        raise SystemExit(f"Property '{spec}' must be of the form 'LeafName:Position' or ':Rotation'")
    leaf, kind = spec.split(':', 1)
    if kind not in PROP_KINDS:
        raise SystemExit(f"Property kind '{kind}' must be 'Position' or 'Rotation'")
    return leaf, kind


def resolve_src_path(doc, leaf, kind):
    list_name = PROP_KINDS[kind]['vec_list']
    matches = [p for p in doc.resolve_leaf(leaf) if doc.find_vec_curve(list_name, p) is not None]
    if not matches:
        raise SystemExit(
            f"No {kind} curve found in animation for source leaf '{leaf}'. "
            f"Searched paths ending in '/{leaf}' across {list_name}."
        )
    if len(matches) > 1:
        raise SystemExit(
            f"Source leaf '{leaf}' is ambiguous for {kind}: matched {len(matches)} paths "
            f"({', '.join(matches)}). Property names must resolve to a single path."
        )
    return matches[0]


def resolve_dst_path(doc, leaf, kind, src_path):
    """
    Resolve destination by leaf. If a curve already exists on a path with this leaf,
    use that. Otherwise, derive a sibling path by replacing the leaf in src_path.
    """
    list_name = PROP_KINDS[kind]['vec_list']
    candidates = doc.resolve_leaf(leaf)
    existing = [p for p in candidates if doc.find_vec_curve(list_name, p) is not None]
    if len(existing) > 1:
        raise SystemExit(
            f"Destination leaf '{leaf}' is ambiguous for {kind}: matched {len(existing)} paths "
            f"({', '.join(existing)})."
        )
    if existing:
        return existing[0], False
    if candidates:
        if len(candidates) > 1:
            raise SystemExit(
                f"Destination leaf '{leaf}' has multiple paths ({', '.join(candidates)}); "
                f"can't decide which to write. Disambiguate by adding a single keyframe in Unity."
            )
        return candidates[0], True
    # Synthesize a destination path by swapping the leaf in src_path.
    src_segs = src_path.split('/')
    src_segs[-1] = leaf
    return '/'.join(src_segs), True


def load_anim(path):
    text = path.read_text(encoding='utf-8')
    # Split off Unity directive header so ruamel doesn't choke on `!u!74 &7400000`.
    # Header is the first three lines: %YAML, %TAG, --- !u!N &M
    lines = text.split('\n')
    if not (lines[0].startswith('%YAML') and lines[1].startswith('%TAG') and lines[2].startswith('---')):
        raise SystemExit("Unexpected file header; first three lines must be %YAML, %TAG, ---.")
    header = '\n'.join(lines[:3]) + '\n'
    body = '\n'.join(lines[3:])

    yaml = YAML()
    yaml.preserve_quotes = True
    yaml.width = 4096
    yaml.indent(mapping=2, sequence=2, offset=0)
    data = yaml.load(body)
    return header, body, data, yaml


def save_anim(path, header, data, yaml):
    buf = io.StringIO()
    yaml.dump(data, buf)
    # Force LF line endings to match Unity's serialization (avoid spurious CRLF on Windows).
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(header + buf.getvalue())


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--anim', required=True, help='Path to .anim file')
    p.add_argument('--centerAxisLocation', nargs=3, type=float, required=True,
                   metavar=('X', 'Y', 'Z'), help='World-space point on the mirror plane')
    p.add_argument('--centerAxisVector', nargs=3, type=float, required=True,
                   metavar=('X', 'Y', 'Z'), help='Mirror plane normal (must be axis-aligned)')
    p.add_argument('--copySrc', nargs='+', required=True, metavar='Leaf:Kind',
                   help='Source properties (e.g. RightArmTarget:Position)')
    p.add_argument('--copyDst', nargs='+', required=True, metavar='Leaf:Kind',
                   help='Destination properties (e.g. LeftArmTarget:Position)')
    p.add_argument('--dryRun', action='store_true', help='Do not write the file; print what would change')
    args = p.parse_args()

    if len(args.copySrc) != len(args.copyDst):
        raise SystemExit(f"--copySrc has {len(args.copySrc)} entries but --copyDst has {len(args.copyDst)}")

    axis = parse_axis_vector(args.centerAxisVector)
    center = tuple(args.centerAxisLocation)

    anim_path = Path(args.anim)
    if not anim_path.exists():
        raise SystemExit(f"File not found: {anim_path}")

    header, _body, data, yaml = load_anim(anim_path)
    clip = next(iter(data.values())) if isinstance(data, dict) and len(data) == 1 else data
    # The top-level mapping has one key 'AnimationClip' once parsed
    if 'AnimationClip' in data:
        clip = data['AnimationClip']
    doc = AnimDoc(clip)

    print(f"Mirror plane: axis={axis}, center={center}")

    for src_spec, dst_spec in zip(args.copySrc, args.copyDst):
        src_leaf, src_kind = parse_property(src_spec)
        dst_leaf, dst_kind = parse_property(dst_spec)
        if src_kind != dst_kind:
            raise SystemExit(
                f"Mismatched kinds: src '{src_spec}' is {src_kind} but dst '{dst_spec}' is {dst_kind}."
            )

        src_path = resolve_src_path(doc, src_leaf, src_kind)
        dst_path, dst_created = resolve_dst_path(doc, dst_leaf, dst_kind, src_path)

        kind_info = PROP_KINDS[src_kind]
        list_name = kind_info['vec_list']

        if src_kind == 'Position':
            new_keys = mirror_position_curve(doc, src_path, dst_path, center, axis)
        else:
            new_keys = mirror_rotation_curve(doc, src_path, dst_path, axis)

        action = "create" if dst_created else "overwrite"
        print(f"  {src_kind}: '{src_path}' -> '{dst_path}' ({action}, {len(new_keys)} keys)")

        if args.dryRun:
            continue

        # Write back: vector curve + editor scalar curves + binding
        new_block = make_curve_block(dst_path, new_keys)
        doc.replace_or_insert_vec_curve(list_name, dst_path, new_block)

        scalar_curves = split_to_scalar_curves(new_keys)
        doc.replace_or_insert_editor_curves(dst_path, kind_info['attr_names'], scalar_curves)

        if dst_created:
            doc.ensure_binding(dst_path, kind_info['binding_attr'])

    if args.dryRun:
        print("(dry run -- file not written)")
        return

    save_anim(anim_path, header, data, yaml)
    print(f"Wrote {anim_path}")


if __name__ == '__main__':
    main()
