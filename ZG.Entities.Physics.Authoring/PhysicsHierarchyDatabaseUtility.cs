using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Authoring;
using UnityEngine;

namespace ZG
{
    public static class PhysicsHierarchyDatabaseUtility
    {
#if UNITY_EDITOR
        public static void Create(
            IPhysicsHierarchyShape result,
            Transform transform,
            Transform root,
            ref List<UnityEngine.Collider> colliders,
            ref List<PhysicsShapeAuthoring> shapes,
            ref List<PhysicsHierarchyDatabase.Data.Shape> shapeResults,
            ref List<BlobAssetReference<Unity.Physics.Collider>> colliderResults,
            ref List<int> inactiveShapeIndices)
        {
            if (colliders == null)
                colliders = new List<UnityEngine.Collider>();

            var collidersTemp = transform.GetComponents<UnityEngine.Collider>();
            if (collidersTemp != null && collidersTemp.Length > 0)
                colliders.AddRange(collidersTemp);

            if (shapes == null)
                shapes = new List<PhysicsShapeAuthoring>();

            var shapesTemp = transform.GetComponents<PhysicsShapeAuthoring>();
            if (shapesTemp != null && shapesTemp.Length > 0)
                shapes.AddRange(shapesTemp);

            int i, numShapes;
            IPhysicsHierarchyShape childShape;
            List<UnityEngine.Collider> childColliders;
            List<PhysicsShapeAuthoring> childShapes;
            foreach (Transform child in transform)
            {
                childShape = child.GetComponent<IPhysicsHierarchyShape>();
                if (childShape == null)
                {
                    childColliders = colliders;
                    childShapes = shapes;
                }
                else
                {
                    childColliders = null;
                    childShapes = null;
                }

                Create(
                    childShape,
                    child,
                    root,
                    ref childColliders,
                    ref childShapes,
                    ref shapeResults,
                    ref colliderResults,
                    ref inactiveShapeIndices);
            }

            if (result != null)
            {
                int inactiveShapeIndex = transform.gameObject.IsActiveIn(root) ? -1 : (shapeResults == null ? 0 : shapeResults.Count);

                using (var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.TempJob))
                {
                    List<PhysicsHierarchyDatabase.Data.Trigger> triggers = null;
                    PhysicsHierarchyDatabase.Data.Trigger trigger;
                    trigger.index = 0;
                    //trigger.flag = result.triggerFlag;
                    trigger.contactTolerance = result.contactTolerance;

                    string tag;

                    var collisionFilter = result.collisionFilter;
                    if (colliders != null)
                    {
                        colliders.Convert(
                            colliderBlobInstances,
                            root,
                            result.material,
                            collisionFilter,
                            0.0f,
                            0,
                            0,
                            false,
                            false);

                        foreach (var collider in colliders)
                        {
                            if (collider.isTrigger)
                            {
                                trigger.name = collider.name;

                                tag = collider.tag;

                                trigger.tag = tag == "Untagged" ? string.Empty : tag;

                                if (triggers == null)
                                    triggers = new List<PhysicsHierarchyDatabase.Data.Trigger>();

                                triggers.Add(trigger);
                            }

                            //Destroy(collider);

                            ++trigger.index;
                        }
                    }

                    if (shapes != null)
                    {
                        shapes.Convert(
                           colliderBlobInstances,
                           root,
                           collisionFilter.GroupIndex,
                           0,
                           0,
                           false,
                           false);

                        foreach (var shape in shapes)
                        {
                            if (shape.CollisionResponse == CollisionResponsePolicy.RaiseTriggerEvents)
                            {
                                trigger.name = shape.name;

                                tag = shape.tag;

                                trigger.tag = tag == "Untagged" ? string.Empty : tag;

                                if (triggers == null)
                                    triggers = new List<PhysicsHierarchyDatabase.Data.Trigger>();

                                triggers.Add(trigger);
                            }

                            //Destroy(shape);

                            ++trigger.index;
                        }
                    }

                    int numColliderBlobInstances = colliderBlobInstances.Length;

                    PhysicsHierarchyDatabase.Data.Shape shapeResult;
                    shapeResult.name = result.name;
                    shapeResult.colliders = new PhysicsHierarchyDatabase.Data.Collider[numColliderBlobInstances];

                    for (i = 0; i < numColliderBlobInstances; ++i)
                    {
                        ref var collider = ref shapeResult.colliders[i];
                        ref readonly var colliderBlobInstance = ref colliderBlobInstances.ElementAt(i);

                        if (colliderResults == null)
                            colliderResults = new List<BlobAssetReference<Unity.Physics.Collider>>();

                        collider.index = colliderResults.Count;
                        collider.position = colliderBlobInstance.CompoundFromChild.pos;
                        collider.rotation = colliderBlobInstance.CompoundFromChild.rot;

                        colliderResults.Add(colliderBlobInstance.Collider);
                    }

                    shapeResult.triggers = triggers == null ? null : triggers.ToArray();

                    if (shapeResults == null)
                        shapeResults = new List<PhysicsHierarchyDatabase.Data.Shape>();

                    shapeResults.Add(shapeResult);
                }

                if (inactiveShapeIndex != -1 && shapeResults != null)
                {
                    numShapes = shapeResults.Count;
                    if (numShapes > 0)
                    {
                        if (inactiveShapeIndices == null)
                            inactiveShapeIndices = new List<int>();

                        for (i = inactiveShapeIndex; i < numShapes; ++i)
                            inactiveShapeIndices.Add(i);
                    }
                }
            }
        }

        /*public static void Create(
            Transform root,
            ref List<PhysicsHierarchyDatabase.Data.Shape> shapeResults,
            ref List<BlobAssetReference<Unity.Physics.Collider>> colliderResults,
            ref List<int> inactiveShapeIndices)
        {
            if (root == null)
                return;

            var result = root.GetComponent<IPhysicsHierarchyShape>();
            if (result == null)
            {
                foreach (Transform child in root)
                    Create(
                        child,
                        ref shapeResults,
                        ref colliderResults,
                        ref inactiveShapeIndices);
            }
            else
            {
                List<UnityEngine.Collider> colliders = null;
                List<PhysicsShapeAuthoring> shapes = null;

                Create(
                    result,
                    root,
                    root,
                    ref colliders,
                    ref shapes,
                    ref shapeResults,
                    ref colliderResults,
                    ref inactiveShapeIndices);
            }
        }*/

        public static void Create(this PhysicsHierarchyDatabase database)
        {
            List<PhysicsHierarchyDatabase.Data.Shape> shapeResults = null;
            List<BlobAssetReference<Unity.Physics.Collider>> colliderResults = null;
            List<int> inactiveShapeIndices = null; 
            List<UnityEngine.Collider> colliders = null;
            List<PhysicsShapeAuthoring> shapes = null;
            Create(
                database.root.GetComponent<IPhysicsHierarchyShape>(),
                database.root,
                database.root,
                ref colliders,
                ref shapes,
                ref shapeResults,
                ref colliderResults,
                ref inactiveShapeIndices);
            /*Create(
                database.root,
                ref shapeResults,
                ref colliderResults,
                ref inactiveShapeIndices);*/

            database.data.shapes = shapeResults == null ? null : shapeResults.ToArray();

            database.Create(colliderResults == null ? null : colliderResults.ToArray(),
                inactiveShapeIndices == null ? null : inactiveShapeIndices.ToArray());
        }
#endif
    }
}