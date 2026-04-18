# Netcode Overhaul: Reducing Input Latency for Meatball Madness

## Context

Meatball Madness currently runs host-authoritative NGO. Every client sends input via `SubmitInputServerRpc`, the host simulates, and clients interpolate toward snapshots sent every FixedUpdate. The user experience: **non-host players feel one round-trip of input lag** (~50–150 ms on typical connections) before their meatball responds. For a tethered physics co-op game where inputs need to feel immediate, this is the primary pain point.

The ask: move to rollback netcode to minimize input latency while keeping physics faithful across all clients.

## The honest answer about "rollback"

**True lockstep rollback (GGPO-style) is not viable on stock Unity PhysX.** Research-backed reasons:

1. **PhysX is non-deterministic across machines.** Unity has never promised 3D physics determinism. Floating-point results diverge across CPU vendors (Intel/AMD/Apple Silicon), SIMD paths, and driver versions. Run for >30 seconds and peers desync. The project's own `CLAUDE.md` already acknowledges this ("PhysX is non-deterministic across machines. We accept this by going host-authoritative").
2. **PhysX has hidden state that can't be snapshotted.** Persistent contact manifolds, solver warm-start impulses, sleep/island state, broadphase pairs — none are serializable through public Unity APIs. Restoring position+rotation+velocity and re-calling `Physics.Simulate` does not reproduce the prior trajectory exactly. Drift compounds with stacked/resting bodies — precisely our scenario (4 meatballs piling at spawn).
3. **Re-simulating PhysX 10–25 ticks per frame is expensive.** Netick's own docs: *"Predicting 3D physics using PhysX is particularly expensive… not recommended."* At 100 ms RTT you re-sim ~6 ticks/frame; at 300 ms, ~22. On modest hardware that alone can exceed 10 ms/frame with tether wrap geometry in the hot loop.

The only path to *true* rollback is **Photon Quantum** — a full rewrite of the meatball and tether in fixed-point math on Quantum's deterministic ECS physics engine. That's a project reset, not a migration. Not recommended for this game.

## Recommended approach: Client-Side Prediction + Server Reconciliation (CSP+R)

This is what Rocket League, Fall Guys, Gang Beasts, and Human Fall Flat ship. It's a *form* of rollback — it rewinds and re-simulates — but only on each client, only for that client's own meatball, only when a reconciliation arrives. It does not attempt determinism across peers. The host remains authoritative; clients predict locally for instant input feedback and smoothly correct when the host disagrees.

Expected outcome:
- Player's **own** meatball responds to their input in ~1 frame instead of 1 RTT.
- Other players' meatballs still lag one RTT — but that's fine for a silly co-op game (already accepted in `CLAUDE.md`).
- Tether remains host-authoritative, rendered from received endpoints. Zero divergence risk because clients never simulate the spring.

## Technical plan

### 1. Tick synchronization and the shared tick clock

**New script: `NetworkTick.cs` (NetworkBehaviour, attached to a singleton NetworkObject).**

- Host increments a `currentTick : ulong` each `FixedUpdate` (50 Hz per `DynamicsManager.asset`).
- Clients estimate their local tick offset using RTT measurement. Target: `clientTick = hostTick + RTT/2_in_ticks + buffer (2–3 ticks)`. Goal is that inputs produced at `clientTick=T` arrive at host in time to be processed on host's tick `T`.
- Expose `NetworkTick.Current` as the authoritative tick counter used by every predicted/reconciled system.

Reference: Gaffer On Games *State Synchronization*. Align every input, snapshot, and reconcile against this tick.

### 2. Input pipeline: tag, buffer, retransmit

**Modify: [MeatballClientInputHandler.cs](Assets/Scripts/MeatballClientInputHandler.cs)**

Today: each FixedUpdate, owner reads input and fires `SubmitInputServerRpc(move, jump, sprint)` once.

