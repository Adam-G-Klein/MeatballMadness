using UnityEngine;

public class TimedLiftPlatformDetector : MonoBehaviour
{
    public TimedLiftPlatform platform;

    private void Reset()
    {
        if (platform == null)
        {
            platform = GetComponentInParent<TimedLiftPlatform>();
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (platform != null)
        {
            platform.NotifyPlayerEntered(other.transform);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (platform != null)
        {
            platform.NotifyPlayerExited(other.transform);
        }
    }
}