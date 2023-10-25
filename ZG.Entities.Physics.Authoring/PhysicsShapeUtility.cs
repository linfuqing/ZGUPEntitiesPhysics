using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Authoring;
using UnityEngine;
using Material = Unity.Physics.Material;

namespace ZG
{
    public static class PhysicsShapeUtility
    {
        public static Material GetMaterial(this PhysicsMaterialTemplate materialTemplate)
        {
            if (materialTemplate == null)
                return Material.Default;

            Material material = Material.Default;
            material.CollisionResponse = materialTemplate.CollisionResponse;
            material.FrictionCombinePolicy = materialTemplate.Friction.CombineMode;
            material.RestitutionCombinePolicy = materialTemplate.Restitution.CombineMode;
            material.CustomTags = materialTemplate.CustomTags.Value;
            material.Friction = materialTemplate.Friction.Value;
            material.Restitution = materialTemplate.Restitution.Value;

            return material;
        }

        public static Material GetMaterial(this PhysicsShapeAuthoring shape)
        {
            Material material = Material.Default;
            material.CollisionResponse = shape.CollisionResponse;
            material.FrictionCombinePolicy = shape.Friction.CombineMode;
            material.RestitutionCombinePolicy = shape.Restitution.CombineMode;
            material.CustomTags = shape.CustomTags.Value;
            material.Friction = shape.Friction.Value;
            material.Restitution = shape.Restitution.Value;

            return material;
        }
        
        public static CollisionFilter GetFilter(this PhysicsShapeAuthoring shape, int groupIndex)
        {
            CollisionFilter collisionFilter;
            collisionFilter.GroupIndex = groupIndex;
            collisionFilter.BelongsTo = shape.BelongsTo.Value;
            collisionFilter.CollidesWith = shape.CollidesWith.Value;

            return collisionFilter;
        }

        public static CollisionFilter GetFilter(this PhysicsMaterialTemplate materialTemplate, int groupIndex = 0)
        {
            if (materialTemplate == null)
                return CollisionFilter.Default;

            CollisionFilter collisionFilter;
            collisionFilter.GroupIndex = groupIndex;
            collisionFilter.BelongsTo = materialTemplate.BelongsTo.Value;
            collisionFilter.CollidesWith = materialTemplate.CollidesWith.Value;

            return collisionFilter;
        }

        public static float3 GetLocalCenter(this PhysicsShapeAuthoring shape)
        {
            switch (shape.ShapeType)
            {
                case ShapeType.Box:
                    {
                        var geometry = shape.GetBoxProperties();

                        return geometry.Center;
                    }
                case ShapeType.Capsule:
                    {
                        var authoring = shape.GetCapsuleProperties();

                        return authoring.Center;
                    }
                case ShapeType.Sphere:
                    {
                        var geometry = shape.GetSphereProperties(out var orientation);

                        return geometry.Center;
                    }
                case ShapeType.Cylinder:
                    {
                        var geometry = shape.GetCylinderProperties();

                        return geometry.Center;
                    }
                case ShapeType.Plane:
                    {
                        shape.GetPlaneProperties(out var center, out var size, out var orientation);

                        return center;
                    }
                case ShapeType.ConvexHull:
                    {
                        using (var pointCloud = new NativeList<float3>(65535, Allocator.TempJob))
                        {
                            shape.GetConvexHullProperties(pointCloud);

                            int numPoints = pointCloud.Length;
                            if (numPoints == 0)
                            {
                                throw new InvalidOperationException(
                                    $"No vertices associated with {shape.name}. Add a {typeof(MeshFilter)} component or assign a readable PhysicsShapeAuthoring.CustomMesh."
                                );
                            }

                            float3 center = pointCloud[0];
                            for (int i = 1; i < numPoints; ++i)
                                center += pointCloud[i];

                            center /= numPoints;

                            return center;
                        }
                    }
                case ShapeType.Mesh:
                    {
                        const int defaultVertexCount = 2048;
                        using (var vertices = new NativeList<float3>(defaultVertexCount, Allocator.TempJob))
                        using (var triangles = new NativeList<int3>(defaultVertexCount - 2, Allocator.TempJob))
                        {
                            shape.GetMeshProperties(vertices, triangles);

                            if (vertices.Length == 0 || triangles.Length == 0)
                            {
                                throw new InvalidOperationException(
                                    $"Invalid mesh data associated with {shape.name}. " +
                                    $"Add a {typeof(MeshFilter)} component or assign a PhysicsShapeAuthoring.CustomMesh. " +
                                    "Ensure that you have enabled Read/Write on the mesh's import settings."
                                );
                            }

                            int numVertices = vertices.Length;
                            float3 center = vertices[0];
                            for (int i = 1; i < numVertices; ++i)
                                center += vertices[i];

                            center /= numVertices;

                            return center;
                        }
                    }
                default:
                    throw new InvalidOperationException();
            }
        }

        public static float3 GetWorldCenter(this PhysicsShapeAuthoring shape, bool isBaked)
        {
            float3 center = GetLocalCenter(shape);

            if (isBaked)
                return shape.transform.TransformPoint(center);

            return math.transform(Unity.Physics.Math.DecomposeRigidBodyTransform(shape.transform.localToWorldMatrix), center);
        }

