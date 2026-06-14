# Porting Meatball Madness to Godot — Research & Feasibility

## Purpose

This document evaluates what it would take to re-implement **Meatball Madness** (currently
Unity 6 / URP / Netcode for GameObjects) in **Godot 4** (latest stable line, 4.5/4.6). It walks
the six concern areas you flagged, in your stated order of importance, grounds each against the
*actual* code in this repo, and gives a recommendation per area plus an honest overall verdict.

**TL;DR verdict:** A port is *feasible* but is a **near-total rewrite of all gameplay C#**, not a
mechanical translation. The two areas that carry almost all the risk are **(1) the networking
stack** (Godot has no managed Lobby/Relay/QuickJoin service — you must replace it with Steam,
Nakama, or your own relay) and **(2) physics** (Godot has *no public API equivalent to
`Physics.Simulate` / `Physics.simulationMode = Script`*, which our client-side prediction and
rollback in `PredictedMeatball.cs` depend on). Everything else (C#, nodes, shaders, IK/animation)
is real work but well-trodden ground. Budget months, not weeks, and expect the netcode +
prediction layer to be re-architected rather than ported line-for-line.

---

## 1. C#: what do we lose by keeping our C# in Godot?

Godot 4 has first-class C# support via .NET (the **.NET / Mono build** of the editor, currently
.NET 8). You *can* keep writing C#. The drawbacks are real but mostly operational rather than
language-level:

**The big one — web export is dead for C#.** As of the 4.6 line there is still **no C# → WebAssembly
export**. The .NET runtime's WASM story doesn't line up with Godot's web export, with no committed
timeline. If shipping a browser build is ever on the table for this party game, C# blocks it
outright; GDScript does not. Mobile and desktop C# export work fine.

**GDExtension interop is one-directional.** You cannot call a GDExtension (native C++ addon)
*directly* from C#; you route through GDScript with a small perf penalty. This matters here because
several of the most attractive Godot answers to our problems — **Godot Jolt** (older versions),
**godot-rapier-3d** (manual physics stepping), **GodotSteam**, **Nakama** — historically ship as
GDScript-first addons or GDExtensions. Some have C# bindings; many assume GDScript. Expect glue.

**Ecosystem gravity is GDScript.** ~84% of Godot devs use GDScript (community surveys); asset-library
addons ship GDScript examples first, docs samples are GDScript, and the networking/rollback addons
that solve our hardest problems (**netfox**, demos) are GDScript. You'll be translating examples
constantly.

**What we *don't* lose:** raw throughput. For our tight numeric loops — `MeatballMotor.ApplyTick`,
the tether spring/damping math in `TetherForce.ApplyTetherForces`, the SphereCast wrap loop — C#
(especially with .NET AOT) is *faster* than GDScript and keeps our existing `struct`s, generics,
and `INetworkSerializable`-style code shape. Our pure helpers (`MeatballMotor`,
`MeatballBounceResolver`) port almost verbatim.

**Recommendation:** If we never want web, **stay in C#** — it preserves the most code and our team's
fluency. If web is a maybe, the only future-proof choice is GDScript, which means rewriting *all*
~58 scripts in a new language, not just re-homing them. Given the size of the networking/physics
rewrite anyway, C# is the pragmatic pick: it removes one giant variable (language) from an already
large port.

---

## 2. MonoBehaviour / GameObject / Transform → Godot Nodes

This is the most mechanical area. The mental model maps cleanly; the friction is in the
*composition philosophy*.

