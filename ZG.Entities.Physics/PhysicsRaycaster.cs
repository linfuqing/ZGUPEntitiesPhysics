using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Collections;
using Unity.Entities;
using Unity.Physics;
using Unity.Physics.Systems;
using UnityEngine;
using UnityEngine.EventSystems;
using Unity.Burst;
using Unity.Burst.Intrinsics;

namespace ZG.Entities.Physics
{
    public struct PhysicsRaycastCollider : ICleanupComponentData
    {
        public BlobAssetReference<Unity.Physics.Collider> value;
    }

    public partial class PhysicsRaycastSystem : LookupSystem
    {
        private struct Raycast
        {
            public NativeList<PhysicsRaycaster.Collider> collidersToIgnore;

            [ReadOnly]
            public NativeArray<Entity> entityArray;
            [ReadOnly]
            public NativeArray<PhysicsShapeCompoundCollider> compoundColliders;
            public NativeArray<PhysicsRaycastCollider> raycastColliders;

            public unsafe void Execute(int index)
            {
                var raycastedCollider = raycastColliders[index];
                var compoundCollider = index < compoundColliders.Length ? compoundColliders[index].value : BlobAssetReference<Unity.Physics.Collider>.Null;
                if (raycastedCollider.value == compoundCollider)
                    return;

                PhysicsRaycaster.Collider collider;
                collider.entity = entityArray[index];

                if (raycastedCollider.value.IsCreated)
                {
                    collider.value = (Unity.Physics.Collider*)raycastedCollider.value.GetUnsafePtr();
                    int ignoreIndex = collidersToIgnore.IndexOf(collider);
                    if (ignoreIndex != -1)
                        collidersToIgnore.RemoveAt(ignoreIndex);
                }

                if (compoundCollider.IsCreated)
                {
                    collider.value = (Unity.Physics.Collider*)compoundCollider.GetUnsafePtr();

                    collidersToIgnore.Add(collider);
                }

                raycastedCollider.value = compoundCollider;
                raycastColliders[index] = raycastedCollider;
            }
        }

        [BurstCompile]
        private struct RaycastEx : IJobChunk
        {
            public NativeList<PhysicsRaycaster.Collider> collidersToIgnore;

            [ReadOnly]
            public EntityTypeHandle entityType;
            [ReadOnly]
            public ComponentTypeHandle<PhysicsShapeCompoundCollider> compoundColliderType;
            public ComponentTypeHandle<PhysicsRaycastCollider> raycastColliderType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Raycast raycast;
                raycast.collidersToIgnore = collidersToIgnore;
                raycast.entityArray = chunk.GetNativeArray(entityType);
                raycast.compoundColliders = chunk.GetNativeArray(ref compoundColliderType);
                raycast.raycastColliders = chunk.GetNativeArray(ref raycastColliderType);

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    raycast.Execute(i);
            }
        }

        private EntityQuery __groupToInit;
        private EntityQuery __groupToDestroy;

        private EndFrameEntityCommandSystem __endFrameBarrier;