        public static void Convert(
            this PhysicsShapeAuthoring shape, 
            bool isBake,
            int groupIndex,
            NativeArray<EntityTransform> hierarchy, 
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            switch (shape.ShapeType)
            {
                case ShapeType.Box:
                    {
                        var geometry = shape.GetBoxProperties();

                        geometry.CreateCollider(
                            isBake, 
                            GetFilter(shape, groupIndex), 
                            GetMaterial(shape), 
                            hierarchy, 
                            colliders);

                        break;
                    }
                case ShapeType.Capsule:
                    {
                        var authoring = shape.GetCapsuleProperties();

                        UnityEngine.Assertions.Assert.IsTrue(authoring.Height > math.FLT_MIN_NORMAL, shape.name);

                        authoring.CreateCollider(
                            isBake, 
                            GetFilter(shape, groupIndex), 
                            GetMaterial(shape),
                            hierarchy, 
                            colliders);

                        break;
                    }
                case ShapeType.Sphere:
                    {
                        var geometry = shape.GetSphereProperties(out var orientation);

                        geometry.CreateCollider(
                            isBake, 
                            orientation, 
                            GetFilter(shape, groupIndex), 
                            GetMaterial(shape),
                            hierarchy, 
                            colliders);

                        break;
                    }
                case ShapeType.Cylinder:
                    {
                        var geometry = shape.GetCylinderProperties();

                        geometry.CreateCollider(
                            isBake, 
                            GetFilter(shape, groupIndex), 
                            GetMaterial(shape),
                            hierarchy, 
                            colliders);

                        break;
                    }
                case ShapeType.Plane:
                    {
                        shape.GetPlaneProperties(out var center, out var size, out var orientation);

                        PhysicsGeometryUtility.CreateCollider(
                            isBake, 
                            size, 
                            center, 
                            orientation, 
                            GetFilter(shape, groupIndex), 
                            GetMaterial(shape),
                            hierarchy, 
                            colliders);

                        break;
                    }
                case ShapeType.ConvexHull:
                    {
                        using (var pointCloud = new NativeList<float3>(65535, Allocator.TempJob))
                        {
                            shape.GetConvexHullProperties(pointCloud);

                            if (pointCloud.Length == 0)
                            {
                                throw new InvalidOperationException(
                                    $"No vertices associated with {shape.name}. Add a {typeof(MeshFilter)} component or assign a readable PhysicsShapeAuthoring.CustomMesh."
                                );
                            }

                            float4x4 localToRoot = isBake ? pointCloud.AsArray().Bake(hierarchy) : hierarchy.CalculateLocalToRoot();

                            PhysicsGeometryUtility.CreateCollider(
                                localToRoot, 
                                pointCloud.AsArray(), 
                                shape.ConvexHullGenerationParameters, 
                                GetFilter(shape, groupIndex), 
                                GetMaterial(shape), 
                                colliders);
                        }

                        break;
                    }
                case ShapeType.Mesh:
                    {
                        const int defaultVertexCount = 2048;
                        using (var vertices = new NativeList<float3>(defaultVertexCount, Allocator.TempJob))
                        using (var triangles = new NativeList<int3>(defaultVertexCount - 2, Allocator.TempJob))
                        {
                            shape.GetMeshProperties(vertices, triangles);

                            if (vertices.Length == 0 || triangles.Length == 0)
                            {
                                throw new InvalidOperationException(
                                    $"Invalid mesh data associated with {shape.name}. " +
                                    $"Add a {typeof(MeshFilter)} component or assign a PhysicsShapeAuthoring.CustomMesh. " +
                                    "Ensure that you have enabled Read/Write on the mesh's import settings."
                                );
                            }

                            float4x4 localToRoot = isBake ? vertices.AsArray().Bake(hierarchy) : hierarchy.CalculateLocalToRoot();
                            PhysicsGeometryUtility.CreateCollider(localToRoot, vertices.AsArray(), triangles.AsArray(), GetFilter(shape, groupIndex), GetMaterial(shape), colliders);
                        }

                        break;
                    }
                default:
                    throw new InvalidOperationException();
            }
        }

        public static void Convert(
            this  IList<PhysicsShapeAuthoring> inputs,
            in NativeList<CompoundCollider.ColliderBlobInstance> colliders,
            Transform root,
            int groupIndex,
            int startIndex, 
            int count, 
            bool isBaked
#if UNITY_EDITOR
            , bool isShowProgressBar
#endif
            )
        {
            int remainingCount = inputs.Count - startIndex;
            count = count == 0 ? remainingCount : Mathf.Min(count, remainingCount);

            using (var hierarchy = new NativeList<EntityTransform>(Allocator.TempJob))
            {
                PhysicsGroup group;
                PhysicsShapeAuthoring input;
                for (int i = 0; i < count; ++i)
                {
                    input = inputs[i];
#if UNITY_EDITOR
                    if (isShowProgressBar && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Shapes", input.name + '(' + i + '/' + count + ')', i * 1.0f / count))
                        break;
#endif
                    hierarchy.Clear();
                    input.transform.GetHierarchy(root, hierarchy);

                    group = input.GetComponent<PhysicsGroup>();
                    Convert(input, isBaked, group == null ? groupIndex : group.index, hierarchy.AsArray(), colliders);
                }
            }

#if UNITY_EDITOR
            UnityEditor.EditorUtility.ClearProgressBar();
#endif
        }

    }
}