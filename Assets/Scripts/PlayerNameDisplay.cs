using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Lives on a display prefab spawned as a world-space child of the PlayerMeatball.
/// Shows the assigned nickname above the meatball.
///
/// Call Initialize() after spawning, passing the owning player's clientId and
/// whether this is the local (owned) meatball.
/// </summary>
public class PlayerNameDisplay : MonoBehaviour
{
    [SerializeField] TMP_Text _nameLabel;
    [SerializeField] float _yOffset = 1.5f;
    [SerializeField] float _minSpeedToRotate = 0.2f;
    [SerializeField] float _rotationSmoothing = 10f;

    Transform _meatball;
    MeatballNetSync _meatballNetSync;

    public void Initialize(ulong ownerClientId, Transform meatball)
    {
        _meatball = meatball;
        _meatballNetSync = meatball != null ? meatball.GetComponent<MeatballNetSync>() : null;

        if (_nameLabel == null)
        {
            Debug.LogWarning("[PlayerNameDisplay] No TMP_Text assigned.", this);
            return;
        }

        if (PlayerNameAssignment.Instance != null && PlayerNameAssignment.Instance.HasName(ownerClientId))
            _nameLabel.text = PlayerNameAssignment.Instance.GetName(ownerClientId);
        else
            StartCoroutine(WaitAndSetName(ownerClientId));
    }

    void LateUpdate()
    {
        if (_meatball == null) return;

        transform.position = _meatball.position + Vector3.up * _yOffset;

        if (_meatballNetSync == null) return;

        Vector3 horizontalVel = _meatballNetSync.NetworkedVelocity;
        horizontalVel.y = 0f;

        if (horizontalVel.sqrMagnitude < _minSpeedToRotate * _minSpeedToRotate) return;

        Quaternion targetRot = Quaternion.LookRotation(horizontalVel.normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 1f - Mathf.Exp(-_rotationSmoothing * Time.deltaTime));
    }

    private IEnumerator WaitAndSetName(ulong ownerClientId)
    {
        yield return new WaitUntil(() =>
            PlayerNameAssignment.Instance != null &&
            PlayerNameAssignment.Instance.HasName(ownerClientId));

        _nameLabel.text = PlayerNameAssignment.Instance.GetName(ownerClientId);
    }
}
