using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cosmetic Verlet-chain spaghetti renderer that follows the collision-aware wrapped path
/// produced by <see cref="TetherForce"/>.
///
/// The chain is a flat array of positions laid out as:
///     [anchor0, interior..., anchor1, interior..., anchor2, ..., anchorK-1]
/// where every "anchor" is pinned each step (to a meatball position or a wrap pivot), and
/// the interior nodes float under Verlet integration between them. A pivot is just another
/// pinned node from the chain's point of view — the chain naturally bends around it.
///
/// Only the lower-indexed <see cref="Instances"/> entry in each pair owns and simulates the
/// chain, which guarantees exactly one chain per pair regardless of meatball count.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class SpaghettiRenderer : MonoBehaviour
{
    /// <summary>All active SpaghettiRenderer instances. Populated via OnEnable/OnDisable.</summary>
    public static readonly List<SpaghettiRenderer> Instances = new();

    [Header("Chain Shape")]
    [Tooltip("Total interior Verlet nodes across the whole rope (not counting pinned anchors). " +
             "These are distributed across segments between pivots — at least one per segment.")]
    [SerializeField, Min(1)] private int _nodeCount = 4;

    [Tooltip("Full rest length of the tether. Match this to noodleLength in MeatballMovementSettings so " +
             "the chain sags when the wrapped path is short and goes taut when the wrapped path exceeds it.")]
    [SerializeField] private float _tetherRestLength = 5f;

    [Header("Simulation")]
    [Tooltip("Fraction of world gravity applied to interior nodes each step. Lower = lighter, less droopy.")]
    [SerializeField] private float _gravityScale = 0.4f;

    [Tooltip("Velocity retention per frame (0–1). Lower = more drag; chain settles faster. " +
             "Values above ~0.99 produce a very springy, slow-settling noodle.")]
    [SerializeField, Range(0f, 1f)] private float _damping = 0.97f;

    [Tooltip("Length-constraint relaxation passes per frame. More iterations = stiffer, less stretchy chain.")]
    [SerializeField, Min(1)] private int _constraintIterations = 3;

    [Tooltip("Layers the ground raycast checks against. Prevents interior nodes from sinking below the floor.")]
    [SerializeField] private LayerMask _groundMask = Physics.DefaultRaycastLayers;

    [Tooltip("Radius of the noodle. Interior nodes are kept this far above the ground surface.")]
    [SerializeField, Min(0f)] private float _noodleThickness = 0.05f;

    [Header("Line Renderer")]
    [Tooltip("Catmull-Rom subdivisions between each pair of Verlet nodes. Higher = smoother curve, more verts.")]
    [SerializeField, Min(1)] private int _smoothingSteps = 8;

    [Header("Gizmos")]
    [SerializeField] private float _nodeGizmoRadius = 0.07f;
    [SerializeField] private Color _nodeColor       = new Color(0.95f, 0.6f, 0.2f, 1.00f);
    [SerializeField] private Color _anchorColor     = new Color(1.00f, 0.3f, 0.1f, 1.00f);
    [SerializeField] private Color _lineColor       = new Color(0.95f, 0.6f, 0.2f, 0.55f);

    // ── Per-target chain data ───────────────────────────────────────────────
    private struct Chain
    {
        public Vector3[] current;         // positions this frame
        public Vector3[] previous;        // positions last frame (encodes velocity implicitly)
        public int[]     anchorIndices;   // indices in current[] that are pinned each step
                                          // — always sorted ascending, starts at 0, ends at current.Length-1
    }

    private readonly Dictionary<SpaghettiRenderer, Chain> _chains = new();

    // One LineRenderer per owned partner. The first owned chain uses `_lineRenderer`
    // (the component on this GameObject); additional chains use child LRs cloned from it.
    private readonly Dictionary<SpaghettiRenderer, LineRenderer> _partnerLines = new();

    // Reusable buffers to avoid per-frame GC.
    private readonly List<Vector3> _anchorBuf     = new(8);
    private          int[]         _interiorBuf   = new int[0];
    private readonly List<Vector3> _smoothedPoints = new();

    private LineRenderer _lineRenderer;
    private TetherForce  _tether; // cached reference — same GameObject as the meatball

    // When true, LateUpdate skips simulation entirely. Used by checkpoint recall to suspend
    // the chain while meatballs teleport, so the rebuild lerps interior nodes between the new
    // anchor positions instead of inheriting stale positions from the old location.
    private bool _rebuildSuppressed;

    /// <summary>Exposes this meatball's TetherForce so its partner's renderer can query the wrapped path.</summary>
    public TetherForce Tether => _tether;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    private void Awake()
    {
        _lineRenderer = GetComponent<LineRenderer>();
        _tether       = GetComponent<TetherForce>();
    }

    private void OnEnable() => Instances.Add(this);

    private void OnDisable()
    {
        Instances.Remove(this);
        _chains.Clear();
        if (_lineRenderer != null) _lineRenderer.positionCount = 0;
        foreach (var kv in _partnerLines)
            if (kv.Value != null && kv.Value != _lineRenderer) Destroy(kv.Value.gameObject);
        _partnerLines.Clear();
    }

    /// <summary>
    /// Zeros implicit velocity on every owned chain's interior nodes by setting
    /// <c>previous = current</c>. Call this when an authoritative reconcile hard-snaps
    /// the owning meatball — without it, the Verlet integrator interprets the teleport
    /// as a large velocity impulse and the noodle whips violently for a few frames.
    ///
    /// Safe to call on any instance; if this renderer doesn't own a chain for a pair,
    /// the matching renderer on the partner will own it and its own reset (triggered by
    /// the same event) takes care of it.
    /// </summary>
    public void ResetChainVelocities()
    {
        // Chain is a struct but its `current`/`previous` fields are arrays — writing into
        // those arrays via a local copy of the struct still mutates the shared array, so
        // no write-back to the dictionary is needed.
        foreach (var kv in _chains)
        {
            Chain chain = kv.Value;
            if (chain.current == null || chain.previous == null) continue;
            for (int i = 0; i < chain.current.Length; i++)
                chain.previous[i] = chain.current[i];
        }
    }

    /// <summary>
    /// Discards every chain this renderer owns and suspends simulation until
    /// <see cref="EndChainRebuild"/> is called. The next LateUpdate after the suspension lifts
    /// will rebuild each chain from scratch with interior nodes lerped between the current
    /// (post-teleport) anchor positions — eliminating the violent whip that would otherwise
    /// happen when interior nodes left over from the pre-teleport location stretch toward the
    /// new anchors.
    /// </summary>
    public void BeginChainRebuild()
    {
        _rebuildSuppressed = true;
        _chains.Clear();

        if (_lineRenderer != null) _lineRenderer.positionCount = 0;
        foreach (var kv in _partnerLines)
            if (kv.Value != null) kv.Value.positionCount = 0;
    }

    /// <summary>Lifts the suspension started by <see cref="BeginChainRebuild"/>; the next
    /// LateUpdate sees no cached chain and rebuilds fresh from current anchor positions.</summary>
    public void EndChainRebuild() => _rebuildSuppressed = false;

    // ── Simulation ──────────────────────────────────────────────────────────

    private void LateUpdate()
    {
        if (_rebuildSuppressed) return;

        // Only simulate pairs where this instance has a lower index than its partner.
        // Guarantees exactly one chain per pair with no coordination between renderers.
        int myIndex = Instances.IndexOf(this);

        // Mark which partner LRs are still in use this frame; release the rest at the end.
        foreach (var kv in _partnerLines)
            if (kv.Value != null) kv.Value.enabled = false;

        for (int i = myIndex + 1; i < Instances.Count; i++)
        {
            SpaghettiRenderer target = Instances[i];
            StepChain(target);

            if (!_chains.TryGetValue(target, out Chain chain)) continue;

            LineRenderer lr = GetOrCreateLineFor(target);
            if (lr != null)
            {
                lr.enabled = true;
                UpdateLineRenderer(lr, in chain);
            }
        }

        // Any partner LR not touched this frame (partner despawned) gets cleared.
        foreach (var kv in _partnerLines)
            if (kv.Value != null && !kv.Value.enabled) kv.Value.positionCount = 0;

        // If this instance owns no chains, keep its primary LineRenderer empty.
        if (_lineRenderer != null && !_partnerLines.ContainsValue(_lineRenderer))
            _lineRenderer.positionCount = 0;
    }

    /// <summary>
    /// Returns a LineRenderer dedicated to the chain between this renderer and <paramref name="target"/>.
    /// The first owned partner reuses the primary LineRenderer on this GameObject; subsequent partners
    /// get a child GameObject with a LineRenderer cloned from the primary so materials/widths match.
    /// </summary>
    private LineRenderer GetOrCreateLineFor(SpaghettiRenderer target)
    {
        if (_partnerLines.TryGetValue(target, out LineRenderer existing) && existing != null)
            return existing;

        LineRenderer lr;
        if (!_partnerLines.ContainsValue(_lineRenderer) && _lineRenderer != null)
        {
            lr = _lineRenderer;
        }
        else
        {
            var go = new GameObject("SpaghettiLine");
            go.transform.SetParent(transform, worldPositionStays: false);
            lr = go.AddComponent<LineRenderer>();
            if (_lineRenderer != null)
            {
                lr.sharedMaterial    = _lineRenderer.sharedMaterial;
                lr.startWidth        = _lineRenderer.startWidth;
                lr.endWidth          = _lineRenderer.endWidth;
                lr.widthCurve        = _lineRenderer.widthCurve;
                lr.colorGradient     = _lineRenderer.colorGradient;
                lr.numCapVertices    = _lineRenderer.numCapVertices;
                lr.numCornerVertices = _lineRenderer.numCornerVertices;
                lr.useWorldSpace     = _lineRenderer.useWorldSpace;
                lr.textureMode       = _lineRenderer.textureMode;
                lr.alignment         = _lineRenderer.alignment;
                lr.shadowCastingMode = _lineRenderer.shadowCastingMode;
                lr.receiveShadows    = _lineRenderer.receiveShadows;
            }
        }

        _partnerLines[target] = lr;
        return lr;
    }

    /// <summary>
    /// Runs one physics step of the Verlet chain between this and <paramref name="target"/>,
    /// using the wrapped anchor path from TetherForce (or straight-line if tether refs are missing).
    /// </summary>
    private void StepChain(SpaghettiRenderer target)
    {
        // ── Fetch wrapped path ──
        FetchAnchors(target, _anchorBuf);
        int anchorCount = _anchorBuf.Count;
        int segments    = anchorCount - 1;
        if (segments < 1) return; // nothing to simulate

        // ── Distribute interior nodes across segments ──
        // Uniform distribution (with leading-segment remainder) gives stable per-segment counts
        // for a given segment count. That stability matters: if the per-segment count wobbled
        // frame-to-frame, anchor indices would shift and we'd need to rebuild the chain every
        // frame — discarding the Verlet state and losing all visual "swing."
        if (_interiorBuf.Length != segments) _interiorBuf = new int[segments];
        DistributeInterior(segments, _interiorBuf);

        int totalNodes = anchorCount;
        for (int s = 0; s < segments; s++) totalNodes += _interiorBuf[s];

        // ── Rebuild if segment structure changed (pivot added/removed) ──
        if (!_chains.TryGetValue(target, out Chain chain) ||
            chain.current.Length        != totalNodes    ||
            chain.anchorIndices.Length  != anchorCount)
        {
            chain = BuildChain(_anchorBuf, _interiorBuf);
        }

        float dt          = Time.deltaTime;
        float gravityStep = Physics.gravity.y * _gravityScale * dt * dt;

        // Every chain segment gets the same rest length so node density is uniform along the
        // whole rope — regardless of where pivots carve it up.
        float perSegRest = _tetherRestLength / (totalNodes - 1);

        // ── Step 1: Verlet-integrate non-anchor nodes ──
        // velocity ≈ (current - previous); scale by damping for drag; add gravity impulse.
        int nextAnchorCursor = 0;
        for (int i = 0; i < totalNodes; i++)
        {
            // Advance the cursor in lockstep with i. When i matches the next anchor, skip.
            if (nextAnchorCursor < chain.anchorIndices.Length &&
                i == chain.anchorIndices[nextAnchorCursor])
            {
                nextAnchorCursor++;
                continue;
            }

            Vector3 vel = (chain.current[i] - chain.previous[i]) * _damping;
            chain.previous[i]  = chain.current[i];
            chain.current[i]  += vel + new Vector3(0f, gravityStep, 0f);
        }

        // ── Step 2: Pin every anchor (endpoints AND pivots) ──
        // Updating `previous` to match `current` BEFORE overwriting with the anchor position
        // zeroes implicit anchor velocity, preventing pin jitter from bleeding into neighbors
        // through the damping term.
        for (int s = 0; s < anchorCount; s++)
        {
            int idx = chain.anchorIndices[s];
            chain.previous[idx] = chain.current[idx];
            chain.current[idx]  = _anchorBuf[s];
        }

        // ── Step 3: Relax segment-length constraints ──
        // Anchors stay pinned: when one side of a constraint is an anchor, the other absorbs
        // the entire correction (×2 because the standard split halves it).
        for (int iter = 0; iter < _constraintIterations; iter++)
        {
            for (int i = 0; i < totalNodes - 1; i++)
            {
                Vector3 delta = chain.current[i + 1] - chain.current[i];
                float   dist  = delta.magnitude;
                if (dist < 1e-4f) continue;

                bool pinA = IsAnchor(chain.anchorIndices, i);
                bool pinB = IsAnchor(chain.anchorIndices, i + 1);
                if (pinA && pinB) continue; // segment between two anchors — nothing can move

                Vector3 adjust = (delta / dist) * ((dist - perSegRest) * 0.5f);
                if      (pinA)            chain.current[i + 1] -= adjust * 2f;
                else if (pinB)            chain.current[i]     += adjust * 2f;
                else                      { chain.current[i] += adjust; chain.current[i + 1] -= adjust; }
            }
        }

        // ── Step 4: Ground collision (non-anchor nodes only) ──
        // Cast a short ray down from each interior node. Clamp it to sit `_noodleThickness`
        // above the ground surface and zero its downward velocity if it had sunk.
        for (int i = 0; i < totalNodes; i++)
        {
            if (IsAnchor(chain.anchorIndices, i)) continue;

            Vector3 pos = chain.current[i];
            if (Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down,
                                out RaycastHit hit, 0.2f, _groundMask,
                                QueryTriggerInteraction.Ignore))
            {
                float restHeight = hit.point.y + _noodleThickness;
                if (pos.y < restHeight)
                {
                    chain.current[i].y  = restHeight;
                    chain.previous[i].y = restHeight; // kill downward velocity
                }
            }
        }

        _chains[target] = chain;
    }

    /// <summary>
    /// Writes the wrapped path for the pair (this, target) into <paramref name="outAnchors"/>.
    /// If either tether reference is missing (e.g. before NetworkSpawn) falls back to the
    /// straight-line endpoint pair.
    /// </summary>
    private void FetchAnchors(SpaghettiRenderer target, List<Vector3> outAnchors)
    {
        if (_tether != null && target._tether != null)
        {
            _tether.CopyPathTo(target._tether, outAnchors);
            return;
        }
        outAnchors.Clear();
        outAnchors.Add(transform.position);
        outAnchors.Add(target.transform.position);
    }

    /// <summary>
    /// Fills <paramref name="outCounts"/> with per-segment interior node counts that sum to
    /// <see cref="_nodeCount"/> (or to <paramref name="segments"/> if that's larger — every
    /// segment always gets at least 1 interior node so the Verlet integration has something
    /// to work with). The remainder is placed in the leading segments.
    /// </summary>
    private void DistributeInterior(int segments, int[] outCounts)
    {
        int baseCount = Mathf.Max(1, _nodeCount / segments);
        int total     = baseCount * segments;
        int remainder = Mathf.Max(0, _nodeCount - total);
        for (int s = 0; s < segments; s++)
            outCounts[s] = baseCount + (s < remainder ? 1 : 0);
    }

    /// <summary>
    /// Allocates a fresh chain whose flat layout is
    /// <c>[a0, interior0..., a1, interior1..., a2, ..., aK-1]</c>. Seeds both `current` and
    /// `previous` to the initial lerped positions so nodes start with zero velocity.
    /// </summary>
    private static Chain BuildChain(List<Vector3> anchors, int[] interior)
    {
        int anchorCount = anchors.Count;
        int segments    = anchorCount - 1;

        int totalNodes = anchorCount;
        for (int s = 0; s < segments; s++) totalNodes += interior[s];

        var chain = new Chain
        {
            current        = new Vector3[totalNodes],
            previous       = new Vector3[totalNodes],
            anchorIndices  = new int[anchorCount],
        };

        int write = 0;
        chain.anchorIndices[0] = 0;
        chain.current[write]   = anchors[0];
        chain.previous[write]  = anchors[0];

        for (int s = 0; s < segments; s++)
        {
            Vector3 a     = anchors[s];
            Vector3 b     = anchors[s + 1];
            int     count = interior[s];

            for (int n = 1; n <= count; n++)
            {
                write++;
                float   t = (float)n / (count + 1);
                Vector3 p = Vector3.Lerp(a, b, t);
                chain.current[write]  = p;
                chain.previous[write] = p;
            }
            write++;
            chain.current[write]  = b;
            chain.previous[write] = b;
            chain.anchorIndices[s + 1] = write;
        }

        return chain;
    }

    /// <summary>
    /// Linear search of the sorted anchor-index array. Anchor counts are small (2 .. ~10)
    /// so a linear scan outperforms a binary search and a HashSet without extra allocation.
    /// </summary>
    private static bool IsAnchor(int[] anchorIndices, int idx)
    {
        for (int k = 0; k < anchorIndices.Length; k++)
        {
            int a = anchorIndices[k];
            if (a == idx) return true;
            if (a >  idx) return false;
        }
        return false;
    }

    // ── Line Renderer ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes Catmull-Rom-smoothed positions from the Verlet chain into the LineRenderer.
    /// At pivots (intermediate anchors) the tangent control points are clamped so the curve
    /// does not overshoot — the rope takes a visible bend at each pivot without the kink a
    /// raw polyline would show and without the overshoot a naive Catmull-Rom spline would produce.
    /// </summary>
    private void UpdateLineRenderer(LineRenderer lr, in Chain chain)
    {
        int total = chain.current.Length;

        _smoothedPoints.Clear();
        for (int i = 0; i < total - 1; i++)
        {
            // Clamp control points at pivots: if i is an anchor, p0 collapses to p1 (so the
            // tangent entering p1 does not reach "back across" the pivot). Likewise p3 collapses
            // to p2 if (i+1) is an anchor. Also clamp at the chain's physical ends.
            int p0idx = IsAnchor(chain.anchorIndices, i)     ? i     : Mathf.Max(0,         i - 1);
            int p3idx = IsAnchor(chain.anchorIndices, i + 1) ? i + 1 : Mathf.Min(total - 1, i + 2);

            Vector3 p0 = chain.current[p0idx];
            Vector3 p1 = chain.current[i];
            Vector3 p2 = chain.current[i + 1];
            Vector3 p3 = chain.current[p3idx];

            for (int s = 0; s < _smoothingSteps; s++)
            {
                float t = (float)s / _smoothingSteps;
                _smoothedPoints.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }
        _smoothedPoints.Add(chain.current[total - 1]); // final endpoint

        lr.positionCount = _smoothedPoints.Count;
        for (int i = 0; i < _smoothedPoints.Count; i++)
            lr.SetPosition(i, _smoothedPoints[i]);
    }

    /// <summary>Evaluates a Catmull-Rom spline at <paramref name="t"/> ∈ [0,1] between
    /// <paramref name="p1"/> and <paramref name="p2"/>, using <paramref name="p0"/> and
    /// <paramref name="p3"/> as the preceding and following tangent control points.</summary>
    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
             (2f * p1) +
             (-p0 + p2)                          * t  +
             (2f * p0 - 5f * p1 + 4f * p2 - p3)  * t2 +
             (-p0 + 3f * p1 - 3f * p2 + p3)      * t3
        );
    }

    // ── Gizmos ──────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        int myIndex = Instances.IndexOf(this);
        for (int i = myIndex + 1; i < Instances.Count; i++)
        {
            SpaghettiRenderer target = Instances[i];
            if (!_chains.TryGetValue(target, out Chain chain)) continue;

            // Interior Verlet nodes.
            Gizmos.color = _nodeColor;
            for (int j = 0; j < chain.current.Length; j++)
            {
                if (IsAnchor(chain.anchorIndices, j)) continue;
                Gizmos.DrawSphere(chain.current[j], _nodeGizmoRadius);
            }

            // Anchors (endpoints + pivots) — visually distinct.
            Gizmos.color = _anchorColor;
            for (int a = 0; a < chain.anchorIndices.Length; a++)
                Gizmos.DrawSphere(chain.current[chain.anchorIndices[a]], _nodeGizmoRadius * 1.2f);

            // Segment lines between adjacent nodes.
            Gizmos.color = _lineColor;
            for (int j = 0; j < chain.current.Length - 1; j++)
                Gizmos.DrawLine(chain.current[j], chain.current[j + 1]);
        }
    }
}
