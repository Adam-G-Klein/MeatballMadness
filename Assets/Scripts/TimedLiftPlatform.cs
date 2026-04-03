using System.Collections.Generic;
using UnityEngine;

public class TimedLiftPlatform : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Local offset from the starting position that the platform will move to.")]
    public Vector3 targetOffset = new Vector3(0, 5, 0);

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
    public bool parentPlayerWhileOnPlatform = true;

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
        startPoint = transform.position;
        targetPoint = startPoint + targetOffset;
    }

    private void Update()
    {
        bool playerOnPlatform = playersOnPlatform.Count > 0;

        if (!movingToTarget && !movingToStart)
        {
            if (atStart)
            {
                if (playerOnPlatform)
                {
                    onTimer += Time.deltaTime;

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
                    offTimer += Time.deltaTime;

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

            if (Vector3.Distance(transform.position, targetPoint) <= arriveDistance)
            {
                transform.position = targetPoint;
                movingToTarget = false;
                atTarget = true;
                atStart = false;
                offTimer = 0f;
            }
        }
        else if (movingToStart)
        {
            MovePlatform(startPoint);

            if (Vector3.Distance(transform.position, startPoint) <= arriveDistance)
            {
                transform.position = startPoint;
                movingToStart = false;
                atStart = true;
                atTarget = false;
                onTimer = 0f;
            }
        }
    }

    private void MovePlatform(Vector3 destination)
    {
        transform.position = Vector3.MoveTowards(
            transform.position,
            destination,
            moveSpeed * Time.deltaTime
        );
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