| Unity | Godot 4 | Notes |
|---|---|---|
| `GameObject` | `Node` (a `Node3D` for anything spatial) | A GameObject *has* a Transform; a `Node3D` *is* its transform. |
| `Transform` | baked into `Node3D` (`position`, `rotation`, `global_transform`) | No separate component to `GetComponent`. |
| `MonoBehaviour` | `Node` subclass (script extends a Node type) | Scripts attach to nodes, like components, but a node usually *is one* script. |
| `Awake` / `Start` | `_Ready()` | `_Ready` runs once the node + children are in the tree. |
| `Update(dt)` | `_Process(delta)` | Per-render-frame. |
| `FixedUpdate()` | `_PhysicsProcess(delta)` | Fixed-rate, default 60 Hz (we run 50 Hz — set in Project Settings → Physics → Common → Physics Ticks/sec). |
| `OnDestroy` | `_ExitTree()` / `_Notification(NOTIFICATION_PREDELETE)` | |
| `Destroy(go)` | `QueueFree()` | |
| `Instantiate(prefab)` | `PackedScene.Instantiate()` | Prefabs → **scenes** (`.tscn`). |
| `GetComponent<T>()` | `GetNode<T>(path)` / `get_node` / groups | **Different model — see below.** |
| `[SerializeField]` | `[Export]` | Shows up in the inspector identically. |
| `ScriptableObject` | custom `Resource` | Our 4 settings SOs (`MeatballMovementSettings`, `MeatballBounceSettings`, `RampSettings`, `SpaghettiReelSettings`) become `Resource` subclasses with `[Export]` fields — a clean, near-1:1 port. |
| Coroutines (`IEnumerator` + `WaitUntil`) | `await ToSignal(...)` / async / `Tween` | Our few coroutines (`ChefAnimator.WaitAndApplySkin`) become `await`. |
| `LineRenderer` | `MeshInstance3D` w/ `ImmediateMesh`, or a tube mesh | `SpaghettiRenderer` rebuild. |
| `LeanTween` | built-in `Tween` / `create_tween()` | Drop the LeanTween dependency. |

**The real adjustment — composition vs. node hierarchy.** Our meatball prefab stacks *many*
components on one GameObject: `Rigidbody` + `Collider` + `MeatballPhysicsController` +
`MeatballInputDispatcher` + `MeatballNetSync` + `PredictedMeatball` + `TetherForce` +
`SpaghettiRenderer`. Godot strongly prefers **one script per node** and modeling separation as
*child nodes* rather than co-located components. You generally cannot bolt seven scripts onto a
single `RigidBody3D` the way we bolt seven MonoBehaviours onto one GameObject. Options:

- Make the meatball root a `RigidBody3D` with one "controller" script, and push the rest into child
  nodes (`MeatballNetSync`, `TetherForce` as children that reference the parent body), **or**
- Collapse several of our components into fewer, larger scripts.

Our ~97 `GetComponent`/`TryGetComponent` call sites all need rethinking. In Godot you reference
peers by node path or by exporting a `NodePath`/node reference, or via **groups** (Godot's analogue
to our static `Instances`/`ServerInstances` lists — e.g. `add_to_group("tethers")` replaces
`TetherForce.Instances`). The static-`List<T>`-of-instances pattern we use in `TetherForce` and
`SpaghettiRenderer` works fine in C# in Godot too, so those can stay as-is.

**Effort:** Medium, but pervasive — it touches every script. No conceptual blockers.

---

## 3. Networking: replacing NGO + Unity Multiplayer Services

This is where the most design work lives. Let me first restate **what the Unity stack gives this
game**, because it's two separate layers that Godot replaces with two *different* answers.

### What we have today (two layers)

**Layer A — the managed online service (`Unity.Services.Multiplayer`).** In
`MeatballMultiplayerSessionManager.cs` we use `MultiplayerService.Instance`:
- `CreateSessionAsync()` — host creates a session, gets back a **join code** (`m_HostSession.Code`).
- `MatchmakeSessionAsync(quickJoinOptions, …)` — **QuickJoin**, drops a player into any open session.
- `JoinSessionByCodeAsync(code, …)` — **Join-by-code** (the `JoinByCodeUI`).
- Under the hood this is Unity **Lobby + Relay**: NAT punch-through / relay so players behind home
  routers connect without port forwarding, plus the lobby/roster service. We also pull in
  `com.unity.services.vivox` for voice.

