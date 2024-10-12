using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEditor;

namespace ZG
{
    public class PhysicsComponentEditor : Editor
    {
        static readonly Color k_ShapeHandleColor = new Color32(145, 244, 139, 210);
        static readonly Color k_ShapeHandleColorDisabled = new Color32(84, 200, 77, 140);

        public void OnSceneGUI()
        {
            if (Event.current.GetTypeForControl(GUIUtility.hotControl) != EventType.Repaint)
                return;

            var physicsComponent = target as IPhysicsComponent;
            if (!physicsComponent.gameObjectEntity.isCreated)
                return;

            var handleColor = physicsComponent.enabled ? k_ShapeHandleColor : k_ShapeHandleColorDisabled;
            var handleMatrix = math.float4x4(physicsComponent.GetTransform());
            using (new Handles.DrawingScope(handleColor, handleMatrix))
            {
                if (physicsComponent.TryGetComponentData(out PhysicsCollider collider) && collider.IsValid)
                {
                    PhysicsDrawingUtility.Draw(collider.Value, RigidTransform.identity);
                }
            }

            __shapeChildEntities.Clear();
            WriteOnlyListWrapper<PhysicsShapeChildEntity, List<PhysicsShapeChildEntity>> wrapper;
            if (physicsComponent
                .TryGetBuffer<PhysicsShapeChildEntity, List<PhysicsShapeChildEntity>,
                    WriteOnlyListWrapper<PhysicsShapeChildEntity, List<PhysicsShapeChildEntity>>>(
                    ref __shapeChildEntities, ref wrapper))
            {
                foreach (var shapeChildEntity in __shapeChildEntities)
                {
                    if (physicsComponent.TryGetComponentData(shapeChildEntity.value, out Translation translation) &&
                        physicsComponent.TryGetComponentData(shapeChildEntity.value, out Rotation rotation))
                    {
                        handleMatrix = math.float4x4(math.RigidTransform(rotation.Value, translation.Value));
                        using (new Handles.DrawingScope(handleColor, handleMatrix))
                        {
                            if (physicsComponent.TryGetComponentData(shapeChildEntity.value, out PhysicsCollider collider))
                                PhysicsDrawingUtility.Draw(collider.Value, RigidTransform.identity);
                        }
                    }
                }
            }
        }

        private static List<PhysicsShapeChildEntity> __shapeChildEntities = new List<PhysicsShapeChildEntity>();
    }

    [CustomEditor(typeof(PhysicsShapeComponent))]
    public class PhysicsShapeComponentEditor : PhysicsComponentEditor
    {
    }

    [CustomEditor(typeof(PhysicsColliderComponent))]
    public class PhysicssColliderComponentEditor : PhysicsComponentEditor
    {
    }

    [CustomEditor(typeof(PhysicsHierarchyComponent))]
    public class PhysicsHierarchyComponentEditor : PhysicsComponentEditor
    {
    }
}