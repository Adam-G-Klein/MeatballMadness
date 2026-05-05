using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class SkinnedMeshRebinder : EditorWindow
{
    private SkinnedMeshRenderer _renderer;
    private Transform _newSkeletonRoot;
    private Vector2 _scroll;
    private List<string> _missingBones = new List<string>();
    private List<string> _remappedBones = new List<string>();
    private bool _hasPreview;

    [MenuItem("Tools/Skinned Mesh Rebinder")]
    public static void Open() => GetWindow<SkinnedMeshRebinder>("SMR Rebinder");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Remaps a SkinnedMeshRenderer's bones[] (and rootBone) to the same-named transforms under a new skeleton root. " +
            "The two skeletons must share bone names. Bind poses are baked into the mesh asset, so the new rig should have the same rest pose as the original.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        _renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
            new GUIContent("Renderer", "SkinnedMeshRenderer to rebind"),
            _renderer, typeof(SkinnedMeshRenderer), true);
        _newSkeletonRoot = (Transform)EditorGUILayout.ObjectField(
            new GUIContent("New Skeleton Root", "Ancestor transform that contains the new bones"),
            _newSkeletonRoot, typeof(Transform), true);
        if (EditorGUI.EndChangeCheck()) _hasPreview = false;

        using (new EditorGUI.DisabledScope(_renderer == null || _newSkeletonRoot == null))
        {
            if (GUILayout.Button("Preview")) BuildPreview();
            if (GUILayout.Button("Rebind")) Rebind();
        }

        if (_hasPreview)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Matched: {_remappedBones.Count}    Missing: {_missingBones.Count}", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_missingBones.Count > 0)
            {
                EditorGUILayout.LabelField("Missing in new skeleton:", EditorStyles.boldLabel);
                foreach (var n in _missingBones) EditorGUILayout.LabelField("  " + n);
            }
            if (_remappedBones.Count > 0)
            {
                EditorGUILayout.LabelField("Will remap:", EditorStyles.boldLabel);
                foreach (var n in _remappedBones) EditorGUILayout.LabelField("  " + n);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void BuildPreview()
    {
        _missingBones.Clear();
        _remappedBones.Clear();
        var lookup = BuildNameLookup(_newSkeletonRoot);
        foreach (var b in _renderer.bones)
        {
            if (b == null) { _missingBones.Add("<null entry>"); continue; }
            if (lookup.ContainsKey(b.name)) _remappedBones.Add(b.name);
            else _missingBones.Add(b.name);
        }
        _hasPreview = true;
    }

    private void Rebind()
    {
        var lookup = BuildNameLookup(_newSkeletonRoot);
        var oldBones = _renderer.bones;
        var newBones = new Transform[oldBones.Length];
        var missing = new List<string>();
        for (int i = 0; i < oldBones.Length; i++)
        {
            var ob = oldBones[i];
            if (ob != null && lookup.TryGetValue(ob.name, out var nb)) newBones[i] = nb;
            else { newBones[i] = ob; missing.Add(ob != null ? ob.name : $"<null at index {i}>"); }
        }

        Transform newRoot = _renderer.rootBone;
        if (_renderer.rootBone != null && lookup.TryGetValue(_renderer.rootBone.name, out var mappedRoot))
            newRoot = mappedRoot;

        Undo.RecordObject(_renderer, "Rebind Skinned Mesh");
        _renderer.bones = newBones;
        _renderer.rootBone = newRoot;
        EditorUtility.SetDirty(_renderer);

        if (missing.Count > 0)
            Debug.LogWarning($"[SMR Rebinder] {missing.Count} bone(s) had no match and were left unchanged: {string.Join(", ", missing)}", _renderer);
        else
            Debug.Log($"[SMR Rebinder] Rebound {oldBones.Length} bones to '{_newSkeletonRoot.name}'.", _renderer);

        BuildPreview();
    }

    private static Dictionary<string, Transform> BuildNameLookup(Transform root)
    {
        var dict = new Dictionary<string, Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!dict.ContainsKey(t.name)) dict[t.name] = t;
        }
        return dict;
    }
}