Change:
- Introduce `struct InputFrame { ulong tick; Vector2 move; bool jump; bool sprint; }`.
- Maintain a **client-side ring buffer** `CircularBuffer<InputFrame> _inputHistory` (size ~64, covers >1 second at 50 Hz).
- Each FixedUpdate, push current input to history tagged with `NetworkTick.Current`.
- Send a **redundant input packet** covering the last N frames (e.g. N=4): `SubmitInputsServerRpc(ulong startTick, InputFrame[] frames)`. Redundancy protects against UDP drops without retransmit overhead. Rocket League pattern.
- Also record each frame into a **predicted-state buffer** (see §4).

### 3. Server-side input buffer and authoritative simulation

**Modify: [MeatballPhysicsController.cs](Assets/Scripts/MeatballPhysicsController.cs)**

Today: `SubmitInputServerRpc` appends to a `simulatedLatencyMs` queue (editor-only latency test tool). **Delete this queue entirely** — it doesn't fit the new paradigm, and the new tick-keyed buffer subsumes its purpose (artificial latency can be re-added later as a `_debugLatencyTicks` field on the input dispatcher if still needed for testing).

New flow:
- **Per-client input jitter buffer** keyed by tick: `Dictionary<ulong, InputFrame> _inputsByTick` on the host.
- On host FixedUpdate for tick `T`:
  1. Look up each client's input at tick `T`.
     - If present: apply it.
     - If missing but a newer input exists for tick `T+k` (reordered arrival): drop the older request; latest-known-input wins.
     - If fully missing: **repeat last-known input** (Rocket League "decay by age"). Cap at 10 ticks of repeat; beyond that, zero `move` and release `jump`/`sprint`/`reel`.
  2. Apply movement via `MeatballMotor.ApplyTick` (§4), tether force, reel force.
  3. After `Physics.Simulate` completes, capture authoritative snapshot (§5) + any collision events generated this tick (§7).

No change to force application math — just sourcing input by tick instead of draining a time-delayed queue.

**Removing the latency queue is step 1 of the rollout** (§10) — it's a prerequisite, not a parallel change.

### 4. Client-side prediction for the owned meatball

**New script: `PredictedMeatball.cs` (runs only on the owning client; replaces host-authoritative behavior for local meatball).**

Key insight: the owning client must locally run the **same physics code** as the host for its own meatball so local input feels instant.

- On spawn for the local owner: **do not set Rigidbody kinematic** (contradicts current [MeatballNetSync.cs:~80](Assets/Scripts/MeatballNetSync.cs) — needs to branch on `IsOwner && !IsServer`).
- Each FixedUpdate on client:
  1. Read local input for `clientTick = NetworkTick.Current`.
  2. Apply forces via the same code path as `MeatballPhysicsController.ApplyMovement` / `ApplyJump`. Factor the force logic into a pure helper (`MeatballMotor.ApplyTick(rb, input, settings, grounded)`) that both host and owning client call — avoid two copies drifting.
  3. Let Unity's `Physics.Simulate` run (auto mode is fine since we're only predicting locally).
  4. Record `PredictedState { ulong tick; Vector3 pos; Quaternion rot; Vector3 vel; Vector3 angVel; }` into a ring buffer keyed by tick.
- **Do not predict other players' meatballs, the tether, or any obstacle.** Those keep today's snapshot-interpolation model. Only your own meatball predicts.

### 5. Server snapshots with tick stamps

**Modify: [MeatballNetSync.cs](Assets/Scripts/MeatballNetSync.cs)**

Today: four NetworkVariables (pos, rot, vel, angVel) updated every FixedUpdate, no tick stamp.

Change:
- Replace the four NetworkVariables with a single **unreliable ServerRpc-broadcast snapshot** at a throttled rate (30 Hz is plenty — the owner is predicting, others interpolate):
  ```
  struct MeatballSnapshot { ulong tick; Vector3 pos; Quaternion rot; Vector3 vel; Vector3 angVel; }
  ```