**Layer B — the transport + replication (`com.unity.netcode.gameobjects` 2.11).** This is the
host-authoritative simulation machinery:
- `NetworkManager` / `NetworkObject` — connection lifecycle, spawning networked objects, ownership.
- `NetworkBehaviour` + `ServerRpc`/`ClientRpc` — our entire sync protocol:
  `SubmitFrameRedundantServerRpc` (input up), `ReceiveSnapshotClientRpc` (state down),
  `SyncPivotsClientRpc` (tether), `BroadcastTickClientRpc` (clock).
- `INetworkSerializable` — wire format for `InputFrame`, `MeatballSnapshot`, `CollisionImpulse`.
- Ownership semantics (`IsOwner`, `IsServer`, `OwnerClientId`) gate every prediction/interpolation
  branch.

**The architecture itself** (independent of engine): a 50 Hz tick clock (`NetworkTick`), clients
send tick-stamped input, host runs the *only* authoritative PhysX sim, broadcasts
`MeatballSnapshot` at 30 Hz; non-owners **interpolate/extrapolate** a kinematic ghost, the owner
**predicts locally and reconciles** via rollback+replay (`PredictedMeatball`). The tether
(`TetherForce`) is host-only and its pivots are streamed to clients for rendering.

### What Godot provides (Layer B)

