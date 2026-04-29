"""Compute IK target/pole world positions by walking a Unity prefab's bone
hierarchy, and emit YAML blocks for new TwoBoneIKConstraint rigs.

Used to set up Animation Rigging IK for TheOneTrueChef.prefab — arms and
fingers — without having to position handles by hand in the Unity editor.
The script parses the prefab YAML directly, accumulates world transforms
through quaternion + scale composition, and produces values that are
correct on first activation (so MaintainTargetPositionOffset captures
~zero offset).

Usage
-----
1.  Edit `PREFAB`, the bone names you care about, and the position rules
    in `main()`.
2.  Run with numpy installed:  `python compute_ik_positions.py`
3.  Splice the printed positions / generated YAML into the prefab and
    update the relevant `m_Children` lists.

The original invocations of this script:
- Arms: targets at the mean of each hand's finger-root world positions;
  poles at each forearm's world position offset by world -Z = 0.15.
- Fingers: targets at digit3 + digit3.localY * |digit3 - digit2|;
  poles at digit2 + world +Y * |digit3 - digit2|.

Notes
-----
- IKHandles in this prefab sits at world identity, so positions in
  IKHandles' local frame == world coordinates.
- The Armature has scale ~0.01, which is why bone m_LocalPosition values
  look huge (e.g. y=27) — the script handles this via per-axis scale.
- File IDs for new objects in the prefab were allocated in the
  1_100_000_000_000_000_000 range to avoid collisions.
"""
import re
import numpy as np
from numpy.linalg import inv

PREFAB = r"C:\Users\agkle\Unity\MeatballMadness\Assets\Art Assets\Pack_Chefs\Prefabs\TheOneTrueChef.prefab"
TWOBONEIK_GUID = "aeda7bfbf984f2a4da5ab4b8967b115d"


def parse_prefab(path):
    """Return (go_name, transforms) where transforms[id] = dict with
    keys go, pos, rot (xyzw), scale, parent."""
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    docs = re.split(r"^--- !u!", text, flags=re.MULTILINE)

    go_name = {}
    transforms = {}

    for d in docs:
        m = re.match(r"(\d+) &(\d+)\s*\n([A-Za-z]+):\s*\n", d)
        if not m:
            continue
        fid = int(m.group(2))
        kind = m.group(3)
        body = d[m.end():]

        if kind == "GameObject":
            n = re.search(r"^\s*m_Name:\s*(.*)$", body, flags=re.MULTILINE)
            go_name[fid] = n.group(1).strip() if n else ""
        elif kind == "Transform":
            go_m = re.search(r"^\s*m_GameObject:\s*\{fileID:\s*(\d+)\}", body, flags=re.MULTILINE)
            rot_m = re.search(r"^\s*m_LocalRotation:\s*\{x:\s*([-\d.eE+]+),\s*y:\s*([-\d.eE+]+),\s*z:\s*([-\d.eE+]+),\s*w:\s*([-\d.eE+]+)\}", body, flags=re.MULTILINE)
            pos_m = re.search(r"^\s*m_LocalPosition:\s*\{x:\s*([-\d.eE+]+),\s*y:\s*([-\d.eE+]+),\s*z:\s*([-\d.eE+]+)\}", body, flags=re.MULTILINE)
            scl_m = re.search(r"^\s*m_LocalScale:\s*\{x:\s*([-\d.eE+]+),\s*y:\s*([-\d.eE+]+),\s*z:\s*([-\d.eE+]+)\}", body, flags=re.MULTILINE)
            par_m = re.search(r"^\s*m_Father:\s*\{fileID:\s*(\d+)\}", body, flags=re.MULTILINE)
            if not (go_m and rot_m and pos_m and scl_m and par_m):
                continue
            transforms[fid] = {
                "go": int(go_m.group(1)),
                "pos": np.array([float(pos_m.group(1)), float(pos_m.group(2)), float(pos_m.group(3))]),
                "rot": np.array([float(rot_m.group(1)), float(rot_m.group(2)), float(rot_m.group(3)), float(rot_m.group(4))]),
                "scale": np.array([float(scl_m.group(1)), float(scl_m.group(2)), float(scl_m.group(3))]),
                "parent": int(par_m.group(1)),
            }
    return go_name, transforms


def quat_to_matrix(q):
    x, y, z, w = q
    n = x*x + y*y + z*z + w*w
    s = 2.0 / n if n > 0 else 0.0
    xx, yy, zz = x*x*s, y*y*s, z*z*s
    xy, xz, yz = x*y*s, x*z*s, y*z*s
    wx, wy, wz = w*x*s, w*y*s, w*z*s
    return np.array([
        [1.0 - (yy + zz), xy - wz,         xz + wy        ],
        [xy + wz,         1.0 - (xx + zz), yz - wx        ],
        [xz - wy,         yz + wx,         1.0 - (xx + yy)],
    ])


