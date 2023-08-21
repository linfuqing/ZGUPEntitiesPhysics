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

    [BurstCompile, UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct PhysicsRaycastSystem : ISystem
    {
        private struct Raycast
        {
            public SharedList<PhysicsRaycaster.Collider>.Writer collidersToIgnore;

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
                    int colliderIndex = collidersToIgnore.IndexOf(collider);

                    if(colliderIndex != -1)
                        collidersToIgnore.RemoveAt(colliderIndex);
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
            public uint lastSystemVersion;

            public SharedList<PhysicsRaycaster.Collider>.Writer collidersToIgnore;

            [ReadOnly]
            public EntityTypeHandle entityType;
            [ReadOnly]
            public ComponentTypeHandle<PhysicsShapeCompoundCollider> compoundColliderType;
            public ComponentTypeHandle<PhysicsRaycastCollider> raycastColliderType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                if (chunk.Has(ref compoundColliderType) && !chunk.DidChange(ref compoundColliderType, lastSystemVersion))
                    return;

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

        private EntityQuery __group;

        private EntityTypeHandle __entityType;

        private ComponentTypeHandle<PhysicsShapeCompoundCollider> __compoundColliderType;
        private ComponentTypeHandle<PhysicsRaycastCollider> __raycastColliderType;

        public SharedList<PhysicsRaycaster.Collider> collidersToIgnore
        {
            get;

            private set;
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __group = builder
                    .WithAllRW<PhysicsRaycastCollider>()
                    .WithAll<PhysicsShapeCompoundCollider>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                    .AddAdditionalQuery()
                    .WithAllRW<PhysicsRaycastCollider>()
                    .WithNone<PhysicsShapeCompoundCollider>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                    .Build(ref state);

            //__group.SetChangedVersionFilter(ComponentType.ReadOnly<PhysicsShapeCompoundCollider>());

            __entityType = state.GetEntityTypeHandle();
            __compoundColliderType = state.GetComponentTypeHandle<PhysicsShapeCompoundCollider>(true);
            __raycastColliderType = state.GetComponentTypeHandle<PhysicsRaycastCollider>();

            collidersToIgnore = new SharedList<PhysicsRaycaster.Collider>(Allocator.Persistent);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            collidersToIgnore.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            RaycastEx raycast;
            raycast.lastSystemVersion = state.LastSystemVersion;
            raycast.collidersToIgnore = collidersToIgnore.writer;
            raycast.entityType = __entityType.UpdateAsRef(ref state);
            raycast.compoundColliderType = __compoundColliderType.UpdateAsRef(ref state);
            raycast.raycastColliderType = __raycastColliderType.UpdateAsRef(ref state);

            ref var lookupJobManager = ref collidersToIgnore.lookupJobManager;

            var jobHandle = raycast.ScheduleByRef(__group, JobHandle.CombineDependencies(lookupJobManager.readWriteJobHandle, state.Dependency));

            lookupJobManager.readWriteJobHandle = jobHandle;

            state.Dependency = jobHandle;
        }
    }

    [BurstCompile, UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial struct PhysicsRaycastDestroySystem : ISystem
    {
        private EntityQuery __group;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __group = builder
                    .WithAll<PhysicsRaycastCollider>()
                    .WithNone<PhysicsShapeCompoundCollider>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                    .Build(ref state);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.EntityManager.RemoveComponent<PhysicsRaycastCollider>(__group);
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
        private SharedList<Collider> __collidersToIgnore;

        private NativeList<Unity.Physics.RaycastHit> __hits;

        public NativeArray<Unity.Physics.RaycastHit> hits => __hits.AsArray();

        public SharedList<Collider> collidersToIgnore
        {
            get
            {
                if(!__collidersToIgnore.isCreated)
                    __collidersToIgnore = world.GetExistingSystemUnmanaged<PhysicsRaycastSystem>().collidersToIgnore;

                return __collidersToIgnore;
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

        public void AddColliderToIgnore(in Collider collider)
        {
            var collidersToIgnore = this.collidersToIgnore;

            collidersToIgnore.lookupJobManager.CompleteReadWriteDependency();
            collidersToIgnore.writer.Add(collider);
        }

        public bool RemoveColliderFromIgnore(in Collider collider)
        {
            var collidersToIgnore = this.collidersToIgnore;

            collidersToIgnore.lookupJobManager.CompleteReadWriteDependency();

            var writer = collidersToIgnore.writer; 
            int colliderIndex = writer.IndexOf(collider);

            if (colliderIndex != -1)
            {
                writer.RemoveAt(colliderIndex);

                return true;
            }

            return false;
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

            var collidersToIgnore = this.collidersToIgnore;
            collidersToIgnore.lookupJobManager.CompleteReadOnlyDependency();

            var reader = collidersToIgnore.reader;

            int length = __hits.Length;
            float distance;
            Collider collider;
            ChildCollider child;
            RigidBody rigidbody;
            Unity.Physics.RaycastHit hit;
            RaycastResult raycastResult = default;
            Transform transform;
            EntityManager entityManager = world.EntityManager;
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

                if (collidersToIgnore.isCreated)
                {
                    collider.entity = rigidbody.Entity;
                    if (reader.Contains(collider))
                    {
                        __hits.RemoveAtSwapBack(i--);

                        --length;

                        continue;
                    }

                    if (collider.value->GetLeaf(hit.ColliderKey, out child) && reader.Contains(new Collider() { entity = rigidbody.Entity, value = child.Collider }))
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