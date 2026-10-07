using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Places a room root at the horizontal centre of its visible contents without
/// changing the world positions of the room's children.
/// </summary>
public static class CenterRoomPivots
{
    [MenuItem("Tools/Horror Lab/Center Selected Room Pivots")]
    private static void CenterSelectedRoomPivots()
    {
        foreach (var selected in Selection.transforms)
        {
            var renderers = selected.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning($"{selected.name} has no visible renderers to centre.", selected);
                continue;
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            var children = new List<Transform>();
            var worldPositions = new List<Vector3>();
            foreach (Transform child in selected)
            {
                children.Add(child);
                worldPositions.Add(child.position);
            }

            Undo.RecordObject(selected, "Center Room Pivot");
            Undo.RecordObjects(children.ToArray(), "Preserve Room Contents");

            var currentPosition = selected.position;
            selected.position = new Vector3(bounds.center.x, currentPosition.y, bounds.center.z);

            for (var i = 0; i < children.Count; i++)
                children[i].position = worldPositions[i];

            EditorUtility.SetDirty(selected);
            foreach (var child in children)
                EditorUtility.SetDirty(child);
        }
    }

    [MenuItem("Tools/Horror Lab/Center Selected Room Pivots", true)]
    private static bool CanCenterSelectedRoomPivots()
    {
        return Selection.transforms.Length > 0;
    }
}