def build_world_lookup(transforms):
    """Return (world(tid), world_rot_axis(tid, axis_local))."""
    cache = {}

    def local_matrix(t):
        M = np.eye(4)
        M[:3, :3] = quat_to_matrix(t["rot"]) @ np.diag(t["scale"])
        M[:3, 3] = t["pos"]
        return M

    def world(tid):
        if tid == 0:
            return np.eye(4)
        if tid in cache:
            return cache[tid]
        t = transforms.get(tid)
        if t is None:
            return np.eye(4)
        cache[tid] = world(t["parent"]) @ local_matrix(t)
        return cache[tid]

    def world_rot_axis(tid, axis_local):
        M = world(tid)
        cols = []
        for i in range(3):
            c = M[:3, i]
            n = np.linalg.norm(c)
            cols.append(c / n if n > 0 else c)
        return np.column_stack(cols) @ np.array(axis_local)

    return world, world_rot_axis


def name_index(go_name, transforms):
    name_to_xform = {}
    for tid, t in transforms.items():
        nm = go_name.get(t["go"], "")
        if nm:
            name_to_xform.setdefault(nm, tid)
    return name_to_xform


def emit_constraint_block(name, fileids, parent_fileid, root, mid, tip, target, hint,
                          local_pos=(0, 0, 0)):
    """Generate YAML for a Constraint GameObject + Transform + TwoBoneIKConstraint."""
    go, t, mb = fileids
    return f"""--- !u!1 &{go}
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
  - component: {{fileID: {t}}}
  - component: {{fileID: {mb}}}
  m_Layer: 0
  m_Name: {name}
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
--- !u!4 &{t}
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  serializedVersion: 2
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: {local_pos[0]}, y: {local_pos[1]}, z: {local_pos[2]}}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_ConstrainProportionsScale: 0
  m_Children: []
  m_Father: {{fileID: {parent_fileid}}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}
--- !u!114 &{mb}
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {TWOBONEIK_GUID}, type: 3}}
  m_Name:
  m_EditorClassIdentifier: Unity.Animation.Rigging::UnityEngine.Animations.Rigging.TwoBoneIKConstraint
  m_Weight: 1
  m_Data:
    m_Root: {{fileID: {root}}}
    m_Mid: {{fileID: {mid}}}
    m_Tip: {{fileID: {tip}}}
    m_Target: {{fileID: {target}}}
    m_Hint: {{fileID: {hint}}}
    m_TargetPositionWeight: 1
    m_TargetRotationWeight: 1
    m_HintWeight: 1
    m_MaintainTargetPositionOffset: 1
    m_MaintainTargetRotationOffset: 1"""


def emit_handle_block(name, fileids, parent_fileid, local_pos):
    """Generate YAML for a Target/Pole GameObject + Transform (no script)."""
    go, t = fileids
    return f"""--- !u!1 &{go}
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
  - component: {{fileID: {t}}}
  m_Layer: 0
  m_Name: {name}
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
--- !u!4 &{t}
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  serializedVersion: 2
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: {local_pos[0]:.7f}, y: {local_pos[1]:.7f}, z: {local_pos[2]:.7f}}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_ConstrainProportionsScale: 0
  m_Children: []
  m_Father: {{fileID: {parent_fileid}}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}"""


def main():
    """Demo: print finger IK positions for TheOneTrueChef.prefab.

    Adjust this body to compute whatever you need; the helpers above are
    the reusable pieces.
    """
    go_name, transforms = parse_prefab(PREFAB)
    world, world_rot_axis = build_world_lookup(transforms)
    name_to_xform = name_index(go_name, transforms)

    # Arm target world positions (set when the arm rigs were created):
    RIGHT_ARM_TARGET_WORLD = np.array([0.9667861, 1.4082687, -0.0436910])
    LEFT_ARM_TARGET_WORLD  = np.array([-0.9726337, 1.4552826, -0.0449461])

    for side, arm_world in [("Right", RIGHT_ARM_TARGET_WORLD),
                             ("Left",  LEFT_ARM_TARGET_WORLD)]:
        for finger in ["Thumb", "Index", "Middle", "Ring", "Pinky"]:
            d2 = name_to_xform[f"{side}Hand{finger}2"]
            d3 = name_to_xform[f"{side}Hand{finger}3"]
            d2w = world(d2)[:3, 3]
            d3w = world(d3)[:3, 3]
            blen = float(np.linalg.norm(d3w - d2w))
            target_world = d3w + world_rot_axis(d3, [0, 1, 0]) * blen
            pole_world   = d2w + np.array([0, 1, 0]) * blen
            tloc = target_world - arm_world
            ploc = pole_world   - arm_world
            print(f"{side}Hand{finger}: target_local={tloc.tolist()}  pole_local={ploc.tolist()}")


if __name__ == "__main__":
    main()
