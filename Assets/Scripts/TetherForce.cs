using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Collision-aware tether physics between meatballs.
///
/// Maintains a list of WRAP PIVOTS per (this, other) meatball pair — world-space points where
/// the rope catches on a collider edge. On every FixedUpdate:
///
///   1. UNWIND: for each current pivot, SphereCast between its two neighbors in the path.
///      If that cast is clear, the pivot is no longer holding the rope around anything → remove.
///   2. WRAP: for each segment of the current path, SphereCast start → end. On first hit,
///      insert a new pivot just outside the contact point (offset along the hit normal).
///      The segment is then re-examined so deep geometry can add multiple pivots in one step.
///   3. FORCE (host only): the total wrapped-path length drives a Hooke's-law spring plus
///      a damping term proportional to the rate-of-change of that path length. The force is
///      applied to THIS meatball directed along the FIRST segment of the path — i.e. toward
///      the nearest pivot, or toward the other meatball if the path has no pivots.
///
/// The "pull toward the nearest pivot, not toward the partner" behavior is the central design
/// goal: a meatball dangling below a ledge is pulled up toward the ledge edge, not diagonally
/// toward its partner through the ledge itself.
///
/// Pivot maintenance and force application both run on the host only. The host syncs resolved
/// world-space pivot positions to clients via a ClientRpc each FixedUpdate so that
/// SpaghettiRenderer (which calls <see cref="CopyPathTo"/>) sees a consistent wrapped path
/// on all machines without running the geometry-dependent algorithm independently — which
/// would diverge due to differences in interpolation and timing.
///
/// LIMITATIONS:
/// - Pivots are bound to the Transform of the collider they caught on (local-space point +
///   local-space normal), so moving / rotating obstacles carry their pivots correctly. If the
///   obstacle is destroyed the pivot self-removes on the next tick and wrap re-acquires.
///   Note: the world position is resolved from the Transform once per FixedUpdate; Rigidbody
///   obstacles that interpolate in Update will still read the last FixedUpdate pose, which is
///   correct for physics and fine for visuals.
/// - The unwind/wrap pair is a heuristic, not a true geodesic. It handles common shapes
///   (ledges, posts, convex corners) well and degrades to flicker rather than explosion in
///   pathological concave pockets. A hard cap on pivot count prevents runaway growth.
/// - Client-side pivot positions arrive one network tick behind the host's simulation. For a
///   silly co-op game this latency is acceptable and consistent with the overall
///   host-authoritative model.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(MeatballPhysicsController))]
public class TetherForce : NetworkBehaviour
{
    /// <summary>
    /// All active TetherForce instances (host + every client). Mirrors the
    /// <see cref="SpaghettiRenderer.Instances"/> pattern so client-side code can enumerate
    /// peers without needing host authority. <see cref="MeatballPhysicsController.ServerInstances"/>
    /// is host-only and therefore unsuitable for client-side rendering support.
    /// </summary>
    public static readonly List<TetherForce> Instances = new();

    [SerializeField] private MeatballMovementSettings _settings;

    [Header("Wrap Detection")]
    [Tooltip("Layers the tether can catch and wrap around. Configure this to your static obstacle/geometry " +
             "layers. IMPORTANT: do NOT include the meatball's own layer — the SphereCast starts at each " +
             "meatball's position and including its own layer would cause constant self-hits.")]
    [SerializeField] private LayerMask _wrapMask = Physics.DefaultRaycastLayers;

    [Tooltip("Effective radius of the noodle for wrap-detection SphereCasts. Should roughly match the " +
             "visual thickness of the rendered noodle — smaller values let the tether slip through thinner " +
             "gaps before catching on a surface.")]
    [SerializeField, Min(0.001f)] private float _noodleRadius = 0.05f;

    [Tooltip("Extra distance to push each new pivot outward along the contact surface normal, on top of " +
             "the noodle radius. Keeps a freshly inserted pivot clearly outside the collider so the very " +
             "next SphereCast does not immediately re-hit the same surface and insert a duplicate pivot.")]
    [SerializeField, Min(0f)] private float _pivotNormalOffset = 0.04f;

    [Tooltip("Safety cap on pivots retained per meatball pair. Prevents runaway allocation in pathological " +
             "geometry (e.g. a meatball squeezed into a concave pocket that keeps generating new contacts).")]
    [SerializeField, Min(1)] private int _maxPivotsPerPair = 12;