        public NativeList<PhysicsRaycaster.Collider> collidersToIgnore
        {
            get;

            private set;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            __groupToInit = GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<PhysicsRaycastCollider>(),
                        ComponentType.ReadOnly<PhysicsShapeCompoundCollider>()
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });

            __groupToInit.SetChangedVersionFilter(typeof(PhysicsShapeCompoundCollider));

            __groupToDestroy = GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<PhysicsRaycastCollider>()
                    },
                    None = new ComponentType[]
                    {
                        typeof(PhysicsShapeCompoundCollider)
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });

            __endFrameBarrier = World.GetOrCreateSystemManaged<EndFrameEntityCommandSystem>();

            collidersToIgnore = new NativeList<PhysicsRaycaster.Collider>(Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            collidersToIgnore.Dispose();

            base.OnDestroy();
        }

        protected override void _Update()
        {
            RaycastEx raycast;
            raycast.collidersToIgnore = collidersToIgnore;
            raycast.entityType = GetEntityTypeHandle();
            raycast.compoundColliderType = GetComponentTypeHandle<PhysicsShapeCompoundCollider>(true);
            raycast.raycastColliderType = GetComponentTypeHandle<PhysicsRaycastCollider>();

            var jobHandle = raycast.Schedule(__groupToInit, Dependency);

            jobHandle = raycast.Schedule(__groupToDestroy, jobHandle);

            Dependency = __endFrameBarrier.RemoveComponent<PhysicsShapeCompoundCollider>(__groupToDestroy, jobHandle);
        }
    }

    /// <summary>
    ///   <para>Raycaster for casting against 3D Physics components.</para>
    /// </summary>
    [AddComponentMenu("ZG/Entities/Physics Raycaster")]
    [RequireComponent(typeof(Camera))]
    public class PhysicsRaycaster : BaseRaycaster
    {
        private struct Comparable : IComparer<Unity.Physics.RaycastHit>
        {
            public int Compare(Unity.Physics.RaycastHit x, Unity.Physics.RaycastHit y)
            {
                return x.Fraction.CompareTo(y.Fraction);
            }
        }

        [Serializable]
        public struct Collider : IEquatable<Collider>
        {
            public Entity entity;
            public unsafe Unity.Physics.Collider* value;

            public unsafe bool Equals(Collider other)
            {
                return entity == other.entity && value == other.value;
            }

            public unsafe override int GetHashCode()
            {
                return (int)value;
            }
        }

        public int groupIndex;
        public LayerMask belongsTo = 1 << 25;
        public LayerMask collidesWith = -1;

        public LayerMask nearClipMask = ~(1 << 30);

        public float nearClipOverride = 3.0f;

        public string worldName;

        [SerializeField]
        protected Camera _eventCamera;

        private World __world;
        private BuildPhysicsWorld __buildPhysicsWorld;
        private EndFramePhysicsSystem __endFramePhysicsSystem;
        private PhysicsRaycastSystem __physicsRaycastSystem;

        private NativeList<Unity.Physics.RaycastHit> __hits;

        public NativeArray<Unity.Physics.RaycastHit> hits => __hits.AsArray();

        public NativeList<Collider> collidersToIgnore
        {
            get
            {
                if(__physicsRaycastSystem == null)
                    __physicsRaycastSystem = world.GetExistingSystemManaged<PhysicsRaycastSystem>();

                __physicsRaycastSystem.CompleteReadWriteDependency();
                return __physicsRaycastSystem.collidersToIgnore;
            }
        }

        /// <summary>
        ///   <para>Get the camera that is used for this module.</para>
        /// </summary>
        public override Camera eventCamera
        {
            get
            {
                if ((UnityEngine.Object)_eventCamera == (UnityEngine.Object)null)
                    _eventCamera = base.GetComponent<Camera>();

                return _eventCamera ?? Camera.main;
            }
        }

        public World world
        {
            get
            {
                if (__world == null)
                {
                    if (string.IsNullOrEmpty(worldName))
                        __world = World.DefaultGameObjectInjectionWorld;
                    else
                    {
                        __world = null;
                        foreach (World temp in World.All)
                        {
                            if (temp.Name == worldName)
                            {
                                __world = temp;

                                break;
                            }
                        }
                    }
                }

                return __world;
            }
        }

        public void ComputeRayAndDistance(PointerEventData eventData, out UnityEngine.Ray ray, out float distanceToClipPlane)
        {
            ray = eventCamera.ScreenPointToRay(eventData.position);
            Vector3 direction = ray.direction;
            float z = direction.z;
            distanceToClipPlane = ((!Mathf.Approximately(0f, z)) ? Mathf.Abs((eventCamera.farClipPlane - eventCamera.nearClipPlane) / z) : (eventCamera.farClipPlane - eventCamera.nearClipPlane));
        }

        public unsafe override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
        {
            if (eventCamera == null || !eventCamera.pixelRect.Contains(eventData.position))
                return;

            ComputeRayAndDistance(eventData, out var ray, out float distanceToClipPlane);
            
            RaycastInput raycastInput = default;
            raycastInput.Start = ray.origin;
            raycastInput.End = ray.origin + ray.direction * distanceToClipPlane;
            raycastInput.Filter.GroupIndex = groupIndex;
            raycastInput.Filter.BelongsTo = (uint)(int)belongsTo;
            raycastInput.Filter.CollidesWith = (uint)(int)collidesWith;

            if (__buildPhysicsWorld == null)
                __buildPhysicsWorld = world.GetExistingSystemManaged<BuildPhysicsWorld>();

            if(__endFramePhysicsSystem == null)
                __endFramePhysicsSystem = world.GetExistingSystemManaged<EndFramePhysicsSystem>();

            __endFramePhysicsSystem.GetOutputDependency().Complete();

            if (__hits.IsCreated)
                __hits.Clear();
            else
                __hits = new NativeList<Unity.Physics.RaycastHit>(Allocator.Persistent);

            if (!__buildPhysicsWorld.PhysicsWorld.CastRay(raycastInput, ref __hits))
                return;

            __hits.AsArray().Sort(new Comparable());
            
            int length = __hits.Length;
            float distance;
            Collider collider;
            ChildCollider child;
            RigidBody rigidbody;
            Unity.Physics.RaycastHit hit;
            RaycastResult raycastResult = default;
            Transform transform;
            EntityManager entityManager = world.EntityManager;
            var collidersToIgnore = this.collidersToIgnore;
            for (int i = 0; i < length; ++i)
            {
                hit = __hits[i];

                rigidbody = __buildPhysicsWorld.PhysicsWorld.Bodies[hit.RigidBodyIndex];

                collider.value = rigidbody.Collider.IsCreated ? (Unity.Physics.Collider*)rigidbody.Collider.GetUnsafePtr() : null;
                if (collider.value == null)
                    continue;

                distance = hit.Fraction * distanceToClipPlane;
                if (distance < nearClipOverride && (collider.value->GetLeafFilter(hit.ColliderKey).BelongsTo & nearClipMask) != 0)
                    continue;

                if (collidersToIgnore.IsCreated)
                {
                    collider.entity = rigidbody.Entity;
                    if (collidersToIgnore.Contains(collider))
                    {
                        __hits.RemoveAtSwapBack(i--);

                        --length;

                        continue;
                    }

                    if (collider.value->GetLeaf(hit.ColliderKey, out child) && collidersToIgnore.Contains(new Collider() { entity = rigidbody.Entity, value = child.Collider }))
                    {
                        __hits.RemoveAtSwapBack(i--);

                        --length;

                        continue;
                    }
                }

                transform = entityManager.HasComponent<EntityObject<Transform>>(rigidbody.Entity) ? entityManager.GetComponentData<EntityObject<Transform>>(rigidbody.Entity).value : null;
                //Debug.Log(transform);
                
                raycastResult.gameObject = transform == null ? null : transform.gameObject;
                raycastResult.module = this;
                raycastResult.distance = distance;
                raycastResult.worldPosition = hit.Position;
                raycastResult.worldNormal = hit.SurfaceNormal;
                raycastResult.screenPosition = eventData.position;
                raycastResult.index = resultAppendList.Count;
                raycastResult.sortingLayer = 0;
                raycastResult.sortingOrder = 0;
                resultAppendList.Add(raycastResult);
            }
        }

        protected override void OnDestroy()
        {
            if (__hits.IsCreated)
                __hits.Dispose();

            base.OnDestroy();
        }
    }
}