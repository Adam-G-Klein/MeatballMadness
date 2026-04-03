using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TimedLiftPlatform : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Local offset from the starting position that the platform will move to.")]
    public Vector3 targetOffset = new Vector3(0f, 5f, 0f);

    [Tooltip("How fast the platform moves.")]
    public float moveSpeed = 2f;

    [Header("Timing")]
    [Tooltip("How long the player must remain on the platform before it starts moving up.")]
    public float requiredOnTime = 1f;

    [Tooltip("How long the player must remain off the platform before it starts moving back down.")]
    public float requiredOffTime = 1f;

    [Header("Player Detection")]
    public string playerTag = "Player";
    public float arriveDistance = 0.02f;

    [Header("Optional")]
    [Tooltip("If true, the player will be parented to the platform while on it.")]
    public bool parentPlayerWhileOnPlatform = false;

    private Rigidbody rb;

    private Vector3 startPoint;
    private Vector3 targetPoint;

    private float onTimer;
    private float offTimer;

    private bool movingToTarget;
    private bool movingToStart;
    private bool atTarget;
    private bool atStart = true;

    private readonly HashSet<Transform> playersOnPlatform = new HashSet<Transform>();

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        startPoint = rb.position;
        targetPoint = startPoint + targetOffset;
    }

    private void FixedUpdate()
    {
        bool playerOnPlatform = playersOnPlatform.Count > 0;

        if (!movingToTarget && !movingToStart)
        {
            if (atStart)
            {
                if (playerOnPlatform)
                {
                    onTimer += Time.fixedDeltaTime;

                    if (onTimer >= requiredOnTime)
                    {
                        StartMovingToTarget();
                    }
                }
                else
                {
                    onTimer = 0f;
                }
            }
            else if (atTarget)
            {
                if (!playerOnPlatform)
                {
                    offTimer += Time.fixedDeltaTime;

                    if (offTimer >= requiredOffTime)
                    {
                        StartMovingToStart();
                    }
                }
                else
                {
                    offTimer = 0f;
                }
            }
        }

        if (movingToTarget)
        {
            MovePlatform(targetPoint);

            if (Vector3.Distance(rb.position, targetPoint) <= arriveDistance)
            {
                rb.MovePosition(targetPoint);
                movingToTarget = false;
                atTarget = true;
                atStart = false;
                offTimer = 0f;
            }
        }
        else if (movingToStart)
        {
            MovePlatform(startPoint);

            if (Vector3.Distance(rb.position, startPoint) <= arriveDistance)
            {
                rb.MovePosition(startPoint);
                movingToStart = false;
                atStart = true;
                atTarget = false;
                onTimer = 0f;
            }
        }
    }

    private void MovePlatform(Vector3 destination)
    {
        Vector3 nextPosition = Vector3.MoveTowards(
            rb.position,
            destination,
            moveSpeed * Time.fixedDeltaTime
        );

        rb.MovePosition(nextPosition);
    }

    private void StartMovingToTarget()
    {
        movingToTarget = true;
        movingToStart = false;
        onTimer = 0f;
    }

    private void StartMovingToStart()
    {
        movingToStart = true;
        movingToTarget = false;
        offTimer = 0f;
    }

    public void NotifyPlayerEntered(Transform player)
    {
        if (player == null) return;
        if (!player.CompareTag(playerTag)) return;

        playersOnPlatform.Add(player);

        if (parentPlayerWhileOnPlatform)
        {
            player.SetParent(transform, true);
        }
    }

    public void NotifyPlayerExited(Transform player)
    {
        if (player == null) return;
        if (!player.CompareTag(playerTag)) return;

        if (playersOnPlatform.Contains(player))
        {
            playersOnPlatform.Remove(player);
        }

        if (parentPlayerWhileOnPlatform && player.parent == transform)
        {
            player.SetParent(null, true);
        }
    }
}