    [Tooltip("Maximum number of new pivots inserted per FixedUpdate tick, per pair. Spreads wrap resolution " +
             "across frames so a rope snapping suddenly taut against complex geometry cannot cause a long " +
             "single-frame stall.")]
    [SerializeField, Min(1)] private int _maxWrapsPerStep = 4;

    [Tooltip("Small offset along the segment direction used when starting each SphereCast. Moves the cast " +
             "origin slightly away from the meatball center so Unity's rule that a SphereCast ignores " +
             "colliders already overlapping the starting sphere does not swallow legitimate contacts " +
             "immediately in front of the meatball.")]
    [SerializeField, Min(0f)] private float _castStartOffset = 0.02f;

    // ── Per-other-meatball state ────────────────────────────────────────────

    /// <summary>
    /// A single wrap pivot. Bound to the Transform of the collider that was hit so the pivot
    /// follows moving / rotating obstacles. Point and normal are stored in that Transform's
    /// local space; <see cref="ResolveWorld"/> reconstructs the world-space pivot position.
    ///
    /// The surface offset (_noodleRadius + _pivotNormalOffset at capture time) is baked into
    /// the stored local point by applying it along the local-space normal at capture — that
    /// way a rotated obstacle continues to hold the rope at a consistent distance from the
    /// surface without re-reading the offset settings each frame.
    /// </summary>
    private struct Pivot
    {
        public Transform ContactTransform; // collider's transform at capture; null if destroyed
        public Vector3   LocalPoint;       // surface contact point in local space, already offset along localNormal by (noodleRadius + pivotNormalOffset)
        public Vector3   LocalNormal;      // contact normal in local space (for gizmos / debug only)
        public Vector3   CachedWorld;      // last resolved world position (used if ContactTransform is destroyed before removal)

        public bool IsAlive => ContactTransform != null;

        public Vector3 ResolveWorld()
        {
            if (ContactTransform == null) return CachedWorld;
            CachedWorld = ContactTransform.TransformPoint(LocalPoint);
            return CachedWorld;
        }
    }

    // Pivots between this meatball and each other meatball, keyed by the other's TetherForce.
    // Contains ONLY intermediate pivots; the meatball endpoints are implicit and appended at
    // force-application / CopyPathTo time. Populated on the server only.
    private readonly Dictionary<TetherForce, List<Pivot>> _pivotsByOther = new();

    // Client-side storage for synced pivot world positions, received from the host via ClientRpc.
    // Keyed by the other TetherForce. Lists are reused across ticks to avoid allocation.
    private readonly Dictionary<TetherForce, List<Vector3>> _clientPivotPositions = new();

    // Previous tick's total wrapped-path length per partner, used for rate-of-change damping.
    private readonly Dictionary<TetherForce, float> _prevPathLenByOther = new();

    // Reusable anchor buffer (self → pivots → other). Avoids per-frame GC inside ApplyTetherForces.
    private readonly List<Vector3> _pathBuf = new(8);

    private Rigidbody _rb;
    private MeatballPhysicsController _controller;

