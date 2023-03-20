using System.Reflection;
using System.IO;
using Unity.Physics;
using UnityEngine;
using UnityEditor;

namespace ZG
{
    [CustomPropertyDrawer(typeof(PhysicsShapesAttribute))]
    public class PhysicsShapesDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            float width = position.width;
            position.width = width * 0.75f;

            bool isChanged = EditorGUI.PropertyField(position, property);

            position.x += position.width;
            position.width = width * 0.25f;
            isChanged = GUI.Button(position, "Reload") || isChanged;

            if (!isChanged)
                return;

            var transform = property.objectReferenceValue as Transform;
            if (transform == null)
                return;

            var serializedObject = property.serializedObject;
            property = serializedObject.FindProperty(((PhysicsShapesAttribute)attribute).path);

            string propertyPath = property.propertyPath, path;
            propertyPath = EditorHelper.GetPropertyPath(propertyPath);

            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);
            using (var colliderBlobInstances = new Unity.Collections.NativeList<CompoundCollider.ColliderBlobInstance>(Unity.Collections.Allocator.TempJob))
            {
                PhysicsEditor.Convert(true, 0, transform, colliderBlobInstances);

                writer.SerializeColliderBlobInstances(colliderBlobInstances.AsArray());
            }

            memoryStream.Close();
            var bytes = memoryStream.ToArray();
            memoryStream.Dispose();

            object parent;
            FieldInfo fieldInfo;
            foreach (var targetObject in serializedObject.targetObjects)
            {
                path = propertyPath;
                targetObject.Get(ref path, out fieldInfo, out parent);
                fieldInfo.SetValue(parent, bytes);

                EditorUtility.SetDirty(targetObject);
            }

            serializedObject.Update();
        }
    }
}