Godot ships a **high-level multiplayer API** that covers most of NGO's *replication* role:
- `ENetMultiplayerPeer` — the default UDP transport (host/client, like NGO's UnityTransport).
- `@rpc` annotations on functions — direct analogue to `ServerRpc`/`ClientRpc`. You can specify
  `authority`/`any_peer`, `reliable`/`unreliable`, and call-local. Our four RPC channels map onto
  `@rpc` functions cleanly.
- `MultiplayerSpawner` — replicates instancing of scenes (NGO's `NetworkObject.Spawn`).
- `MultiplayerSynchronizer` — declaratively syncs exported properties of a node. **Useful for the
  non-owner ghost interpolation**, but it is *transform/property* sync that will fight a live
  physics sim in exactly the way `CLAUDE.md` already warns about `NetworkRigidbody` — so for the
  owner/host meatballs we'd hand-roll the snapshot RPCs just like today, not lean on it.
- Wire format: Godot RPCs auto-serialize Variants. Our `INetworkSerializable` structs become either
  plain arrays/dictionaries or **custom byte packing** (`PackedByteArray` + `encode_*`) for
  compactness. `MeatballSnapshot` (tick + pos + rot + vel + angVel + collision sidecar) is small and
  hand-packs easily.
- `multiplayer.get_unique_id()` / `is_server()` / per-node `set_multiplayer_authority()` replace
  `OwnerClientId` / `IsServer` / `IsOwner`. This is a good fit — Godot's per-node authority model
  maps directly onto our per-meatball ownership.

So **Layer B (transport + RPC + spawning + ownership) ports well.** It's a rewrite, but a faithful
one — every RPC we have has a home.

### What Godot does *not* provide (Layer A) — the real gap

**Godot has no first-party Lobby/Relay/QuickJoin/matchmaking service.** There is no managed
NAT-punch relay, no `CreateSessionAsync`→join-code, no `MatchmakeSessionAsync`, and no built-in
voice (Vivox). `ENetMultiplayerPeer` assumes a directly reachable host:port — which means **port
forwarding** unless you add a relay. Replacing Layer A is a *product* decision with three common
paths:

1. **GodotSteam (Steam lobbies + P2P relay).** If we ship on Steam, `SteamMultiplayerPeer` gives us
   Steam's relay (no port forwarding), friend invites, and lobby creation/joining by lobby ID — the
   closest off-the-shelf analogue to our join-code + quick-join flow, and it includes Steam voice.
   This is the most popular answer for a Steam-bound co-op party game and the lowest-effort path to
   feature-parity with our current join-code UX. Downside: Steam-only.
2. **Nakama (Heroic Labs).** Open-source/self-hostable game backend with a real **matchmaker**
   (`add_matchmaker_async`), authoritative match handlers, and an official Godot client. This most
   closely matches Unity's *managed services* shape (QuickJoin ≈ matchmaker pool, lobby ≈ match) and
   is cross-platform, but it's a server you host/operate.
3. **Roll-your-own relay** (e.g., a small headless Godot server, or a WebSocket/ENet relay on a
   VPS). Most control, most work, you own NAT traversal.

**Bottom line on networking:** Layer B (the host-authoritative replication, the part `CLAUDE.md`
cares about most) is a faithful rewrite onto Godot's `@rpc` + ENet + per-node authority. Layer A
(the thing that makes "press Quick Join and you're in a game with a friend behind a router" work)
has **no drop-in** and must be re-platformed onto Steam or Nakama — a meaningful scope item, plus
re-doing voice.

### How do people do this architecture (host-authoritative + client prediction + host physics) in Godot?

The same way we do, with the same caveats — and it's a known-hard problem on Godot for the same
reason it's hard on Unity: **engine physics is non-deterministic and not designed to be re-stepped.**
The community-standard toolkit is **netfox**, a set of GDScript addons that provides exactly our
shape: a fixed network tick clock (its `_rollback_synchronizer` / `_tick_interpolator`),
**client-side prediction + server reconciliation**, and interpolation — i.e. netfox is the Godot
equivalent of the bespoke `NetworkTick` + `PredictedMeatball` + `MeatballNetSync` layer we wrote by
hand. There are also full reference projects (authoritative server + prediction + reconciliation +
lag comp) and commercial middleware (**Netick for Godot**). The catch is that all of these, like our
system, predict by **re-running game/physics logic per tick** — which lands us squarely on the
physics problem below.

---

## 4. Physics: Rigidbodies, colliders, and the `Physics.Simulate` problem

Two-thirds of this is easy and one-third is the single scariest item in the whole port.

### The easy two-thirds — bodies, colliders, forces, queries

Godot 4 has direct equivalents, and as of **4.4 Godot bundles Jolt** (default 3D engine in 4.6),
which is a mature PhysX-class solver:

| Unity | Godot 4 |
|---|---|
| `Rigidbody` (dynamic) | `RigidBody3D` |
| `Rigidbody.isKinematic` (our ghost peers) | `AnimatableBody3D` / freeze mode, or `RigidBody3D` frozen |
| `Collider` (Sphere/Box/Mesh) | `CollisionShape3D` with `SphereShape3D` etc. |
| `PhysicsMaterial` (friction/bounce) | `PhysicsMaterial` resource |
| `rb.AddForce(f, Force)` | `apply_central_force(f)` / inside `_integrate_forces` |
| `rb.AddForce(f, Impulse)` | `apply_central_impulse(f)` |
| `rb.MovePosition/MoveRotation` (interp) | move an `AnimatableBody3D`, or set transform |
| `Physics.SphereCast` (our tether wrap!) | `PhysicsDirectSpaceState3D.intersect_shape` / `cast_motion` with a sphere |
| `Physics.OverlapSphereNonAlloc` (ground check) | `intersect_shape` / a `ShapeCast3D` node |
| `OnCollisionEnter` (host bounce) | `body_entered` signal / `_integrate_forces` contact data, or contact monitoring |
| Movement in `FixedUpdate` | `_PhysicsProcess` / `_integrate_forces` |
| Set physics rate to 50 Hz | Project Settings → Physics ticks per second = 50 |

`MeatballMotor.ApplyTick`, the tether spring math, `MeatballBounceResolver`, and the SphereCast
wrap/unwind loop in `TetherForce` all have clean Godot homes. The tether's SphereCasts become
`intersect_shape`/`cast_motion` calls on the space state — a faithful port, though Godot's
shape-query ergonomics differ (you build a `PhysicsShapeQueryParameters3D`, results are dictionaries).
`Physics.DefaultRaycastLayers`/`LayerMask` → Godot's `collision_mask` bitmasks. Contact-point/normal
data for inserting pivots is available but accessed differently.

For per-body custom integration Godot has `RigidBody3D` with `custom_integrator = true` and an
overridden `_integrate_forces(state)` — close in spirit to writing forces in `FixedUpdate`, and the
right hook for our movement/tether forces.

### The scary one-third — manual stepping for prediction/rollback

Here is the load-bearing fact. **`PredictedMeatball.cs` does not use Unity's automatic physics
loop. It does:**

```csharp
// OnNetworkSpawn (owner client only):
_prevSimulationMode = Physics.simulationMode;
Physics.simulationMode = SimulationMode.Script;   // take over the physics clock
...
// during reconciliation replay, per buffered tick:
MeatballMotor.ApplyTick(_rb, frame, _settings, grounded, rampAugment, out _);
if (manualStep) Physics.Simulate(Time.fixedDeltaTime);   // step ONE tick by hand
```

That is the core of our client-side prediction: when a `MeatballSnapshot` arrives, we snap the
Rigidbody to the authoritative state at tick `T`, then **replay** inputs for ticks `T+1 … current`
by calling `Physics.Simulate(fixedDt)` once per tick — *N physics steps inside a single frame*,
under our control. `MeatballNetSync` and `MeatballPhysicsController` also assume the host runs a
clean one-step-per-tick `FixedUpdate`, and snapshots record `rb.linearVelocity` *after*
`Physics.Simulate` for the tick.

**Godot has no public equivalent to `Physics.Simulate` / `Physics.simulationMode = Script`.** You
cannot, from script, tell Godot's built-in physics world (Godot Physics or bundled Jolt) to "advance
exactly one step now, then again, then again" within a frame. This is a long-standing open
feature request (godot-proposals #2821); the engine owns the physics clock and ticks it once per
`_physics_process` at the fixed rate. This breaks our prediction/rollback approach directly. Options,
worst-to-best for us:

1. **Don't manually step — change the prediction strategy.** Use **netfox**-style rollback, where on
   reconciliation you set the body's transform/velocity and let Godot's *normal* `_physics_process`
   ticks re-converge, accepting that you can't cram N catch-up steps into one frame. This is the
   "intended" Godot path and is what most Godot prediction uses, but it changes the feel/latency
   characteristics of our owner correction and needs careful tuning. Lowest-tech, highest-rework of
   our existing netcode design.
2. **Use a steppable third-party physics engine.** **godot-rapier-3d** (a Rapier GDExtension) exposes
   `Rapier3D.step()` you can call manually from `_physics_process` — i.e. it restores the
   `Physics.Simulate`-style control we rely on, *and* Rapier is designed with cross-platform
   determinism in mind (better rollback story than PhysX or Godot-Jolt, which explicitly does **not**
   guarantee determinism). Cost: you leave Godot's native physics nodes for Rapier's, replacing
   `RigidBody3D`/queries with Rapier equivalents, and (per §1) it's a GDExtension so C# interop goes
   through GDScript glue. This is the closest structural match to today's code.
3. **Custom tick-manager hack** over Godot's fixed step — run physics faster and subdivide. Fragile;
   doesn't really give arbitrary N-step replay. Not recommended.

**This is the single most important finding in the document.** Our netcode's correctness currently
*depends on a Unity-specific capability Godot does not expose*. Any Godot port must either (a) adopt
a Rapier/GDExtension that re-exposes manual stepping (closest to current design, recommended if we
keep the rollback model), or (b) re-architect prediction to the netfox/`MultiplayerSynchronizer`
model that doesn't manually step. Either way the entire `PredictedMeatball` / `MeatballNetSync` /
`MeatballPhysicsController` triad is **redesigned, not ported.**

Note also the determinism angle: our `CLAUDE.md` already accepts PhysX non-determinism and goes
host-authoritative *precisely* to sidestep it; Godot-Jolt carries the same "looks deterministic, not
guaranteed" caveat, so host-authoritative remains the right call. Rapier is the only option that
opens the door to *true* lockstep determinism later, if we ever wanted it.

---

## 5. Shaders & materials

Low risk. Mostly mechanical.

**We have very little custom shader surface.** Per the inventory: one hand-written shader,
`Assets/Shaders/SpaghettiNoodle.shader` (URP HLSL — fakes a cylindrical normal across the
LineRenderer's UV.y for specular/rim on the noodle), one ShaderGraph (`Tablecloth.shadergraph`,
gingham), a couple of plain materials, plus stock TextMesh Pro shaders. That's the whole job.

**HLSL → Godot shading language.** Godot does **not** consume Unity's HLSL/ShaderLab directly. Its
shading language is **GLSL-ES-3.0-based** ("GDShader"). The good news, per Godot's own
"Converting GLSL to Godot" docs: it's ~90% familiar — the differences are largely cosmetic
(`float3`→`vec3`, `tex2D()`→`texture()`, `lerp`→`mix`, a `fragment()`/`vertex()`/`light()` function
structure, and Godot's own built-ins for lighting instead of URP's `#include` macros). Our
`SpaghettiNoodle` shader's math (reconstruct normal from UV.y, Blinn-style spec + rim) translates
directly; the URP-specific scaffolding (keywords like `_MAIN_LIGHT_SHADOWS`, the URP lighting
includes) gets replaced by Godot's `spatial` shader model and its automatic light handling. A few
hours of work and a visual A/B.

**Material/ShaderGraph porting tools.** There is **no reliable automated Unity-material → Godot
converter**. ShaderGraph (`Tablecloth.shadergraph`) has no importer — it must be **rebuilt** in
Godot's **VisualShader** editor (node-for-node, straightforward for a gingham pattern) or rewritten
as a small GDShader. For the HLSL, people use the official GLSL-conversion guide plus, increasingly,
LLM-assisted "port this HLSL to GDShader" passes — with the firm caveat that you must verify Godot's
built-in names by hand. Standard PBR materials (BaseColor/metallic/roughness/normal) map onto Godot's
`StandardMaterial3D` with no shader code at all.

**Effort:** Low. One real shader + one ShaderGraph to rebuild. The bigger cosmetic task is re-authoring
materials on every imported mesh, which falls out of the model re-import work in §6.

---

## 6. Chef characters: IK rig + animations (`TheOneTrueChef.prefab`)

You're right to call this out — and right that it's heavier than it looks. I verified the prefab.

### What's actually in there

`Assets/Art Assets/Pack_Chefs/Prefabs/TheOneTrueChef.prefab` is **not** just an FBX. It carries a full
**Unity Animation Rigging** (`com.unity.animation.rigging` 1.4.1) setup configured in-editor:
- 1 `RigBuilder`, 3 `Rig` layers, an `IKHandles` hierarchy.
- **14 `TwoBoneIKConstraint`s** with **42 target/pole references** — arms, legs, **and every finger**
  (thumb/index/middle/ring/pinky on each hand), each as a `…Constraint` + `…Target` + `…Pole(hint)`
  triad (`LeftArmTarget`/`LeftArmPole`, `RightHandIndexTarget`/`…Pole`, etc.).

Worth noting a wrinkle the inventory surfaced: the **currently shipping** chef (`AnimatedChef.prefab`
driven by `ChefAnimator.cs`) does **not** use any of this rig — `ChefAnimator` drives the skin by
swapping meshes and by **manually writing `_skinRoot.rotation` / `_spineBone.localRotation`** for
facing and lean (no IK API calls at all; the Animation Rigging package is installed but the live
character doesn't use it). So there are effectively two things to talk about: the simple
script-driven lean (trivial to port — it's just quaternion math in `LateUpdate`, → Godot
`_process` writing bone poses on a `Skeleton3D`) and the elaborate `TheOneTrueChef` IK rig (the
heavy item).

### Godot equivalents to the IK setup

Godot is mid-transition here, which matters for which version we target:

- Godot's modern bone-modification system is **`SkeletonModifier3D`** (4.3+) — the base class for
  nodes that post-process a `Skeleton3D` after the animation plays, with a clean, deterministic
  processing order (it fixed the old "IK runs before/after the AnimationMixer depending on tree
  order" problem). This is the conceptual analogue of Animation Rigging's rig-layer-on-top-of-clip
  model.
- For **two-bone IK specifically** (which is *all* our constraints are), Godot's older
  `SkeletonIK3D` exists but is **deprecated** in 4.x, and the built-in modern IK solver nodes were
  still being filled in across the 4.4+ releases. In practice today people use **`SkeletonModifier3D`
  subclasses** (built-in LookAt/“lookat”-style modifiers for the head/aim, custom two-bone solvers)
  or a community addon. There is **no one-click import of a Unity Animation Rigging rig** — the rig
  graph (constraints, targets, poles, weights) has no exchange format and must be **rebuilt** in
  Godot against the imported skeleton.
- The honest assessment: **14 two-bone constraints including per-finger IK is a lot to re-author by
  hand.** Two mitigations worth weighing: (1) most per-finger IK on a stylized chef is likely cosmetic
  — finger poses can often be baked into the animation clips and dropped from runtime IK entirely,
  collapsing 10 of the 14 constraints; (2) Godot’s IK is improving release-over-release, so targeting
  the latest 4.x reduces the amount of custom solver code.

### Animations (clips + controller)

- **Clips:** `chefWalk.anim`, `emote_mamamia.anim`, `idle_pose.anim`. Unity `.anim` is a proprietary
  YAML asset; **Godot can't read it.** The reliable path is to go through the **source rig**: import
  the FBX (Godot 4.3+ has the much-improved **ufbx** importer; glTF is even safer — export from
  Blender as glTF 2.0), which brings clips in as Godot `Animation` resources on an `AnimationPlayer`.
  If the only copy of an animation is the Unity `.anim`, it has to be re-exported through Blender/FBX
  or baked, **or** run through the community **`unidot_importer`** (a GDScript addon that imports
  `.unitypackage`/prefabs and *does* handle humanoid `.anim` and animation-tree porting — the closest
  thing to an automated Unity→Godot animation converter, but expect cleanup).
- **`ChefAC.controller` (Animator Controller)** → Godot **`AnimationTree`** with a `StateMachine`
  root; our single `Speed` float blend (idle↔walk) and the `emote_mamamia` one-shot map directly onto
  an `AnimationNodeStateMachine` / blend + a one-shot. Conceptually 1:1, terminology differs.
- **Retargeting:** Godot 4 has a built-in **SkeletonProfile**-based retargeting system (analogous to
  Unity's Humanoid Avatar), so once the skeleton is imported, mapping clips onto it is supported
  natively.
- **The `ChefAnimator` driving code** (`SetFloat("Speed", …)`, `CrossFade("emote_mamamia", …)`,
  manual spine writes) ports to: set `AnimationTree` parameters, fire the one-shot, and write bone
  poses on the `Skeleton3D` from `_process` — small, faithful changes. Mind the `CLAUDE.md` "Apply
  Root Motion" footgun has a Godot cousin: in Godot you'll fight `AnimationTree`/root-motion vs. your
  manual bone writes via the `SkeletonModifier3D` ordering rather than an "Apply Root Motion"
  checkbox — same class of bug, different lever.

**Effort:** Medium-high. Re-importing meshes/clips through glTF is routine; **rebuilding the
14-constraint IK rig and the AnimationTree is the bulk of the character work**, and there's no
shortcut tool that does it losslessly (`unidot_importer` gets you partway).

---

## Overall recommendation & rough effort map

| Area | Portability | Effort | Risk |
|---|---|---|---|
| C# language | Keep C# (lose web export only) | Low | Low |
| Nodes / MonoBehaviour / Transform | Clean conceptual map; touches every file | Medium | Low |
| Settings ScriptableObjects → `Resource` | Near 1:1 | Low | Low |
| Networking **Layer B** (RPC/spawn/ownership) | Faithful rewrite onto `@rpc`/ENet | Medium-High | Medium |
| Networking **Layer A** (Lobby/Relay/QuickJoin/voice) | **No drop-in** → Steam or Nakama | High | **High** |
| Physics bodies/colliders/forces/queries | Direct equivalents (Jolt) | Medium | Low-Medium |
| Physics **manual stepping** (`Physics.Simulate`) | **No native equivalent** → Rapier or redesign prediction | High | **High** |
| Shaders (1 HLSL + 1 ShaderGraph) | Rewrite to GDShader/VisualShader | Low | Low |
| Chef animations/clips/controller | Re-import via glTF; AnimationTree | Medium | Medium |
| Chef **IK rig (14× TwoBoneIK)** | Rebuild by hand; no lossless tool | Medium-High | Medium |

**The two red cells drive the whole decision:**
1. **Layer-A networking** — our QuickJoin/join-code/relay/voice has no Godot first-party answer; we'd
   commit to **Steam (GodotSteam)** or **Nakama**, plus re-do voice.
2. **Manual physics stepping** — our prediction/rollback in `PredictedMeatball` relies on
   `Physics.Simulate`, which Godot doesn't expose. We'd either adopt **godot-rapier-3d** (keeps our
   design, GDExtension cost) or **re-architect prediction** to the netfox model.

If both of those are acceptable, the rest is a large-but-ordinary rewrite. My recommendation if we
pursue this: **C# + Godot Jolt-or-Rapier + netfox-or-custom RPC + GodotSteam**, prototype the
*meatball + tether + 2-player host-authoritative prediction* slice **first** (it's where 90% of the
risk concentrates) before touching content, chefs, or UI. Prove the physics-stepping and Layer-A
relay decisions on a vertical slice; everything else is derisked once those two hold.

---

## Open questions for you

1. **Is web export ever a target?** It's the one thing that would force GDScript over C# and roughly
   double the rewrite. If desktop/Steam-only, C# is the clear pick.
2. **What's the distribution plan — Steam-exclusive or broader?** This decides GodotSteam vs. Nakama
   for Layer A, and whether we get relay + voice "for free" from Steam.
3. **Do we keep the rollback/prediction model, or accept the simpler netfox-style reconciliation?**
   This decides Rapier (manual stepping) vs. native Jolt + redesign.
4. **Is `TheOneTrueChef`'s per-finger IK gameplay-relevant, or cosmetic?** If cosmetic, baking finger
   poses into clips removes ~10 of the 14 constraints and most of the character-rig effort.

---

## Sources

- [GDScript vs C# in Godot 4 — Chickensoft](https://chickensoft.games/blog/gdscript-vs-csharp)
- [Godot Shaders in 2026 / GDShader — Ziva](https://ziva.sh/blogs/godot-shaders)
- [Converting GLSL to Godot shaders — Godot docs](https://docs.godotengine.org/en/stable/tutorials/shaders/converting_glsl_to_godot_shaders.html)
- [High-level multiplayer — Godot docs](https://docs.godotengine.org/en/stable/tutorials/networking/high_level_multiplayer.html)
- [netfox — addons for online multiplayer games](https://github.com/foxssake/netfox)
- [MonkeNet — client/server authoritative addon](https://github.com/grazianobolla/godot-monke-net)
- [Add ability to simulate physics manually — godot-proposals #2821](https://github.com/godotengine/godot-proposals/issues/2821)
- [godot-rapier-3d (manual `step()`)](https://github.com/deltasiege/godot-rapier-3d)
- [Godot Jolt](https://github.com/godot-jolt/godot-jolt) · [Godot 4.4 native Jolt](https://gamefromscratch.com/godot-4-4-gets-native-jolt-physics-support/)
- [RigidBody3D custom integrator / `_integrate_forces`](https://rokojori.com/en/labs/godot/docs/4.4/rigidbody3d-class)
- [Design of the SkeletonModifier3D — Godot Engine](https://godotengine.org/article/design-of-the-skeleton-modifier-3d/)
- [Improved ufbx importer in Godot 4.3 — Godot Engine](https://godotengine.org/article/introducing-the-improved-ufbx-importer-in-godot-4-3/)
- [unidot_importer — import Unity packages/prefabs/.anim into Godot](https://github.com/V-Sekai/unidot_importer)
- [GodotSteam — lobbies & matchmaking](https://godotsteam.com/tutorials/lobbies/)
- [Nakama Godot client (matchmaker)](https://github.com/heroiclabs/nakama-godot)
- [Unity→Godot node lifecycle migration guide](http://robochase6000.github.io/2023/09/23/unity-to-godot-migration-guide-node-lifecycle.html)
</content>
</invoke>
