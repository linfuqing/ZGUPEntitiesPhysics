using System;
using System.Collections.Generic;
using System.IO;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using Unity.Physics.Authoring;
using Unity.Burst;
using Unity.Jobs;
using Math = Unity.Physics.Math;

namespace ZG
{
    public static class LegacyColliderUtility
    {
        [Flags]
        public enum LayerFlag : byte
        {
            Trigger = 32,
            Convex = 64,
        }

        public struct BoxColliderData : IEquatable<BoxColliderData>
        {
            public byte layer;
            public float3 size;
            public float3 center;

            public BoxGeometry Convert(float bevelRadius)
            {
                BoxGeometry result = default;
                result.BevelRadius = bevelRadius;
                result.Size = size;
                result.Center = center;
                result.Orientation = quaternion.identity;
                return result;
            }

            public bool Equals(BoxColliderData other)
            {
                return layer == other.layer && size.Equals(other.size) && center.Equals(other.center);
            }
        }

        public struct SphereColliderData : IEquatable<SphereColliderData>
        {
            public byte layer;
            public float radius;
            public float3 center;

            public SphereGeometry Convert()
            {
                SphereGeometry result = default;
                result.Radius = radius;
                result.Center = center;
                return result;
            }

            public bool Equals(SphereColliderData other)
            {
                return layer == other.layer && radius == other.radius && center.Equals(other.center);
            }
        }

        public struct CapsuleColliderData : IEquatable<CapsuleColliderData>
        {
            public byte layer;
            public float radius;
            public float3 x;
            public float3 y;

            public CapsuleGeometry Convert()
            {
                CapsuleGeometry result = default;
                result.Radius = radius;
                result.Vertex0 = x;
                result.Vertex1 = y;
                return result;
            }

            public bool Equals(CapsuleColliderData other)
            {
                return layer == other.layer && radius == other.radius && x.Equals(other.x) && y.Equals(other.y);
            }
        }

        private struct MeshInfo : IEquatable<MeshInfo>
        {
            public byte layer;
            public int index;
            public float3 scale;

            public bool Equals(MeshInfo other)
            {
                return layer == other.layer && index == other.index && scale.Equals(other.scale);
            }
        }

        private struct MeshKey : IEquatable<MeshKey>
        {
            public byte layer;
            public float3 scale;

            public bool Equals(MeshKey other)
            {
                return layer == other.layer && scale.Equals(other.scale);
            }
        }

        private struct MeshValue : IDisposable
        {
            public NativeList<float3> vertices;

            public NativeList<int> indices;

            public void Dispose()
            {
                vertices.Dispose();

                if (indices.IsCreated)
                    indices.Dispose();
            }
        }

        private struct EqualityComparer : IEqualityComparer<float3>
        {
            public float maxDistanceSquare;

            public bool Equals(float3 x, float3 y)
            {
                return math.distancesq(x, y) <= maxDistanceSquare;
            }

            public int GetHashCode(float3 obj)
            {
                return obj.GetHashCode();
            }
        }

