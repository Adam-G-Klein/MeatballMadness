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

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void OnEnable()  => Instances.Add(this);

    private void OnDisable()
    {
        Instances.Remove(this);
        _chains.Clear();
    }

    // ── Simulation ───────────────────────────────────────────────────────────

    private void LateUpdate()
    {
        // Only process pairs where this instance has a lower index than the partner.
        // This guarantees exactly one chain per pair.
        int myIndex = Instances.IndexOf(this);
        for (int i = myIndex + 1; i < Instances.Count; i++)
            StepChain(Instances[i]);
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
