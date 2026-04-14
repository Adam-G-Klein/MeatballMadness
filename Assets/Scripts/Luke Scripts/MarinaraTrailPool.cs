using System.Collections.Generic;
using UnityEngine;

public class MarinaraTrailPool : MonoBehaviour
{
    public static MarinaraTrailPool Instance { get; private set; }

    [Header("Pool")]
    [SerializeField] private MarinaraDecalInstance decalPrefab;
    [SerializeField] private int initialPoolSize = 300;
    [SerializeField] private Transform container;

    private readonly Queue<MarinaraDecalInstance> available = new Queue<MarinaraDecalInstance>();
    private readonly Queue<MarinaraDecalInstance> inUseOrder = new Queue<MarinaraDecalInstance>();
    private readonly List<MarinaraDecalInstance> allDecals = new List<MarinaraDecalInstance>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (container == null)
        {
            GameObject go = new GameObject("MarinaraDecalContainer");
            container = go.transform;
        }

        for (int i = 0; i < initialPoolSize; i++)
        {
            CreateNew();
        }
    }

    private MarinaraDecalInstance CreateNew()
    {
        MarinaraDecalInstance instance = Instantiate(decalPrefab, container);
        instance.gameObject.SetActive(false);
        instance.SetPool(this);

        available.Enqueue(instance);
        allDecals.Add(instance);

        return instance;
    }

    public MarinaraDecalInstance Get()
    {
        MarinaraDecalInstance instance;

        if (available.Count > 0)
        {
            instance = available.Dequeue();
        }
        else
        {
            if (inUseOrder.Count == 0)
            {
                Debug.LogError("MarinaraTrailPool has no available decals and no active decals to recycle.");
                return null;
            }

            // Reuse the oldest active decal instead of creating a new one.
            instance = inUseOrder.Dequeue();

            // Detach from any moving platform or old parent before reuse.
            instance.transform.SetParent(container, true);
        }

        instance.gameObject.SetActive(true);
        inUseOrder.Enqueue(instance);

        return instance;
    }

    public void ReturnToPool(MarinaraDecalInstance instance)
    {
        if (instance == null)
            return;

        instance.transform.SetParent(container, true);
        instance.gameObject.SetActive(false);

        if (!available.Contains(instance))
        {
            available.Enqueue(instance);
        }
    }
}