using UnityEngine;
using UnityEditor;

namespace ZG
{
    [CustomEditor(typeof(PhysicsHierarchyDatabase))]
    public class PhysicsHierarchyDatabaseEditor : Editor
    {
        [MenuItem("Assets/ZG/MeshInstance/Rebuild All Physics Hierarchies")]
        [CommandEditor("Physics", 0)]
        public static void RebuildAllPhysicsHierarchies()
        {
            PhysicsHierarchyDatabase target;
            var guids = AssetDatabase.FindAssets("t:PhysicsHierarchyDatabase");
            string path;
            int numGUIDs = guids.Length;
            for (int i = 0; i < numGUIDs; ++i)
            {
                path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (EditorUtility.DisplayCancelableProgressBar("Rebuild All Physics Hierarchies", path, i * 1.0f / numGUIDs))
                    break;

                target = AssetDatabase.LoadAssetAtPath<PhysicsHierarchyDatabase>(path);
                if (target == null)
                    continue;

                if (target.root == null)
                {
                    Debug.LogError($"{target.name} missing root", target.root);

                    continue;
                }

                switch (PrefabUtility.GetPrefabAssetType(target.root))
                {
                    case PrefabAssetType.Regular:
                    case PrefabAssetType.Variant:
                        var root = (Transform)PrefabUtility.InstantiatePrefab(target.root);

                        PrefabUtility.RecordPrefabInstancePropertyModifications(root);

                        var prefab = PrefabUtility.GetNearestPrefabInstanceRoot(root);

                        PrefabUtility.ApplyPrefabInstance(prefab, InteractionMode.AutomatedAction);

                        DestroyImmediate(prefab);
                        break;
                }

                //PhysicsHierarchyDatabase.Data.isShowProgressBar = false;
                try
                {
                    target.Create();

                    target.EditorMaskDirty();
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e.InnerException ?? e, target);
                }
            }

            EditorUtility.ClearProgressBar();
        }

        public override void OnInspectorGUI()
        {
            var target = (PhysicsHierarchyDatabase)base.target;

            bool isRebuild = false;

            EditorGUI.BeginChangeCheck();
            target.root = EditorGUILayout.ObjectField("Root", target.root, typeof(Transform), true) as Transform;
            if (EditorGUI.EndChangeCheck() || GUILayout.Button("Rebuild"))
            {
                if (target.root != null)
                {
                    target.Create();

                    if (PrefabUtility.GetPrefabInstanceStatus(target.root) == PrefabInstanceStatus.Connected)
                        target.root = PrefabUtility.GetCorrespondingObjectFromSource(target.root);
                }

                isRebuild = true;
            }

            isRebuild = GUILayout.Button("Reset") || isRebuild;
            if (isRebuild)
                target.EditorMaskDirty();

            base.OnInspectorGUI();
        }
    }
}