    /// <summary>Same rigidbody used for movement — exposed so SpaghettiRenderer can grab live position.</summary>
    public Rigidbody Rigidbody => _rb;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _controller = GetComponent<MeatballPhysicsController>();
    }

    private void OnEnable() => Instances.Add(this);

    private void OnDisable()
    {
        Instances.Remove(this);
        _pivotsByOther.Clear();
        _clientPivotPositions.Clear();
        _prevPathLenByOther.Clear();
    }

    private bool ShouldSimulate => _controller != null && _controller.ShouldSimulate;

    private void FixedUpdate()
    {
        if (!ShouldSimulate) return;

        UpdateAllPivots();
        if (IsServer) SyncPivotsToClients();
        ApplyTetherForces();
    }

    // ── Pivot maintenance ───────────────────────────────────────────────────

    /// <summary>Refreshes the pivot list for every (this, other) pair this instance knows about.</summary>
    private void UpdateAllPivots()
    {
        if (_rb == null) return;

        for (int i = 0; i < Instances.Count; i++)
        {
            TetherForce other = Instances[i];
            if (other == this || other == null || other._rb == null) continue;

            if (!_pivotsByOther.TryGetValue(other, out var pivots))
            {
                pivots = new List<Pivot>(4);
                _pivotsByOther[other] = pivots;
            }

            UpdatePivotList(pivots, _rb.position, other._rb.position);
        }
    }

    /// <summary>
    /// Refreshes the world-space positions for every pivot in <paramref name="pivots"/> and
    /// drops any whose contact Transform has been destroyed. Call this at the start of a tick
    /// before running any segment cast — otherwise casts would use stale positions from
    /// obstacles that have already moved this physics step.
    /// </summary>
    private void ResolvePivotsAndPrune(List<Pivot> pivots)
    {
        for (int i = pivots.Count - 1; i >= 0; i--)
        {
            Pivot p = pivots[i];
            if (!p.IsAlive) { pivots.RemoveAt(i); continue; }
            p.ResolveWorld();
            pivots[i] = p;
        }
    }

    /// <summary>Returns the world position of <paramref name="index"/>-th pivot (already resolved this tick).</summary>
    private static Vector3 GetWorld(List<Pivot> pivots, int index) => pivots[index].CachedWorld;

    /// <summary>
    /// One pass of unwind + wrap on the supplied pivot list for a single pair.
    /// See the class-level doc-comment for the high-level algorithm description.
    /// </summary>
    private void UpdatePivotList(List<Pivot> pivots, Vector3 selfPos, Vector3 otherPos)
    {
        // Refresh cached world positions from live Transforms AND drop pivots whose obstacle
        // was destroyed. Every step below reads CachedWorld, so this must come first.
        ResolvePivotsAndPrune(pivots);

        // ── UNWIND ──
        // Iterate until no more removals happen in a single pass; removing one pivot can expose
        // its neighbors as also unwindable (e.g. three collinear pivots on a slope that
        // straightens all at once). The loop is bounded because each iteration strictly shrinks
        // pivots.Count.

        // Adam: check each pair of pivots and remove any where the sphere cast to the next one doesn't hit
        bool removedAny;
        do
        {
            removedAny = false;
            for (int i = 0; i < pivots.Count; i++)
            {
                Vector3 prev = (i == 0)                ? selfPos  : GetWorld(pivots, i - 1);
                Vector3 next = (i == pivots.Count - 1) ? otherPos : GetWorld(pivots, i + 1);

                Vector3 toNext = next - prev;
                float   segLen = toNext.magnitude;
                if (segLen < 1e-5f)
                {
                    // Neighbors have collapsed onto each other — pivot is degenerate.
                    pivots.RemoveAt(i);
                    removedAny = true;
                    break;
                }

                Vector3 dir = toNext / segLen;

                // Use the SAME SphereCast shape that the wrap pass uses. This matters:
                // if the unwind test used a zero-radius ray, it might report "clear" on a
                // contact that the (radius = _noodleRadius) wrap pass would immediately re-hit,
                // producing add/remove/add/... flicker on the same frame boundary.
                if (!SphereCastHits(prev, dir, segLen))
                {
                    pivots.RemoveAt(i);
                    removedAny = true;
                    break;
                }
            }
        } while (removedAny);

        // ── WRAP ──
        // Walk segments using a manually advanced index so we can re-examine a segment after
        // inserting a new pivot inside it (the new pivot may itself sit near more geometry
        // that needs another wrap immediately).
        int added = 0;
        int segIndex = 0;
        while (segIndex <= pivots.Count &&
               added < _maxWrapsPerStep &&
               pivots.Count < _maxPivotsPerPair)
        {
            Vector3 from = (segIndex == 0)            ? selfPos  : GetWorld(pivots, segIndex - 1);
            Vector3 to   = (segIndex == pivots.Count) ? otherPos : GetWorld(pivots, segIndex);

            Vector3 seg = to - from;
            float   d   = seg.magnitude;
            if (d < 1e-4f) { segIndex++; continue; }
            Vector3 dir = seg / d;

            if (TrySphereCastFirstHit(from, dir, d, out RaycastHit hit))
            {
                // Pivot sits just outside the surface along the contact normal. The combined
                // offset (_noodleRadius + _pivotNormalOffset) has to be enough that the very
                // next SphereCast from a neighbor back toward this point does not re-hit the
                // same surface at zero depth — otherwise we'd duplicate the pivot.
                Vector3 worldPivot = hit.point + hit.normal * (_noodleRadius + _pivotNormalOffset);

                // Guard against inserting a pivot coincident with either anchor — would create
                // a zero-length segment that confuses the next pass.
                if ((worldPivot - from).sqrMagnitude < 1e-6f ||
                    (worldPivot - to  ).sqrMagnitude < 1e-6f)
                {
                    segIndex++;
                    continue;
                }

                // Bind the pivot to the hit collider's Transform by capturing the world position
                // in that Transform's local space. If the collider later translates or rotates,
                // ResolveWorld reconstructs the correct world position each tick.
                Transform contact = hit.collider != null ? hit.collider.transform : null;
                Pivot pivot = new Pivot
                {
                    ContactTransform = contact,
                    LocalPoint       = contact != null ? contact.InverseTransformPoint(worldPivot)   : worldPivot,
                    LocalNormal      = contact != null ? contact.InverseTransformDirection(hit.normal) : hit.normal,
                    CachedWorld      = worldPivot,
                };

                pivots.Insert(segIndex, pivot);
                added++;

                // Deliberately do NOT increment segIndex: re-check this segment (now
                // `from` → newPivot) because deeper geometry may need another wrap here.
                continue;
            }

            segIndex++;
        }
    }

    /// <summary>True iff a SphereCast (origin offset forward by _castStartOffset) hits something in the wrap mask.</summary>
    private bool SphereCastHits(Vector3 origin, Vector3 dir, float distance)
    {
        float offset = Mathf.Min(_castStartOffset, Mathf.Max(0f, distance - 1e-4f));
        return Physics.SphereCast(
            origin + dir * offset,
            _noodleRadius,
            dir,
            out _,
            Mathf.Max(0f, distance - offset),
            _wrapMask,
            QueryTriggerInteraction.Ignore);
    }

    /// <summary>SphereCast variant that surfaces the first-hit RaycastHit. Same origin offset rule as <see cref="SphereCastHits"/>.</summary>
    private bool TrySphereCastFirstHit(Vector3 origin, Vector3 dir, float distance, out RaycastHit hit)
    {
        float offset = Mathf.Min(_castStartOffset, Mathf.Max(0f, distance - 1e-4f));
        return Physics.SphereCast(
            origin + dir * offset,
            _noodleRadius,
            dir,
            out hit,
            Mathf.Max(0f, distance - offset),
            _wrapMask,
            QueryTriggerInteraction.Ignore);
    }

    // ── Pivot sync (host → clients) ───────────────────────────────────────

    /// <summary>
    /// Sends this instance's resolved pivot world positions to all clients via a single
    /// batched ClientRpc. Called every FixedUpdate on the server, after pivots have been
    /// updated and their CachedWorld values are fresh.
    /// </summary>
    private void SyncPivotsToClients()
    {
        // Build flattened arrays: one entry in otherIds/counts per pair, all pivot
        // positions concatenated into a single positions array.
        int pairCount = 0;
        int totalPivots = 0;
        foreach (var kvp in _pivotsByOther)
        {
            if (kvp.Key == null) continue;
            pairCount++;
            totalPivots += kvp.Value.Count;
        }

        var otherIds = new ulong[pairCount];
        var counts   = new int[pairCount];
        var positions = new Vector3[totalPivots];

        int pair = 0;
        int pos  = 0;
        foreach (var kvp in _pivotsByOther)
        {
            TetherForce other = kvp.Key;
            if (other == null) continue;
            List<Pivot> pivots = kvp.Value;

            otherIds[pair] = other.NetworkObjectId;
            counts[pair]   = pivots.Count;
            for (int i = 0; i < pivots.Count; i++)
                positions[pos++] = pivots[i].CachedWorld;
            pair++;
        }

        SyncPivotsClientRpc(otherIds, positions, counts);
    }

    [ClientRpc]
    private void SyncPivotsClientRpc(ulong[] otherIds, Vector3[] allPositions, int[] countsPerPair)
    {
        if (IsServer) return; // host already has authoritative data

        // Clear all existing lists (reuse the List objects to avoid allocation).
        foreach (var kvp in _clientPivotPositions)
            kvp.Value.Clear();

        int offset = 0;
        for (int i = 0; i < otherIds.Length; i++)
        {
            int count = countsPerPair[i];

            if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(otherIds[i], out var netObj))
            {
                var other = netObj.GetComponent<TetherForce>();
                if (other != null)
                {
                    if (!_clientPivotPositions.TryGetValue(other, out var list))
                    {
                        list = new List<Vector3>(4);
                        _clientPivotPositions[other] = list;
                    }
                    for (int j = 0; j < count; j++)
                        list.Add(allPositions[offset + j]);
                }
            }

            offset += count;
        }
    }

    // ── Force application (host only) ───────────────────────────────────────

    /// <summary>
    /// For every other meatball, compute the wrapped-path length and apply a Hooke-plus-damping
    /// force along the FIRST segment of the path (toward the nearest pivot, or the partner if
    /// no pivot exists). Matches the behavior of the pre-wrap TetherForce implementation when
    /// the path has no pivots, so un-wrapped behavior is unchanged.
    /// </summary>
    private void ApplyTetherForces()
    {
        for (int i = 0; i < Instances.Count; i++)
        {
            TetherForce other = Instances[i];
            if (other == this || other == null || other._rb == null) continue;

            if (!_pivotsByOther.TryGetValue(other, out var pivots)) continue;

            // Build the full anchor path: [self, ...pivots, other]. Pivots were already
            // world-resolved by UpdatePivotList earlier this tick, so CachedWorld is fresh.
            _pathBuf.Clear();
            _pathBuf.Add(_rb.position);
            for (int k = 0; k < pivots.Count; k++) _pathBuf.Add(pivots[k].CachedWorld);
            _pathBuf.Add(other._rb.position);

            // Wrapped-path length is what drives stretch — NOT the straight-line meatball distance.
            // This is the correction that makes a dangling meatball feel pulled toward its
            // wrapping pivot rather than toward its partner through the obstacle.
            float totalLen = 0f;
            for (int k = 0; k < _pathBuf.Count - 1; k++)
                totalLen += Vector3.Distance(_pathBuf[k], _pathBuf[k + 1]);

            float stretch = totalLen - _settings.noodleLength;

            // Always update last-length so the damping finite-difference remains well-defined
            // on the next tick, even when the rope is currently slack.
            bool hadLast = _prevPathLenByOther.TryGetValue(other, out float lastLen);
            _prevPathLenByOther[other] = totalLen;

            if (stretch <= 0f) continue;

            // Pull along the first segment of the path. For an un-wrapped tether this is
            // directly toward the partner; for a wrapped tether this is toward the first pivot.
            Vector3 firstNext = _pathBuf[1];
            Vector3 delta     = firstNext - _rb.position;
            float   dist      = delta.magnitude;
            if (dist < 1e-5f) continue;
            Vector3 dir = delta / dist;

            // Hooke's law on total wrapped stretch.
            float spring = _settings.tetherSpringK * stretch;

            // Damping based on the rate-of-change of total path length. Positive when meatballs
            // are separating (through the wrap geometry) — reinforces the pull to resist the
            // motion. Negative when meatballs are closing — reduces pull to avoid overshoot.
            float pathRate = 0f;
            if (hadLast && Time.fixedDeltaTime > 0f)
                pathRate = (totalLen - lastLen) / Time.fixedDeltaTime;
            float damping = _settings.tetherDamping * pathRate;

            _rb.AddForce(dir * (spring + damping), ForceMode.Force);
        }
    }

    // ── Public API for SpaghettiRenderer ────────────────────────────────────

    /// <summary>
    /// Copies the full anchor path from this meatball to <paramref name="other"/> into
    /// <paramref name="outPath"/>, clearing it first. The output is
    /// <c>[this.position, ...pivots..., other.position]</c> and always has at least 2 entries.
    ///
    /// Safe to call on host or client. On the host, pivots are resolved fresh from their
    /// contact Transforms (interpolated visual pose). On clients, pivots come from the latest
    /// synced world positions received via ClientRpc. If no pivot record exists yet (first tick
    /// for this pair) the path contains only the two endpoints — rendering as a straight line.
    /// </summary>
    public void CopyPathTo(TetherForce other, List<Vector3> outPath)
    {
        outPath.Clear();
        outPath.Add(_rb != null ? _rb.position : transform.position);

        if (ShouldSimulate)
        {
            if (_pivotsByOther.TryGetValue(other, out var pivots))
            {
                // Renderer runs in LateUpdate — resolve pivot world positions fresh here so the
                // rope follows the obstacle's interpolated visual pose within a frame, rather
                // than using the FixedUpdate-stale CachedWorld.
                for (int k = 0; k < pivots.Count; k++)
                {
                    Pivot p = pivots[k];
                    outPath.Add(p.IsAlive ? p.ContactTransform.TransformPoint(p.LocalPoint) : p.CachedWorld);
                }
            }
        }
        else if (_clientPivotPositions.TryGetValue(other, out var positions))
        {
            for (int k = 0; k < positions.Count; k++)
                outPath.Add(positions[k]);
        }

        outPath.Add(other._rb != null ? other._rb.position : other.transform.position);
    }

    // ── Public API for SpaghettiReelAbility ─────────────────────────────────

    /// <summary>
    /// Returns the first waypoint in this meatball's path toward <paramref name="other"/>:
    /// the nearest wrap pivot if the tether has caught on geometry, otherwise the partner's
    /// position. Use this to direct reel forces toward the tether's contact point rather than
    /// straight at the partner through geometry.
    /// </summary>
    public Vector3 GetFirstPathTarget(TetherForce other)
    {
        if (_pivotsByOther.TryGetValue(other, out var pivots) && pivots.Count > 0)
        {
            Pivot p = pivots[0];
            return p.IsAlive ? p.ContactTransform.TransformPoint(p.LocalPoint) : p.CachedWorld;
        }

        return other._rb != null ? other._rb.position : other.transform.position;
    }

    /// <summary>
    /// Like <see cref="GetFirstPathTarget"/> but shifts the returned position away from the
    /// contact surface by <paramref name="normalOffset"/> units along the outward contact normal.
    /// When no pivot exists the partner's position is returned unchanged.
    /// </summary>
    public Vector3 GetFirstPathTargetWithNormalOffset(TetherForce other, float normalOffset)
    {
        if (_pivotsByOther.TryGetValue(other, out var pivots) && pivots.Count > 0)
        {
            Pivot p = pivots[0];
            Vector3 worldPoint  = p.IsAlive ? p.ContactTransform.TransformPoint(p.LocalPoint)     : p.CachedWorld;
            Vector3 worldNormal = p.IsAlive ? p.ContactTransform.TransformDirection(p.LocalNormal) : p.LocalNormal;
            return worldPoint + worldNormal * normalOffset;
        }

        return other._rb != null ? other._rb.position : other.transform.position;
    }

    // ── Gizmos ──────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        if (_settings == null) return;

        // Draw each pair exactly once, owned by the lower-indexed partner in Instances.
        int myIndex = Instances.IndexOf(this);
        if (myIndex < 0) return;

        for (int i = myIndex + 1; i < Instances.Count; i++)
        {
            TetherForce other = Instances[i];
            if (other == null) continue;
            if (!_pivotsByOther.TryGetValue(other, out var pivots)) continue;

            // Measure total path length so we can color the rope based on whether it's taut.
            Vector3 prev     = transform.position;
            float   totalLen = 0f;
            for (int k = 0; k < pivots.Count; k++)
            {
                Vector3 w = pivots[k].CachedWorld;
                totalLen += Vector3.Distance(prev, w);
                prev = w;
            }
            totalLen += Vector3.Distance(prev, other.transform.position);

            Gizmos.color = totalLen > _settings.noodleLength ? Color.red : Color.cyan;

            prev = transform.position;
            for (int k = 0; k < pivots.Count; k++)
            {
                Pivot   p = pivots[k];
                Vector3 w = p.CachedWorld;
                Gizmos.DrawLine(prev, w);
                Gizmos.DrawWireSphere(w, _noodleRadius * 2f);

                // Draw the contact normal as a yellow ray.
                Vector3 worldNormal = p.IsAlive
                    ? p.ContactTransform.TransformDirection(p.LocalNormal)
                    : p.LocalNormal;
                Gizmos.color = Color.yellow;
                Gizmos.DrawRay(w, worldNormal * 0.5f);
                Gizmos.color = totalLen > _settings.noodleLength ? Color.red : Color.cyan;

                prev = w;
            }
            Gizmos.DrawLine(prev, other.transform.position);
        }
    }
}