        public static void Convert(
            this UnityEngine.Collider collider,
            bool isBaked,
            float convexRadius,
            CollisionFilter filter,
            Unity.Physics.Material material,
            NativeArray<EntityTransform> hierarchy, 
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            if (filter.BelongsTo == 0)
            {
                GameObject gameObject = collider.gameObject;
                if (gameObject != null)
                    filter.BelongsTo = (uint)(1 << gameObject.layer);
            }

            UnityEngine.Assertions.Assert.IsFalse(filter.IsEmpty, $"Empty Filter: {collider.name} In {collider.transform.name}");

            var boxCollider = collider as UnityEngine.BoxCollider;
            if (boxCollider != null)
            {
                BoxGeometry geometry = default;
                geometry.BevelRadius = convexRadius;
                geometry.Center = boxCollider.center;
                geometry.Size = boxCollider.size;
                geometry.Orientation = quaternion.identity;

                geometry.CreateCollider(
                    isBaked, 
                    filter, 
                    material,
                    hierarchy, 
                    colliders);

                return;
            }

            var sphereCollider = collider as UnityEngine.SphereCollider;
            if (sphereCollider != null)
            {
                SphereGeometry geometry = default;
                geometry.Center = sphereCollider.center;
                geometry.Radius = sphereCollider.radius;

                geometry.CreateCollider(
                    isBaked, 
                    quaternion.identity, 
                    filter, 
                    material,
                    hierarchy,
                    colliders);

                return;
            }

            var capsuleCollider = collider as UnityEngine.CapsuleCollider;
            if (capsuleCollider != null)
            {
                PhysicsGeometryUtility.CreateCollider(
                    isBaked, 
                    capsuleCollider.direction, 
                    capsuleCollider.height, 
                    capsuleCollider.radius, 
                    capsuleCollider.center, 
                    filter, 
                    material,
                    hierarchy,
                    colliders);

                return;
            }

            var meshCollider = collider as UnityEngine.MeshCollider;
            var mesh = meshCollider == null ? null : meshCollider.sharedMesh;
            if (mesh != null)
            {
                using (var pointCloud = new NativeArray<float3>(mesh.vertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                {
                    pointCloud.Reinterpret<Vector3>().CopyFrom(mesh.vertices);

                    var localToRoot = isBaked ? pointCloud.Bake(hierarchy) : hierarchy.CalculateLocalToRoot();
                    if (meshCollider.convex)
                    {
                        var parameters = ConvexHullGenerationParameters.Default;
                        parameters.BevelRadius = convexRadius;

                        PhysicsGeometryUtility.CreateCollider(localToRoot, pointCloud, parameters, filter, material, colliders);

                        return;
                    }

                    var indices = mesh.triangles;
                    int numIndices = indices.Length;
                    using (var triangles = new NativeArray<int3>(numIndices / 3, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                    {
                        triangles.Slice().SliceConvert<int>().CopyFrom(indices);

                        PhysicsGeometryUtility.CreateCollider(localToRoot, pointCloud, triangles, filter, material, colliders);

                        return;
                    }
                }
            }

            throw new InvalidOperationException();
        }

        public static void Convert(
            this IList<UnityEngine.Collider> inputs,
            NativeList<CompoundCollider.ColliderBlobInstance> outputs,
            Transform root,
            Unity.Physics.Material material,
            CollisionFilter filter,
            float convexRadius, 
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
                UnityEngine.Collider input;
                for(int i = 0; i < count; ++i)
                {
                    input = inputs[i + startIndex];

#if UNITY_EDITOR
                    if (isShowProgressBar && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Colliders", input.name + '(' + i + '/' + count + ')', i * 1.0f / count))
                        break;
#endif
                    material.CollisionResponse = input.isTrigger ? CollisionResponsePolicy.RaiseTriggerEvents : CollisionResponsePolicy.Collide;

                    hierarchy.Clear();
                    input.transform.GetHierarchy(root, hierarchy);

                    try
                    {
                        Convert(
                            input,
                            isBaked,
                            convexRadius,
                            filter,
                            material,
                            hierarchy.AsArray(),
                            outputs);
                    }
                    catch(Exception e)
                    {
                        Debug.LogException(e.InnerException ?? e, input);
                    }
                }
            }

#if UNITY_EDITOR
            UnityEditor.EditorUtility.ClearProgressBar();
#endif
        }

        public unsafe static int Serialize(
            this BinaryWriter writer, 
#if UNITY_EDITOR
            ICollection
#else
            IEnumerable
#endif
            <UnityEngine.Collider> colliders,
            float maxVertexDistanceSquare, 
            bool isCombineMesh
#if UNITY_EDITOR
            , bool isShowProgressBar
#endif
            )
        {
            int length = 0;
#if UNITY_EDITOR
            try
            {
                int count = colliders.Count, index = 0;
#endif
                byte layer;
                float4 worldCenter;
                RigidTransform shapeFromWorld;
                Transform transform;
                UnityEngine.Mesh mesh;
                MeshInfo meshInfo;
                BoxColliderData boxColliderData;
                SphereColliderData sphereColliderData;
                CapsuleColliderData capsuleColliderData;
                UnityEngine.BoxCollider boxCollider;
                UnityEngine.SphereCollider sphereCollider;
                UnityEngine.CapsuleCollider capsuleCollider;
                var boxColliders = new NativeParallelMultiHashMap<BoxColliderData, RigidTransform>(1, Allocator.Temp);
                var sphereColliders = new NativeParallelMultiHashMap<SphereColliderData, RigidTransform>(1, Allocator.Temp);
                var capsuleColliders = new NativeParallelMultiHashMap<CapsuleColliderData, RigidTransform>(1, Allocator.Temp);
                var meshInfos = new NativeParallelMultiHashMap<MeshInfo, RigidTransform>(1, Allocator.Temp);
                List<UnityEngine.Mesh> meshes = null;
                Dictionary<UnityEngine.Mesh, int> meshIndices = null;
                foreach (var collider in colliders)
                {
#if UNITY_EDITOR
                    ++index;

                    if (isShowProgressBar && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Colliders", collider.name + '(' + index + '/' + count + ')', index * 1.0f / count))
                        break;
#endif
                    layer = (byte)collider.gameObject.layer;
                    if (collider.isTrigger)
                        layer |= (byte)LayerFlag.Trigger;

                    transform = collider.transform;

                    shapeFromWorld = math.RigidTransform(transform.rotation, transform.position);

                    boxCollider = collider as UnityEngine.BoxCollider;
                    if (boxCollider != null)
                    {
                        worldCenter = math.mul(transform.localToWorldMatrix, new float4(boxCollider.center, 1.0f));
                        boxColliderData.layer = layer;
                        boxColliderData.center = math.mul(math.inverse(math.float4x4(shapeFromWorld)), worldCenter).xyz;
                        boxColliderData.size = math.abs(boxCollider.size * (float3)transform.lossyScale);
                        boxColliders.Add(boxColliderData, shapeFromWorld);
                    }

                    sphereCollider = collider as UnityEngine.SphereCollider;
                    if (sphereCollider != null)
                    {
                        worldCenter = math.mul(transform.localToWorldMatrix, new float4(sphereCollider.center, 1f));
                        sphereColliderData.layer = layer;
                        sphereColliderData.center = math.mul(math.inverse(math.float4x4(shapeFromWorld)), worldCenter).xyz;
                        sphereColliderData.radius = sphereCollider.radius * math.cmax(math.abs(transform.lossyScale));
                        sphereColliders.Add(sphereColliderData, shapeFromWorld);
                    }

                    capsuleCollider = collider as UnityEngine.CapsuleCollider;
                    if (capsuleCollider != null)
                    {
                        var linearScalar = math.abs(transform.lossyScale);

                        var direction = capsuleCollider.direction;
                        // radius is max of the two non-height axes
                        var radius = capsuleCollider.radius * math.cmax(new float3(linearScalar) { [direction] = 0f });

                        var ax = new float3 { [direction] = 1f };
                        var vertex = ax * (0.5f * capsuleCollider.height);
                        var center = capsuleCollider.center;
                        worldCenter = math.mul(transform.localToWorldMatrix, new float4(center, 0f));
                        var offset = math.mul(math.inverse(new float4x4(shapeFromWorld)), worldCenter).xyz - center * linearScalar;

                        capsuleColliderData.layer = layer;
                        capsuleColliderData.radius = radius;
                        capsuleColliderData.x = offset + ((float3)center + vertex) * math.abs(linearScalar) - ax * radius;
                        capsuleColliderData.y = offset + ((float3)center - vertex) * math.abs(linearScalar) + ax * radius;

                        capsuleColliders.Add(capsuleColliderData, shapeFromWorld);
                    }

                    var meshCollider = collider as UnityEngine.MeshCollider;
                    mesh = meshCollider == null ? null : meshCollider.sharedMesh;
                    if (mesh != null)
                    {
                        if (meshCollider.convex)
                            layer |= (byte)LayerFlag.Convex;

                        meshInfo.layer = layer;

                        if (meshIndices == null)
                            meshIndices = new Dictionary<UnityEngine.Mesh, int>();

                        if (!meshIndices.TryGetValue(mesh, out meshInfo.index))
                        {
                            if (meshes == null)
                                meshes = new List<UnityEngine.Mesh>();

                            meshInfo.index = meshes.Count;
                            meshIndices[mesh] = meshInfo.index;

                            meshes.Add(mesh);
                        }

                        meshInfo.scale = transform.lossyScale;

                        meshInfos.Add(meshInfo, shapeFromWorld);
                    }
                }
                
                length = boxColliders.Count() + sphereColliders.Count() + capsuleColliders.Count();

                writer.Serialize(boxColliders);
                writer.Serialize(sphereColliders);
                writer.Serialize(capsuleColliders);

                boxColliders.Dispose();
                sphereColliders.Dispose();
                capsuleColliders.Dispose();

#if UNITY_EDITOR
                if (index < count)
                    return 0;
#endif

                var indices = new NativeList<int>(Allocator.Temp);
                var points = new NativeList<float3>(Allocator.Temp);
                var transforms = new NativeList<RigidTransform>(Allocator.Temp);
                var keys = meshInfos.GetKeyArray(Allocator.Temp);

                Dictionary<MeshKey, MeshValue> meshMap = null;

                int[] triangles;
                
                int numKeys = keys.ConvertToUniqueArray(), indexOffset;
                MeshKey meshKey;
                MeshValue meshValue;

                EqualityComparer equalityComparer;
                equalityComparer.maxDistanceSquare = maxVertexDistanceSquare;

                List<Vector3> vertices = null;
#if UNITY_EDITOR
                index = 0;
                count = numKeys;
#endif
                for(int i = 0; i < numKeys; ++i)
                {
                    meshInfo = keys[i];
                    mesh = meshes[meshInfo.index];

#if UNITY_EDITOR
                    ++index;

                    if (isShowProgressBar && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Mesh Colliders", mesh.name.ToString() + '(' + index + '/' + count + ')', index * 1.0f / count))
                        break;
#endif

                    if (vertices == null)
                        vertices = new List<Vector3>();

                    mesh.GetVertices(vertices);

                    if (isCombineMesh)
                    {
                        if (meshInfos.TryGetFirstValue(meshInfo, out shapeFromWorld, out var iterator))
                        {
                            meshKey.layer = meshInfo.layer;
                            meshKey.scale = meshInfo.scale;

                            if (meshMap == null)
                                meshMap = new Dictionary<MeshKey, MeshValue>();

                            if (!meshMap.TryGetValue(meshKey, out meshValue))
                            {
                                if (((LayerFlag)meshInfo.layer & LayerFlag.Convex) != LayerFlag.Convex)
                                    meshValue.indices = new NativeList<int>(Allocator.Temp);

                                meshValue.vertices = new NativeList<float3>(Allocator.Temp);

                                meshMap[meshKey] = meshValue;
                            }

                            triangles = ((LayerFlag)meshInfo.layer & LayerFlag.Convex) == LayerFlag.Convex ? null : mesh.triangles;

                            do
                            {
                                if (triangles != null)
                                {
                                    indexOffset = meshValue.vertices.Length;

                                    foreach (var triangle in triangles)
                                        meshValue.indices.Add(triangle + indexOffset);
                                }

                                foreach (var vertex in vertices)
                                    meshValue.vertices.Add(math.transform(shapeFromWorld, vertex * meshInfo.scale));

                            } while (meshInfos.TryGetNextValue(out shapeFromWorld, ref iterator));
                        }
                    }
                    else
                    {
                        writer.Write(meshInfo.layer);

                        points.Clear();
                        foreach (var vertex in vertices)
                            points.Add(vertex * meshInfo.scale);

                        if (((LayerFlag)meshInfo.layer & LayerFlag.Convex) == LayerFlag.Convex)
                        {
                            if (maxVertexDistanceSquare > math.FLT_MIN_NORMAL)
                            {
                                var pointCloud = points.AsArray();
                                writer.Serialize(pointCloud, pointCloud.ConvertToUniqueArray(equalityComparer));
                            }
                            else
                                writer.Serialize(points.AsArray());
                        }
                        else
                        {
                            triangles = mesh.triangles;
                            if (maxVertexDistanceSquare > math.FLT_MIN_NORMAL)
                            {
                                using (var indexMap = new NativeArray<int>(points.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                                {
                                    var pointCloud = points.AsArray();
                                    writer.Serialize(pointCloud, pointCloud.ConvertToUniqueArray(equalityComparer, indexMap));

                                    indices.Clear();

                                    int numTriangles = triangles.Length, x, y, z;
                                    for (int j = 0; j < numTriangles; j += 3)
                                    {
                                        x = indexMap[triangles[j + 0]];
                                        y = indexMap[triangles[j + 1]];
                                        z = indexMap[triangles[j + 2]];

                                        if (x == y || x == z || y == z)
                                            continue;

                                        indices.Add(x);
                                        indices.Add(y);
                                        indices.Add(z);
                                    }

                                    writer.Serialize(indices.AsArray());
                                }
                            }
                            else
                            {
                                writer.Serialize(points.AsArray());

                                indices.Clear();

                                fixed (void* pointer = triangles)
                                    indices.AddRange(pointer, triangles.Length);

                                writer.Serialize(indices.AsArray());
                            }
                        }

                        transforms.Clear();
                        if (meshInfos.TryGetFirstValue(meshInfo, out shapeFromWorld, out var iterator))
                        {
                            do
                            {
                                transforms.Add(shapeFromWorld);

                            } while (meshInfos.TryGetNextValue(out shapeFromWorld, ref iterator));
                        }

                        length += transforms.Length;

                        writer.Serialize(transforms.AsArray());
                    }
                }

                meshInfos.Dispose();
                points.Dispose();
                keys.Dispose();

                if (meshMap != null
#if UNITY_EDITOR
                    && index >= count
#endif
                    )
                {
                    transforms.Clear();
                    transforms.Add(RigidTransform.identity);

#if UNITY_EDITOR
                    index = 0;
                    count = meshMap.Count;
#endif
                    foreach (var pair in meshMap)
                    {
                        layer = pair.Key.layer;

#if UNITY_EDITOR
                        ++index;

                        if (isShowProgressBar && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Combined Mesh", layer.ToString() + '(' + index + '/' + count + ')', index * 1.0f / count))
                            break;
#endif

                        writer.Write(layer);

                        meshValue = pair.Value;

                        if (((LayerFlag)layer & LayerFlag.Convex) == LayerFlag.Convex)
                        {
                            if (maxVertexDistanceSquare > math.FLT_MIN_NORMAL)
                            {
                                var pointCloud = meshValue.vertices.AsArray();
                                writer.Serialize(pointCloud, pointCloud.ConvertToUniqueArray(equalityComparer));
                            }
                            else
                                writer.Serialize(meshValue.vertices.AsArray());
                        }
                        else
                        {
                            if (maxVertexDistanceSquare > math.FLT_MIN_NORMAL)
                            {
                                using (var indexMap = new NativeArray<int>(meshValue.vertices.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                                {
                                    var pointCloud = meshValue.vertices.AsArray();
                                    writer.Serialize(pointCloud, pointCloud.ConvertToUniqueArray(equalityComparer, indexMap));

                                    indices.Clear();

                                    int numIndices = meshValue.indices.Length, x, y, z;
                                    for (int i = 0; i < numIndices; i += 3)
                                    {
                                        x = indexMap[meshValue.indices[i + 0]];
                                        y = indexMap[meshValue.indices[i + 1]];
                                        z = indexMap[meshValue.indices[i + 2]];

                                        if (x == y || x == z || y == z)
                                            continue;

                                        indices.Add(x);
                                        indices.Add(y);
                                        indices.Add(z);
                                    }

                                    writer.Serialize(indices.AsArray());
                                }
                            }
                            else
                            {
                                writer.Serialize(meshValue.vertices.AsArray());
                                
                                writer.Serialize(meshValue.indices.AsArray());
                            }
                        }

                        writer.Serialize(transforms.AsArray());

                        meshValue.Dispose();
                    }

                    length += meshMap.Count;
                }

                writer.Write((byte)255);

                indices.Dispose();
                transforms.Dispose();
#if UNITY_EDITOR
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                if (isShowProgressBar)
                    UnityEditor.EditorUtility.ClearProgressBar();
            }
#endif

            return length;
        }

        public static void DeserializeLegacyColliders(
            this BinaryReader reader,
            ref NativeList<CompoundCollider.ColliderBlobInstance> colliderBlobInstances,
            Unity.Physics.Material material,
            CollisionFilter filter, 
            float convexRadius
#if UNITY_EDITOR
            , int progressBarDisplayCount
#endif
            )
        {
            bool isAutoLayer = filter.BelongsTo == 0;
#if UNITY_EDITOR
            try
            {
                int index = 0;
#endif
                CompoundCollider.ColliderBlobInstance colliderBlobInstance;

                using (var colliders = reader.DeserializeNativeMultiHashMap<BoxColliderData, RigidTransform>(Allocator.Temp))
                {
                    using (var keys = colliders.GetKeyArray(Allocator.Temp))
                    {
                        using (var values = colliders.GetValueArray(Allocator.Temp))
                        {
                            int length = math.max(keys.Length, values.Length);
                            BoxColliderData key;
                            for (int i = 0; i < length; ++i)
                            {
                                key = keys[i];

#if UNITY_EDITOR
                                ++index;

                                if (progressBarDisplayCount > 0 && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Colliders", "Box Collider(" + index + '/' + progressBarDisplayCount + ')', index * 1.0f / progressBarDisplayCount))
                                    return;
#endif
                                if (isAutoLayer)
                                    filter.BelongsTo = 1u << (key.layer & 31);

                                material.CollisionResponse = ((LayerFlag)key.layer & LayerFlag.Trigger) == LayerFlag.Trigger ? CollisionResponsePolicy.RaiseTriggerEvents : CollisionResponsePolicy.Collide;

                                colliderBlobInstance.Collider = Unity.Physics.BoxCollider.Create(key.Convert(convexRadius), filter, material);
                                colliderBlobInstance.CompoundFromChild = values[i];
                                colliderBlobInstances.Add(colliderBlobInstance);
                            }
                        }
                    }
                }

                using (var colliders = reader.DeserializeNativeMultiHashMap<SphereColliderData, RigidTransform>(Allocator.Temp))
                {
                    using (var keys = colliders.GetKeyArray(Allocator.Temp))
                    {
                        using (var values = colliders.GetValueArray(Allocator.Temp))
                        {
                            int length = math.max(keys.Length, values.Length);
                            SphereColliderData key;
                            for (int i = 0; i < length; ++i)
                            {
                                key = keys[i];

#if UNITY_EDITOR
                                ++index;

                                if (progressBarDisplayCount > 0 && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Colliders", "Sphere Collider(" + index + '/' + progressBarDisplayCount + ')', index * 1.0f / progressBarDisplayCount))
                                    return;
#endif
                                if (isAutoLayer)
                                    filter.BelongsTo = 1u << (key.layer & 31);

                                material.CollisionResponse = ((LayerFlag)key.layer & LayerFlag.Trigger) == LayerFlag.Trigger ? CollisionResponsePolicy.RaiseTriggerEvents : CollisionResponsePolicy.Collide;

                                colliderBlobInstance.Collider = Unity.Physics.SphereCollider.Create(key.Convert(), filter, material);
                                colliderBlobInstance.CompoundFromChild = values[i];
                                colliderBlobInstances.Add(colliderBlobInstance);
                            }
                        }
                    }
                }

                using (var colliders = reader.DeserializeNativeMultiHashMap<CapsuleColliderData, RigidTransform>(Allocator.Temp))
                {
                    using (var keys = colliders.GetKeyArray(Allocator.Temp))
                    {
                        using (var values = colliders.GetValueArray(Allocator.Temp))
                        {
                            int length = math.max(keys.Length, values.Length);
                            CapsuleColliderData key;
                            for (int i = 0; i < length; ++i)
                            {
                                key = keys[i];
#if UNITY_EDITOR
                                ++index;

                                if (progressBarDisplayCount > 0 && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Colliders", "Capsule Collider(" + index + '/' + progressBarDisplayCount + ')', index * 1.0f / progressBarDisplayCount))
                                    return;
#endif
                                if (isAutoLayer)
                                    filter.BelongsTo = 1u << (key.layer & 31);

                                material.CollisionResponse = ((LayerFlag)key.layer & LayerFlag.Trigger) == LayerFlag.Trigger ? CollisionResponsePolicy.RaiseTriggerEvents : CollisionResponsePolicy.Collide;

                                colliderBlobInstance.Collider = Unity.Physics.CapsuleCollider.Create(key.Convert(), filter, material);
                                colliderBlobInstance.CompoundFromChild = values[i];
                                colliderBlobInstances.Add(colliderBlobInstance);
                            }
                        }
                    }
                }

                ConvexHullGenerationParameters parameters = ConvexHullGenerationParameters.Default;
                parameters.BevelRadius = convexRadius;

                byte layer = reader.ReadByte();
                while (layer != 255)
                {
                    if (isAutoLayer)
                        filter.BelongsTo = 1u << (layer & 31);

                    using (var points = reader.DeserializeNativeArray<float3>(Allocator.Temp))
                    {
                        if (((LayerFlag)layer & LayerFlag.Convex) == LayerFlag.Convex)
                        {
                            colliderBlobInstance.Collider = ConvexCollider.Create(points, parameters, filter, material);
                        }
                        else
                        {
                            using (var indices = reader.DeserializeNativeArray<int3>(Allocator.Temp))
                            {
                                colliderBlobInstance.Collider = Unity.Physics.MeshCollider.Create(points, indices, filter, material);
                            }
                        }

                        using (var transforms = reader.DeserializeNativeArray<RigidTransform>(Allocator.Temp))
                        {
                            foreach (var transform in transforms)
                            {
#if UNITY_EDITOR
                                ++index;

                                if (progressBarDisplayCount > 0 && UnityEditor.EditorUtility.DisplayCancelableProgressBar("Convert Colliders", "Mesh Collider(" + index + '/' + progressBarDisplayCount + ')', index * 1.0f / progressBarDisplayCount))
                                    return;
#endif
                                colliderBlobInstance.CompoundFromChild = transform;

                                colliderBlobInstances.Add(colliderBlobInstance);
                            }
                        }
                    }

                    layer = reader.ReadByte();
                }

#if UNITY_EDITOR
            }
            catch(Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                if (progressBarDisplayCount > 0)
                    UnityEditor.EditorUtility.ClearProgressBar();
            }
#endif
        }
    }
}