- Send via a `BroadcastSnapshotClientRpc(MeatballSnapshot)` targeted to all clients. Or batch all meatballs' snapshots into one `ClientRpc` per tick from a central dispatcher to reduce RPC overhead (4 meatballs × 50 Hz × 4 = 800 RPCs/sec otherwise).
- Clients route snapshots into two paths:
  - **Owner path** (§6): feed into reconciliation buffer.
  - **Non-owner path**: feed into existing Lerp/Slerp interpolation — same as today, just with a tick stamp now available.

### 6. Reconciliation (the "rollback" part, client-local)

**New: `ReconcileOwnedMeatball` logic inside `PredictedMeatball.cs`**

When a snapshot arrives for the owned meatball at tick `T_auth`:

1. Look up the predicted state at the same tick: `predicted = _predictedStates[T_auth]`.
2. Compute error: `posError = |predicted.pos - authoritative.pos|`.
3. **Always correct the Rigidbody immediately** — re-simulate forward from the authoritative state to current client tick:
   - Snap `rb.{position, rotation, linearVelocity, angularVelocity}` to authoritative values.
   - Replay inputs from `_inputHistory[T_auth+1 .. currentClientTick]` via `MeatballMotor.ApplyTick` + manual `Physics.Simulate(fixedDt)` per tick. (Requires `Physics.autoSimulation = false` during the rollback window, then re-enabled; or use `Physics.SimulateWithCallbacks`-style manual stepping — see Unity 6 `Physics.Simulate` docs.)
   - Replay any tick-stamped collision impulses from the server for those ticks (§7) so the re-sim accounts for bounces the owner didn't originally see.
   - This re-sim is *not* bit-identical to the host (different CPU, different float path) but the window is short and the next reconcile absorbs residual error.
4. **Visual smoothing, not physics smoothing:** Do not SmoothDamp the Rigidbody transform — it will fight the solver and feed back into tether spring + ground-check. Instead:
   - Split rendering off the physics GameObject. Put the mesh/skinned-chef model on a **child transform** (`MeatballVisual`).
   - On reconcile with `posError > 0`, compute `visualOffset = predicted.pos - authoritative.pos` **before** snapping the Rigidbody. Store on `MeatballVisual`.
   - Each frame in `LateUpdate`, ease `visualOffset → Vector3.zero` over 2–4 frames (exponential decay or `Vector3.SmoothDamp`). Apply as a local offset on `MeatballVisual.localPosition`.
   - Rigidbody, tether attachment, ground check, camera target all read the **authoritative Rigidbody transform**. Only the mesh lags behind visually for a few frames.
   - This is the Rocket League / Overwatch pattern; the visual layer absorbs correction error so physics never fights itself.
5. If `posError > hugeThreshold` (e.g. 3 m, matching current `snapDistance`): hard snap, clear visual offset and prediction history. Matches current behavior for respawn/teleport.

**Collisions with non-owner meatballs are the hard case** and need explicit treatment — see §7.

### 7. Collision events: broadcast impulses so the owner can replay them

**The likely-noticeable case you flagged.** Meatball-vs-meatball collisions happen often in this game, apply a sharp impulsive bounce (see [MeatballBounceSettings](Assets/Scripts/MeatballBounceSettings.cs): `baseBounceImpulse + velocityScale * approachSpeed`, capped at `maxBounceImpulse`), and are exactly the moments players are watching closely. If the owning client's prediction misses the bounce, the reconcile snap will be large and feel like a teleport.

Mitigation — **explicit collision event broadcast**:

- Extend `MeatballSnapshot` (§5) to carry a `CollisionImpulse[]` sidecar for the tick:
  ```
  struct CollisionImpulse { ulong tick; Vector3 impulse; Vector3 contactPoint; ulong otherClientId; }
  ```
