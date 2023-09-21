using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Physics;
using Unity.Physics.Authoring;
using UnityEngine;
using UnityEditor;

namespace ZG
{
    public class PhysicsEditor : EditorWindow
    {
        public const string NAME_SPACE_MATERIAL_TEMPLATE_GUID = "PhysicsEditorMaterialTemplateGUID";

        public static float convexRadius
        {
            get
            {
                return 0.0f;
            }
        }

        public static float maxCombineDistanceSquare
        {
            get
            {
                return 0.1f;
            }
        }

        public static PhysicsMaterialTemplate materialTemplate
        {
            get
            {
                return EditorPrefs.HasKey(NAME_SPACE_MATERIAL_TEMPLATE_GUID) ? 
                    AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(NAME_SPACE_MATERIAL_TEMPLATE_GUID))) as PhysicsMaterialTemplate : 
                    null;
            }

            set
            {
                if (value == null)
                    EditorPrefs.DeleteKey(NAME_SPACE_MATERIAL_TEMPLATE_GUID);
                else
                    EditorPrefs.SetString(NAME_SPACE_MATERIAL_TEMPLATE_GUID, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)));
            }
        }

        public static void Convert(bool isBaked, int groupIndex, Transform transform, NativeList<CompoundCollider.ColliderBlobInstance> results)
        {
            var materialTemplate = PhysicsEditor.materialTemplate;

            var colliders = transform.GetComponentsInChildren<UnityEngine.Collider>();
            colliders.Convert(
                results, 
                transform, 
                materialTemplate.GetMaterial(), 
                materialTemplate.GetFilter(groupIndex), 
                convexRadius, 
                0, 
                0, 
                isBaked, 
                true);

            var shapes = transform.GetComponentsInChildren<PhysicsShapeAuthoring>();
            shapes.Convert(results, transform, groupIndex, 0, 0, isBaked, true);
        }

        public static void Serialize(
            ref NativeList<CompoundCollider.ColliderBlobInstance> colliderBlobInstances,
            GameObject gameObject,
            Dictionary<int, IEntityDataStreamSerializer> serializers = null)
        {
            var colliders = gameObject.GetComponentsInChildren<UnityEngine.Collider>();

            /*using (var memoryStream = new MemoryStream())
            {
                int length;
                BinaryWriter writer = new BinaryWriter(memoryStream);
                {
                    length = writer.Serialize(colliders, maxCombineDistanceSquare, false, true);
                }

                memoryStream.Position = 0L;

                BinaryReader reader = new BinaryReader(memoryStream);
                {
                    PhysicsMaterialTemplate materialTemplate = PhysicsEditor.materialTemplate;
                    reader.DeserializeLegacyColliders(ref colliderBlobInstances, materialTemplate.GetMaterial(), materialTemplate.GetFilter(), convexRadius, length);
                }
            }*/

            colliders.Convert(
                colliderBlobInstances, 
                null, 
                materialTemplate.GetMaterial(), 
                materialTemplate.GetFilter(), 
                convexRadius,
                0,
                0,
                true, 
                true);

            List<IEntityDataStreamSerializer> serializersTemp = null;
            EntityDataStreamSerializer serializer;
            int numColliders = colliders.Length;

            if (serializers != null)
            {
                for (int i = 0; i < numColliders; ++i)
                {
                    if (serializersTemp == null)
                        serializersTemp = new List<IEntityDataStreamSerializer>();

                    colliders[i].GetComponents(serializersTemp);

                    if (serializersTemp.Count > 0)
                    {
                        serializer = new EntityDataStreamSerializer();
                        serializer.children = serializersTemp;

                        if (serializers == null)
                            serializers = new Dictionary<int, IEntityDataStreamSerializer>();

                        serializers[i] = serializer;

                        serializersTemp = null;
                    }
                }
            }

            var shapes = gameObject.GetComponentsInChildren<PhysicsShapeAuthoring>();

            shapes.Convert(colliderBlobInstances, null, 0, 0, 0, true, true);

            if (serializers != null)
            {
                int numShapes = shapes.Length;
                for (int i = 0; i < numShapes; ++i)
                {
                    if (serializersTemp == null)
                        serializersTemp = new List<IEntityDataStreamSerializer>();

                    shapes[i].GetComponents(serializersTemp);

                    if (serializersTemp.Count > 0)
                    {
                        serializer = new EntityDataStreamSerializer();
                        serializer.children = serializersTemp;

                        if (serializers == null)
                            serializers = new Dictionary<int, IEntityDataStreamSerializer>();

                        serializers[i + numColliders] = serializer;

                        serializersTemp = null;
                    }
                }
            }
        }

        [MenuItem("GameObject/ZG/Physics/Filter Colliders", false, 10)]
        public static void FilterColliders(MenuCommand menuCommand)
        {
            GameObject context = menuCommand.context as GameObject;
            if (context == null)
                return;
            
            Transform transform = new GameObject("Colliders").transform;
            EditorTools.FilterTo<UnityEngine.Collider>(context, transform);
            EditorTools.FilterTo<PhysicsShapeAuthoring>(context, transform);
        }
        
        [MenuItem("GameObject/ZG/Physics/Save Legacy")]
        public static void SaveLegacy(MenuCommand menuCommand)
        {
            GameObject gameObject = menuCommand == null ? null : menuCommand.context as GameObject;
            if (gameObject == null)
                return;

            string path = EditorUtility.SaveFilePanel("Save Physics", string.Empty, "physics", string.Empty);
            if (string.IsNullOrEmpty(path))
                return;
            
            List<UnityEngine.Collider> colliders = new List<UnityEngine.Collider>();

            AssetDatabase.StartAssetEditing();

            gameObject.GetComponentsInChildren(colliders);

            PhysicsMaterialTemplate materialTemplate = PhysicsEditor.materialTemplate;
            using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
            {
                writer.Serialize(materialTemplate.GetMaterial());
                writer.Serialize(materialTemplate.GetFilter());
                writer.Serialize(colliders, maxCombineDistanceSquare, false, true);
            }

            AssetDatabase.StopAssetEditing();

            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("GameObject/ZG/Physics/Save With Combine Mesh")]
        public static void SaveWithCombineMesh(MenuCommand menuCommand)
        {
            GameObject gameObject = menuCommand == null ? null : menuCommand.context as GameObject;
            if (gameObject == null)
                return;

            string path = EditorUtility.SaveFilePanel("Save Physics", string.Empty, "physics", string.Empty);
            if (string.IsNullOrEmpty(path))
                return;

            List<UnityEngine.Collider> colliders = new List<UnityEngine.Collider>();

            AssetDatabase.StartAssetEditing();

            gameObject.GetComponentsInChildren(colliders);

            var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.TempJob);
            {
                using (var memoryStream = new MemoryStream())
                {
                    int length;
                    BinaryWriter writer = new BinaryWriter(memoryStream);
                    {
                        length = writer.Serialize(colliders, maxCombineDistanceSquare, true, true);
                    }

                    memoryStream.Position = 0L;

                    BinaryReader reader = new BinaryReader(memoryStream);
                    {
                        PhysicsMaterialTemplate materialTemplate = PhysicsEditor.materialTemplate;
                        reader.DeserializeLegacyColliders(ref colliderBlobInstances, materialTemplate.GetMaterial(), materialTemplate.GetFilter(), convexRadius, length);
                    }
                }

                //colliders.Convert(colliderBlobInstances.Add, null, materialTemplate.GetMaterial(), materialTemplate.GetFilter(), convexRadius, true);

                List<PhysicsShapeAuthoring> shapes = new List<PhysicsShapeAuthoring>();
                gameObject.GetComponentsInChildren(shapes);

                shapes.Convert(colliderBlobInstances, null, 0, 0, 0, true, true);

                AssetDatabase.StopAssetEditing();

                if (colliderBlobInstances.Length > 0)
                {
                    using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
                    {
                        writer.SerializeColliderBlobInstances(colliderBlobInstances.AsArray());
                    }
                }
            }
            colliderBlobInstances.Dispose();

            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("GameObject/ZG/Physics/Save")]
        public static void Save(MenuCommand menuCommand)
        {
            GameObject gameObject = menuCommand == null ? null : menuCommand.context as GameObject;
            if (gameObject == null)
                return;

            string path = EditorUtility.SaveFilePanel("Save Physics", string.Empty, "physics", string.Empty);
            if (string.IsNullOrEmpty(path))
                return;

            var serializers = new Dictionary<int, IEntityDataStreamSerializer>();

            var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.TempJob);
            {
                AssetDatabase.StartAssetEditing();

                Serialize(ref colliderBlobInstances, gameObject, serializers);

                AssetDatabase.StopAssetEditing();

                using (var buffer = new NativeBuffer(Allocator.Temp, 1))
                {
                    var writer = buffer.writer;

                    if (colliderBlobInstances.Length > 0)
                        writer.SerializeColliderBlobInstances(colliderBlobInstances.AsArray(), serializers);

                    using (var stream = File.Create(path))
                    {
                        stream.Write(buffer.ToBytes(), 0, buffer.length);
                    }
                }
            }
            colliderBlobInstances.Dispose();

            EditorUtility.RevealInFinder(path);
        }
        
        [MenuItem("GameObject/ZG/Physics/Append")]
        public static void Append(MenuCommand menuCommand)
        {
            GameObject gameObject = menuCommand == null ? null : menuCommand.context as GameObject;
            if (gameObject == null)
                return;

            string path = EditorUtility.SaveFilePanel("Save Physics", string.Empty, "physics", string.Empty);
            if (string.IsNullOrEmpty(path))
                return;

            var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.TempJob);
            {
                if (File.Exists(path))
                {
                    using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
                    {
                        reader.Deserialize(ref colliderBlobInstances);
                    }
                }

                List<UnityEngine.Collider> colliders = new List<UnityEngine.Collider>();
                gameObject.GetComponentsInChildren(colliders);

                PhysicsMaterialTemplate materialTemplate = PhysicsEditor.materialTemplate;
                colliders.Convert(
                    colliderBlobInstances, 
                    null, 
                    materialTemplate.GetMaterial(), 
                    materialTemplate.GetFilter(), 
                    convexRadius, 
                    0, 
                    0, 
                    true, 
                    true);

                List<PhysicsShapeAuthoring> shapes = new List<PhysicsShapeAuthoring>();
                gameObject.GetComponentsInChildren(shapes);

                shapes.Convert(colliderBlobInstances, null, 0, 0, 0, true, true);

                if (colliderBlobInstances.Length > 0)
                {
                    using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
                    {
                        writer.SerializeColliderBlobInstances(colliderBlobInstances.AsArray());
                    }
                }
            }
            colliderBlobInstances.Dispose();

            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("Window/ZG/Physics Editor")]
        public static void GetWindow()
        {
            GetWindow<PhysicsEditor>();
        }

        void OnGUI()
        {
            materialTemplate = EditorGUILayout.ObjectField("Physics Material Template", materialTemplate, typeof(PhysicsMaterialTemplate), false) as PhysicsMaterialTemplate;
        }
    }
}