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
        if (available.Count == 0)
        {
            CreateNew();
        }

        MarinaraDecalInstance instance = available.Dequeue();
        instance.gameObject.SetActive(true);
        return instance;
    }

    public void ReturnToPool(MarinaraDecalInstance instance)
    {
        instance.gameObject.SetActive(false);
        available.Enqueue(instance);
    }
}