# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Game Concept

**Meatball Madness** is a 3D physics-based co-op game for 2+ players (initially 2). Each player controls a meatball connected to their partner by an elastic spaghetti tether. Players must coordinate to navigate obstacle courses — the tether's elastic physics is the central challenge and source of humor.

Core appeal: silly difficulty, forced communication, emergent chaos from the tether constraint.

## Tech Stack

- **Unity 3D** with PhysX (`com.unity.modules.physics`)
- **Unity Input System** (`com.unity.inputsystem` 1.19.0) — action maps in `Assets/InputSystem_Actions.inputactions`; C# wrapper auto-generated at `Assets/InputSystem_Actions.cs`. Do NOT use Input.GetKey or Input.GetAxis, always subscribe to the callbacks in IPlayerActions
- **Netcode for GameObjects (NGO)** via `com.unity.multiplayer.center` — install NGO from there
- **Visual Scripting** and **Timeline** available but prefer C# for game logic

## Architecture Decisions

### Meatball Physics — Unity Rigidbody + PhysX

Use Unity `Rigidbody` components for the meatballs. Apply movement as forces in `FixedUpdate`, never by setting `transform.position` directly.

**Why:** Zero groundwork for collision, sleeping, and broadphase. PhysX is mature and editor-integrated. The "silly" feel is achieved by tuning drag, mass, and force scale — not by fighting the solver.

**Tradeoff accepted:** PhysX is non-deterministic across machines. We accept this by going host-authoritative (see Networking below) so only the host's simulation is canonical.

### Tether — Custom Force + Verlet Visual

**Do not use `SpringJoint` or any Unity joint for the spaghetti tether.** Unity joints are known to explode (diverge to infinity) at high stiffness and are hard to tune for an elastic/bouncy feel.

Instead:
- **Force layer:** In `FixedUpdate`, measure the distance between the two meatball `Rigidbody` positions. If distance exceeds rest length, apply `AddForce` to each meatball along the connecting vector, scaled by stretch amount (Hooke's law: `F = k * stretch`). Include a damping term proportional to relative velocity to prevent endless oscillation.
- **Visual layer:** Render the spaghetti using 3–5 Verlet integration points that don't participate in physics — they just follow the meatball endpoints each frame and sag/swing cosmetically. Use a `LineRenderer` or ribbon mesh.

**Why custom:** Stable at any stiffness, no solver explosions, trivially networkable (only the endpoints matter), and gives direct control over the "feel" of the tether.

**Tradeoff accepted:** We own the tether math. The force application and damping constants will require manual tuning to feel good.

### Networking — Host-Authoritative NGO

Use **Netcode for GameObjects (NGO)** with a host-authoritative model:
- The host runs the full PhysX simulation for both meatballs
- Each client sends input to the host; the host applies it and owns the result
- Sync `(position, velocity, angularVelocity)` for each meatball via a custom `NetworkBehaviour` — do not rely on `NetworkRigidbody` alone, as it only syncs transform and fights the physics simulation
- The tether force runs on the host only; clients receive meatball positions and render the tether visually from those

**Why host-authoritative:** Sidesteps PhysX non-determinism entirely. The host simulation is the single source of truth. Simpler than lockstep or rollback netcode for a 2-player co-op game.

**Tradeoff accepted:** The client's meatball will lag one round-trip behind the host's. Tether forces will feel slightly delayed for the non-host player. For a silly co-op game this is acceptable and can even read as intentional chaos.

### Input — Unity Input System (callback subscription)

Always use the generated `InputSystem_Actions` C# wrapper for input. Never use the legacy `Input` class (`Input.GetAxis`, `Input.GetButtonDown`, etc.).

The preferred pattern is the **callback subscription** method:
1. Implement `InputSystem_Actions.IPlayerActions` on your `MonoBehaviour`.
2. In `Awake`, create an `InputSystem_Actions` instance, get the `Player` action map, and call `_player.AddCallbacks(this)`.
3. Enable the map in `OnEnable`, disable it in `OnDisable`, and call `_actions.Dispose()` in `OnDestroy`.
4. Handle input state changes inside the `OnMove`, `OnJump`, `OnLook`, `OnSprint` (etc.) callbacks — store values or set flags that `FixedUpdate` reads.

**Why callbacks over polling:** Callbacks are event-driven and fire exactly when input changes. Polling `ReadValue` every frame works but ties input sampling to frame rate and makes it easy to miss short-pressed buttons. The callback pattern also cleanly separates input registration from game logic.

## Development Workflow

- Build and iterate from the Unity Editor (no CLI build scripts)
- `Assets/Scenes/AdamScene.unity` and `Assets/Scenes/LukeScene.unity` are personal sandbox scenes per developer — do not treat these as shippable
- Build shared gameplay as **prefabs** (Meatball prefab, Tether prefab, etc.) so changes propagate across scenes without merge conflicts
- Unity `.unity` scene files are YAML and will conflict — minimize scene-level changes, prefer prefab-based workflows