- When the host's `MeatballPhysicsController.OnCollisionEnter` fires, it computes the impulse (existing logic). **Also** store the impulse keyed by tick and attach to the next snapshot broadcast to both colliding players.
- Owner's reconcile (§6, step 3) replays these impulses at the correct tick during rollback: apply `rb.AddForce(impulse, Impulse)` on the matching tick before calling `Physics.Simulate(fixedDt)`. Result: the prediction re-sim reproduces the bounce at the right moment, not as a teleport.
- For the **initial** frame the owner's local prediction contacts an interpolated non-owner ghost, we have two options:
  - **Don't locally predict the bounce at all.** The owner's local meatball passes through the ghost freely for one RTT, then the next reconcile applies the impulse and the meatball gets visibly launched. Feels "late" but never wrong.
  - **Disable owner-ghost collision locally** (layer masks) so the local sim ignores other players entirely, preventing double-resolution. The bounce arrives purely from the broadcast. Recommended.
- Combined with the visual-offset smoothing in §6.4, this should make collisions read as "I got bounced" rather than "I teleported." Not perfect — if RTT is high (>150 ms) the bounce still arrives noticeably late — but it's within the "acceptable chaos" envelope.

Tuning: widen the visual-smoothing window (4–6 frames) *specifically when replaying a collision impulse* so the visual catches up smoothly instead of popping.

### 8. Tether: keep host-authoritative, render from endpoints

**Minimal change to [TetherForce.cs](Assets/Scripts/TetherForce.cs) and [SpaghettiRenderer.cs](Assets/Scripts/SpaghettiRenderer.cs).**

- TetherForce force application stays host-only (already `IsServer`-guarded). **Do not** let the owning client predict the tether spring — per research, re-simulating an underdamped Hooke spring from a snapshot lands on a different oscillation phase and reads as jitter. CLAUDE.md already commits to this: *"The tether force runs on the host only; clients receive meatball positions and render the tether visually from those."*
- Clients already receive pivot positions via `SyncPivotsClientRpc`. Keep.
- `SpaghettiRenderer` already treats its Verlet chain as cosmetic and follows endpoints. Keep.
- **New behavior:** when the owning client snaps the meatball during reconcile (§6 case 5), also reset the visual Verlet chain on the same frame to avoid the rope visibly snapping rubbery. The Verlet nodes keep their velocities by default — zero them on a hard reconcile.

One subtle interaction: the tether force read by the *owning* client during prediction. Two options:
- **Option A (recommended):** Owning client does not apply the tether force during prediction. Only movement input. The host-authoritative tether force pulls the client via reconciliation deltas. This is simpler and avoids the Hooke-divergence trap. The predicted meatball will drift slightly from the host while tether is active, corrected on each snapshot. Acceptable because the tether typically pulls over many frames, so reconciles are small and smooth-blendable.
- **Option B:** Owning client also applies the tether force locally, using the last-received partner position as a stale input. Gives more accurate prediction but introduces divergence and tuning complexity. Not recommended initially — can revisit if Option A feels too floaty.

### 9. Reel: move into the main input pipeline

Reel is a core gameplay input (hold-E to pull partner), not a sparse one-shot. It deserves the same latency treatment as move/jump/sprint.

**Changes to [SpaghettiReelAbility.cs](Assets/Scripts/Luke Scripts/SpaghettiReelAbility.cs):**

- Remove `SetReelingIntentServerRpc`. Delete the per-frame RPC entirely.
- Add `bool reel` to the `InputFrame` struct (§2). The owning client's `MeatballClientInputHandler` reads reel-held state in its existing `IPlayerActions.OnReel` callback (add this action to the `.inputactions` asset if not already present) and includes it in each tick's `InputFrame` alongside move/jump/sprint.
- The redundant input packet (§2) now carries reel state for free — no additional RPC surface.
- Host reads `reel` from the tick-keyed input buffer (§3) each FixedUpdate. If true and reel conditions pass (ground check, partner in range), apply the existing reel forces and gate audio ClientRpcs.
- **Owning client prediction of reel:** do NOT locally apply reel force during prediction. Reel pulls both meatballs toward each other, which requires the non-owner's authoritative position — same Hooke-divergence trap as the tether (§8). Keep reel force host-only. The owner's meatball will feel reel pulls ~1 RTT late, matching tether feel. Consistent with the tether-authority decision.
- `PlayImpactSfxClientRpc` stays as-is (one-shot SFX on collision during reel).

