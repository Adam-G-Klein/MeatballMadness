using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cosmetic Verlet chain between every pair of meatballs. Attach one to each meatball.
///
/// Only the lower-indexed instance in Instances owns and simulates the chain for each pair,
/// so exactly one chain runs per pair regardless of how many meatballs are in the scene.
///
/// The chain does not participate in physics — it is visual-only. Gizmo spheres mark each
/// Verlet node; swap in a LineRenderer or ribbon mesh later for the real look.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class SpaghettiRenderer : MonoBehaviour
{
    /// <summary>All active SpaghettiRenderer instances. Populated via OnEnable/OnDisable.</summary>
    public static readonly List<SpaghettiRenderer> Instances = new();

    [Header("Chain Shape")]
    [Tooltip("Interior Verlet nodes, not counting the two meatball endpoint anchors.")]
    [SerializeField, Min(1)] private int _nodeCount = 4;

    [Tooltip("Full rest length of the tether. Match this to noodleLength in MeatballMovementSettings " +
             "so the chain sags when meatballs are close and goes taut when they are far apart.")]
    [SerializeField] private float _tetherRestLength = 5f;

    [Header("Simulation")]
    [Tooltip("Fraction of world gravity applied to interior nodes each step. " +
             "Lower values produce a lighter, less droopy noodle.")]
    [SerializeField] private float _gravityScale = 0.4f;

    [Tooltip("Velocity retention per frame (0–1). Lower = more drag; chain settles faster. " +
             "Values above ~0.99 produce a very springy, slow-settling noodle.")]
    [SerializeField, Range(0f, 1f)] private float _damping = 0.97f;

    [Tooltip("Length-constraint relaxation passes per frame. " +
             "More iterations = stiffer, less stretchy chain. 2–4 is typical.")]
    [SerializeField, Min(1)] private int _constraintIterations = 3;

    [Tooltip("Layers the ground raycast checks against. Set this to your ground/terrain layer " +
             "so the chain never clips below the floor.")]
    [SerializeField] private LayerMask _groundMask = Physics.DefaultRaycastLayers;

    [Tooltip("Radius of the noodle. Interior nodes are kept this far above the ground surface.")]
    [SerializeField, Min(0f)] private float _noodleThickness = 0.05f;

    [Header("Line Renderer")]
    [Tooltip("Catmull-Rom subdivisions between each Verlet node. Higher = smoother curve, more verts.")]
    [SerializeField, Min(1)] private int _smoothingSteps = 8;

    [Header("Gizmos")]
    [SerializeField] private float _nodeGizmoRadius = 0.07f;
    [SerializeField] private Color _nodeColor       = new Color(0.95f, 0.6f, 0.2f, 1.00f);
    [SerializeField] private Color _lineColor       = new Color(0.95f, 0.6f, 0.2f, 0.55f);

    // ── Per-target chain data ────────────────────────────────────────────────
    private struct Chain
    {
        public Vector3[] current;   // positions this frame
        public Vector3[] previous;  // positions last frame (encodes velocity implicitly)
    }

    private readonly Dictionary<SpaghettiRenderer, Chain> _chains = new();

    private LineRenderer   _lineRenderer;
    private readonly List<Vector3> _smoothedPoints = new(); // reused buffer to avoid per-frame allocation

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake() => _lineRenderer = GetComponent<LineRenderer>();

    private void OnEnable()  => Instances.Add(this);

    private void OnDisable()
    {
        Instances.Remove(this);
        _chains.Clear();
        if (_lineRenderer != null) _lineRenderer.positionCount = 0;
    }

    // ── Simulation ───────────────────────────────────────────────────────────

    private void LateUpdate()
    {
        // Only process pairs where this instance has a lower index than the partner.
        // This guarantees exactly one chain per pair.
        int myIndex = Instances.IndexOf(this);

        bool drewChain = false;
        for (int i = myIndex + 1; i < Instances.Count; i++)
        {
            SpaghettiRenderer target = Instances[i];
            StepChain(target);

            // Drive the LineRenderer with the first chain this instance owns.
            // (For 2-player this is always the only chain.)
            if (!drewChain && _lineRenderer != null && _chains.TryGetValue(target, out Chain chain))
            {
                UpdateLineRenderer(in chain);
                drewChain = true;
            }
        }

        // If this instance owns no chains (e.g. it is the higher-indexed meatball),
        // keep the LineRenderer empty so it renders nothing.
        if (!drewChain && _lineRenderer != null)
            _lineRenderer.positionCount = 0;
    }

    private void StepChain(SpaghettiRenderer target)
    {
        Vector3 start = transform.position;
        Vector3 end   = target.transform.position;
        int     total = _nodeCount + 2; // interior nodes + 2 endpoint anchors

        // Rebuild if this is a new pair or _nodeCount was changed while tuning.
        if (!_chains.TryGetValue(target, out Chain chain) || chain.current.Length != total)
            chain = BuildChain(start, end, total);

        float dt          = Time.deltaTime;
        float gravityStep = Physics.gravity.y * _gravityScale * dt * dt;
        float segRestLen  = _tetherRestLength / (_nodeCount + 1);

        // ── Step 1: Verlet-integrate every interior node ─────────────────────
        // velocity ≈ (current - previous); multiply by damping to add drag.
        for (int i = 1; i < total - 1; i++)
        {
            Vector3 vel = (chain.current[i] - chain.previous[i]) * _damping;
            chain.previous[i]  = chain.current[i];
            chain.current[i]  += vel + new Vector3(0f, gravityStep, 0f);
        }

        // ── Step 2: Pin both endpoints to their meatball positions ───────────
        // Updating previous before clamping ensures the anchor contributes zero
        // implicit velocity, preventing endpoint jitter from bleeding inward.
        chain.previous[0]         = chain.current[0];
        chain.current[0]          = start;
        chain.previous[total - 1] = chain.current[total - 1];
        chain.current[total - 1]  = end;

        // ── Step 3: Relax segment-length constraints ─────────────────────────
        // Each iteration nudges adjacent nodes toward their rest distance.
        // Endpoints are pinned and do not move during relaxation.
        for (int iter = 0; iter < _constraintIterations; iter++)
        {
            for (int i = 0; i < total - 1; i++)
            {
                Vector3 delta  = chain.current[i + 1] - chain.current[i];
                float   dist   = delta.magnitude;
                if (dist < 0.0001f) continue;

                // Split the correction evenly unless one end is a pinned endpoint.
                Vector3 adjust = (delta / dist) * ((dist - segRestLen) * 0.5f);
                bool    pinA   = (i == 0);
                bool    pinB   = (i == total - 2);
                if (!pinA) chain.current[i]     += adjust;
                if (!pinB) chain.current[i + 1] -= adjust;
            }
        }

        // ── Step 4: Ground collision ─────────────────────────────────────────
        // Cast a short ray downward from each interior node. If the node has
        // sunk below the surface, push it back up and zero out downward velocity.
        for (int i = 1; i < total - 1; i++)
        {
            Vector3 pos = chain.current[i];
            if (Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down, out RaycastHit hit, 0.2f, _groundMask))
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

    /// <summary>Initializes a new chain with nodes evenly distributed along the straight line
    /// from <paramref name="start"/> to <paramref name="end"/>.</summary>
    private static Chain BuildChain(Vector3 start, Vector3 end, int total)
    {
        var chain = new Chain
        {
            current  = new Vector3[total],
            previous = new Vector3[total],
        };
        for (int i = 0; i < total; i++)
        {
            float t = (float)i / (total - 1);
            Vector3 p = Vector3.Lerp(start, end, t);
            chain.current[i]  = p;
            chain.previous[i] = p;  // zero initial velocity
        }
        return chain;
    }

    // ── Line Renderer ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes Catmull-Rom-smoothed positions from the Verlet chain into the LineRenderer.
    /// Each pair of adjacent nodes is subdivided into <see cref="_smoothingSteps"/> intervals,
    /// using the neighboring nodes as Catmull-Rom tangent control points so the curve passes
    /// smoothly through every Verlet node without kinks.
    /// </summary>
    private void UpdateLineRenderer(in Chain chain)
    {
        int total = chain.current.Length;

        _smoothedPoints.Clear();
        for (int i = 0; i < total - 1; i++)
        {
            // Clamp control points at the chain boundaries by repeating the endpoint.
            Vector3 p0 = chain.current[Mathf.Max(0,         i - 1)];
            Vector3 p1 = chain.current[i];
            Vector3 p2 = chain.current[i + 1];
            Vector3 p3 = chain.current[Mathf.Min(total - 1, i + 2)];

            for (int s = 0; s < _smoothingSteps; s++)
            {
                float t = (float)s / _smoothingSteps;
                _smoothedPoints.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }
        _smoothedPoints.Add(chain.current[total - 1]); // final endpoint

        _lineRenderer.positionCount = _smoothedPoints.Count;
        for (int i = 0; i < _smoothedPoints.Count; i++)
            _lineRenderer.SetPosition(i, _smoothedPoints[i]);
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
             (-p0 + p2)                    * t  +
             (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
             (-p0 + 3f * p1 - 3f * p2 + p3)     * t3
        );
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        int myIndex = Instances.IndexOf(this);
        for (int i = myIndex + 1; i < Instances.Count; i++)
        {
            SpaghettiRenderer target = Instances[i];
            if (!_chains.TryGetValue(target, out Chain chain)) continue;

            // Draw a sphere at every node (endpoints + interior).
            Gizmos.color = _nodeColor;
            foreach (Vector3 pos in chain.current)
                Gizmos.DrawSphere(pos, _nodeGizmoRadius);

            // Draw segments connecting adjacent nodes.
            Gizmos.color = _lineColor;
            for (int j = 0; j < chain.current.Length - 1; j++)
                Gizmos.DrawLine(chain.current[j], chain.current[j + 1]);
        }
    }
}