**Why this matters:** before the change, reel was its own special RPC with its own drop semantics. After: reel benefits from redundant input packets (no missed hold-frames on packet loss) and from the decay-by-age rule (smooth handling of dropouts). Unified input = uniform feel.

### 10. Other systems that need tick-awareness

- [MeatballCheckpointReturnManager.cs](Assets/Scripts/MeatballCheckpointReturnManager.cs): respawns are hard-snap events. On reconcile of any ticks that crossed a respawn event, **do not rollback past the respawn** — clear predicted history on respawn and treat that tick as the new prediction baseline. The authoritative snapshot after a respawn carries a "baseline reset" flag; owner's reconcile sees it and drops all earlier predicted state.
- [MMMovement.cs](Assets/Scripts/MMMovement.cs) (platforms): stays host-authoritative interpolated. Owning client's prediction of its own meatball must read **current client-side interpolated platform position** for ground checks and collisions. Minor risk of ghost collisions if a platform teleports; acceptable given platform motion is smooth.
- [MarinaraTrailPainter.cs](Assets/Scripts/Luke Scripts/MarinaraTrailPainter.cs): host-authoritative RPC-broadcasted decal; no change needed.
- [GameManager.cs](Assets/Scripts/GameManager.cs) / connection approval: no change.

### 11. Settings and tuning

- `DynamicsManager.asset` fixed timestep stays at 0.02 (50 Hz).
- Input buffer size: 64 ticks (~1.3s of history).
- Snapshot rate: 30 Hz initially, throttle up/down based on bandwidth testing.
- Small-error smoothing window: 2–4 ticks.
- `posError` thresholds: 0.05 m (smooth) / 3.0 m (hard snap) — match existing `snapDistance`.
- Redundant input frames per packet: 4.
- Missing-input repeat cap: 10 ticks before zeroing move.

### 12. What this does NOT do

- **Does not make the tether feel more responsive for the non-host player.** Tether pulls still feel one RTT delayed. This is a physics-determinism limitation, not fixable without Quantum.
- **Does not make other players' meatballs feel live.** They still arrive via snapshots + interpolation.
- **Does not fix physics disagreement on meatball-vs-meatball collisions.** The host is authoritative; the owning client will see a small reconcile snap when another player bumps them. Smoothing masks it.
- **Does not change cheat-resistance** (irrelevant per user) — still host-authoritative for all authoritative state.

## Files to modify

| File | Change |
|------|--------|
| [MeatballClientInputHandler.cs](Assets/Scripts/MeatballClientInputHandler.cs) | Tagged input frames (inc. `reel`), redundant packets, client input history |
| [MeatballPhysicsController.cs](Assets/Scripts/MeatballPhysicsController.cs) | Delete latency-sim queue; tick-keyed input buffer; extract shared motor; emit collision-impulse events into snapshot |
| [MeatballNetSync.cs](Assets/Scripts/MeatballNetSync.cs) | Tick-stamped snapshots with collision-impulse sidecar; stop forcing kinematic on owner; wire up `MeatballVisual` child for visual-offset smoothing |
| [TetherForce.cs](Assets/Scripts/TetherForce.cs) | Minor: zero Verlet velocities on hard reconcile event |
| [SpaghettiRenderer.cs](Assets/Scripts/SpaghettiRenderer.cs) | Hook for reconcile-triggered reset |
| [SpaghettiReelAbility.cs](Assets/Scripts/Luke Scripts/SpaghettiReelAbility.cs) | Remove `SetReelingIntentServerRpc`; consume `reel` from tick-keyed input buffer |
| [InputSystem_Actions.inputactions](Assets/InputSystem_Actions.inputactions) | Add `Reel` action (E key) if not already present, so it flows through `IPlayerActions` |
| [PlayerMeatball.prefab](Assets/Prefabs/PlayerMeatball.prefab) | Add `MeatballVisual` child transform; reparent mesh/chef under it |

## Files to add

| File | Purpose |
|------|---------|
| `Assets/Scripts/Net/NetworkTick.cs` | Shared tick clock + RTT estimator |
| `Assets/Scripts/Net/MeatballMotor.cs` | Pure-function force application shared by host + predicting client |
| `Assets/Scripts/Net/PredictedMeatball.cs` | Owner-side prediction + reconcile loop |
| `Assets/Scripts/Net/InputFrame.cs` | Struct + serialization for tagged input |
| `Assets/Scripts/Net/MeatballSnapshot.cs` | Struct + batching for authoritative snapshots |

## Rollout order (incremental, each step testable)

1. **Delete the simulated-latency queue** in [MeatballPhysicsController.cs](Assets/Scripts/MeatballPhysicsController.cs). Host applies each `SubmitInputServerRpc` immediately on the next FixedUpdate. Pure simplification — shipping behavior is identical (artificial latency was editor-only). Clears the decks for the new input pipeline.
2. **NetworkTick + tick stamping** — no gameplay change, just infrastructure. Verify host/client tick agreement in logs.
3. **Extract MeatballMotor** — pure refactor, behavior unchanged. Both host and (still-kinematic) client can compile against it.
4. **Tagged input buffer + redundant packets** — owner sends tick-stamped `InputFrame[]` including `reel`; host applies from tick-keyed dictionary. Simultaneously delete `SetReelingIntentServerRpc` and route reel through the new pipeline. Verify no regressions in move/jump/sprint/reel.
5. **Reparent mesh onto `MeatballVisual` child** in the prefab. Zero visual offset — behavior unchanged — but the indirection is now in place for step 7.
6. **Authoritative snapshots with tick stamps + collision-impulse sidecar** — replace per-field NetworkVariables. Non-owner clients interpolate as before; collision impulses are recorded and broadcast but unused until step 8.
7. **Owner prediction (§4)** — flip owner's rigidbody out of kinematic, apply MeatballMotor locally. At this point owner's input feels instant but will drift until step 8 lands.
8. **Reconciliation (§6) + collision replay (§7)** — close the loop: snap Rigidbody, replay inputs and broadcast collision impulses, apply visual offset to `MeatballVisual`. This is the critical step.
9. **Tether reconcile-reset polish (§8)** — zero Verlet velocities on hard snap events to avoid rope jitter.
10. **Tuning pass** — thresholds, snapshot rate, redundancy, visual-smoothing windows (wider for collision replays).

Each step should be landable and shippable on its own. Don't leave the repo mid-migration for long stretches.

## Verification

- **Local multi-instance test:** Use Unity's ParrelSync or separate editor instances to host + join locally. Measure "input to visible movement" latency for the owning client before/after (should drop from ~1 RTT to ~1 frame).
- **Artificial latency:** Reuse existing `MeatballPhysicsController._settings.simulatedLatencyMs`. Crank to 100 ms, 200 ms, 300 ms; verify owner's meatball still feels immediate and reconciles smoothly without jitter.
- **Packet loss simulation:** Use Clumsy or Unity's network conditioner. Drop 5–10% of packets. Verify redundant input packets cover the gap and meatball doesn't stutter.
- **Tether stability:** Pull partner around complex geometry; verify host-authoritative pivot sync still lines up with predicted meatball position after reconcile.
- **Checkpoint respawn:** Trigger respawn during active prediction; verify prediction history clears and no phantom rollback past the respawn.
- **Collision correctness:** Two predicting players (well, only the owner on each client predicts) collide head-on. Confirm host's collision impulse (computed from `OnCollisionEnter`) reaches each client and produces a clean reconcile rather than oscillation.

## Out of scope (for this change)

- Replacing NGO with FishNet or Netick.
- Rewrite on Photon Quantum or Netcode for Entities (only credible paths to *true* determinism, but massive rewrites).
- Any changes to obstacle physics, ramp logic, or level scripting.
- Cheat mitigation (per user instruction).
