using Unity.Jobs;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using Unity.Physics.Systems;

namespace ZG
{
    [BurstCompile, UpdateInGroup(typeof(PhysicsHierarchyTriggerSystemGroup), OrderLast = true)/*typeof(EntityObjectSystemGroup), OrderLast = true),
        UpdateAfter(typeof(PhysicsHierarchySystemGroup)), 
        UpdateAfter(typeof(EndEntityObjectSystemGroupEntityCommandSystem))*/]
    public partial struct PhysicsShapeChildSystem : ISystem
    {
        private struct Count
        {
            [NativeDisableParallelForRestriction]
            public NativeArray<int> counts;
            [ReadOnly]
            public BufferAccessor<PhysicsShapeChild> children;

            public void Execute(int index)
            {
                var children = this.children[index];
                int numChildren = children.Length;
                for (int i = 0; i < numChildren; ++i)
                {
                    if (children[i].contactTolerance > math.FLT_MIN_NORMAL)
                        ++counts[0];
                    else
                        ++counts[1];
                }
            }
        }

        [BurstCompile]
        private struct CountEx : IJob
        {
            public NativeArray<int> counts;

            [ReadOnly]
            public NativeArray<ArchetypeChunk> chunks;
            [ReadOnly]
            public BufferTypeHandle<PhysicsShapeChild> childType;

            public void Execute(int index)
            {
                var chunk = chunks[index];
                Count count;
                count.counts = counts;
                count.children = chunk.GetBufferAccessor(ref childType);
                int numChildren = chunk.Count;
                for (int i = 0; i < numChildren; ++i)
                    count.Execute(i);
            }

            public void Execute()
            {
                int length = chunks.Length;
                for (int i = 0; i < length; ++i)
                    Execute(i);
            }
        }

        [BurstCompile]
        private struct FillColliders : IJob
        {
            [ReadOnly]
            public EntityTypeHandle entityType;
            [ReadOnly]
            public ComponentTypeHandle<Translation> translationType;
            [ReadOnly]
            public ComponentTypeHandle<Rotation> rotationType;
            [ReadOnly]
            public BufferTypeHandle<PhysicsShapeChild> childType;
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<ArchetypeChunk> chunks;
            [ReadOnly]
            public NativeArray<Entity> colliders;
            [ReadOnly]
            public NativeArray<Entity> triggers;
            
            public ComponentLookup<PhysicsShapeParent> parents;

            public ComponentLookup<PhysicsShapeCollider> physicsShapeColliders;
            public ComponentLookup<PhysicsCollider> physicsColliders;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Translation> translations;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Rotation> rotations;

            public UnsafeListEx<TagData> tags;

            public UnsafeListEx<Entity> tagedEntities;

            public UnsafeListEx<Entity> disabledEntities;

            public void Execute()
            {
                RigidTransform transform, childTransform;
                Translation childTranslation;
                Rotation childRotation;
                PhysicsCollider physicsCollider;
                PhysicsShapeCollider physicsShapeCollider;
                PhysicsShapeChild child;
                PhysicsShapeChildEntity childEntity;
                PhysicsShapeParent parent;
                TagData tag;
                ArchetypeChunk chunk;
                NativeArray<Entity> entityArray;
                NativeArray<Translation> translations;
                NativeArray<Rotation> rotations;
                DynamicBuffer<PhysicsShapeChild> children;
                BufferAccessor<PhysicsShapeChild> childrenAccessor;
                int i, j, k, numChildren, numEntities, numChunks = chunks.Length, colliderIndex = 0, triggerIndex = 0;
                for (i = 0; i < numChunks; ++i)
                {
                    chunk = chunks[i];
                    numEntities = chunk.Count;
                    entityArray = chunk.GetNativeArray(entityType);
                    translations = chunk.GetNativeArray(ref translationType);
                    rotations = chunk.GetNativeArray(ref rotationType);
                    childrenAccessor = chunk.GetBufferAccessor(ref childType);
                    for (j = 0; j < numEntities; ++j)
                    {
                        parent.entity = entityArray[j];
                        transform = math.RigidTransform(rotations[j].Value, translations[j].Value);
                        children = childrenAccessor[j];
                        numChildren = children.Length;
                        for (k = 0; k < numChildren; ++k)
                        {
                            child = children[k];

                            UnityEngine.Assertions.Assert.IsTrue(child.collider.IsCreated);
                            UnityEngine.Assertions.Assert.IsFalse(child.collider.Value.Filter.IsEmpty);

                            if (child.contactTolerance > math.FLT_MIN_NORMAL)
                            {
                                childEntity.value = colliders[colliderIndex++];

                                physicsShapeCollider.contactTolerance = child.contactTolerance;
                                physicsShapeCollider.value = child.collider;
                                physicsShapeColliders[childEntity.value] = physicsShapeCollider;
                            }
                            else
                            {
                                childEntity.value = triggers[triggerIndex++];

                                physicsCollider.Value = child.collider;
                                physicsColliders[childEntity.value] = physicsCollider;
                            }

                            if (childEntity.value != Entity.Null)
                            {
                                parent.index = k;

                                childTransform = math.mul(transform, child.transform);

                                childTranslation.Value = childTransform.pos;
                                childRotation.Value = childTransform.rot;

                                parents[childEntity.value] = parent;
                                this.translations[childEntity.value] = childTranslation;
                                this.rotations[childEntity.value]  = childRotation;

                                if (child.tag.Length > 0)
                                {
                                    tag.value = child.tag;
                                    tags.Add(tag);
                                    tagedEntities.Add(childEntity.value);
                                }

                                if ((child.flag & PhysicsShapeChild.Flag.ColliderDisabled) == PhysicsShapeChild.Flag.ColliderDisabled)
                                    disabledEntities.Add(childEntity.value);
                            }
                        }
                    }
                }
                
                UnityEngine.Assertions.Assert.AreEqual(colliders.Length, colliderIndex);
                UnityEngine.Assertions.Assert.AreEqual(triggers.Length, triggerIndex);
            }
        }


        [BurstCompile]
        private struct SetTags : IJobParallelFor
        {
            [ReadOnly]
            public UnsafeListEx<Entity> entityArray;

            [ReadOnly]
            public UnsafeListEx<TagData> source;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<TagData> destination;

            public void Execute(int index)
            {
                destination[entityArray[index]] = source[index];
            }
        }
        
        [BurstCompile]
        private struct DisposeAll : IJob
        {
            public UnsafeListEx<Entity> entities;

            public UnsafeListEx<TagData> tags;

            public void Execute()
            {
                entities.Dispose();
                tags.Dispose();
            }
        }

        [BurstCompile]
        private struct CollectChildEntities : IJob
        {
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> entityArray;

            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> colliders;
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> triggers;

            [ReadOnly]
            public BufferLookup<PhysicsShapeChild> children;
            
            public BufferLookup<PhysicsShapeChildEntity> childEntities;

            public void Execute()
            {
                DynamicBuffer<PhysicsShapeChildEntity> childEntities;
                DynamicBuffer<PhysicsShapeChild> children;
                PhysicsShapeChildEntity childEntity;
                Entity entity;
                int colliderIndex = 0, triggerIndex = 0, numEntities = entityArray.Length, numChildren, i, j;
                for(i = 0; i < numEntities; ++i)
                {
                    entity = entityArray[i];
                    children = this.children[entity];
                    childEntities = this.childEntities[entity];
                    numChildren = children.Length;
                    for(j = 0; j < numChildren; ++j)
                    {
                        childEntity.value = children[j].contactTolerance > math.FLT_MIN_NORMAL ? colliders[colliderIndex++] : triggers[triggerIndex++];
                        
                        childEntities.Add(childEntity);
                    }
                }
                
                UnityEngine.Assertions.Assert.AreEqual(colliders.Length, colliderIndex);
                UnityEngine.Assertions.Assert.AreEqual(triggers.Length, triggerIndex);
            }
        }

        private struct CollectEntitiesToDestroy
        {
            [ReadOnly]
            public BufferAccessor<PhysicsShapeChildEntity> childEntities;

            public NativeList<Entity> entities;

            public void Execute(int index)
            {
                entities.AddRange(childEntities[index].Reinterpret<Entity>().AsNativeArray());
            }
        }

        [BurstCompile]
        private struct CollectEntitiesToDestroyEx : IJobChunk
        {
            [ReadOnly]
            public BufferTypeHandle<PhysicsShapeChildEntity> childEntityType;

            public NativeList<Entity> entities;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectEntitiesToDestroy collectEntitiesToDestroy;
                collectEntitiesToDestroy.childEntities = chunk.GetBufferAccessor(ref childEntityType);
                collectEntitiesToDestroy.entities = entities;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    collectEntitiesToDestroy.Execute(i);
            }
        }

        public static readonly int InnerloopBatchCount = 1;

        private EntityArchetype __colliderArchetype;
        private EntityArchetype __triggerArchetype;
        private EntityQuery __groupToCreate;
        private EntityQuery __groupToDestroy;

        public void OnCreate(ref SystemState state)
        {
            BurstUtility.InitializeJob<CountEx>();
            BurstUtility.InitializeJob<FillColliders>();
            BurstUtility.InitializeJobParallelFor<SetTags>();
            BurstUtility.InitializeJob<DisposeAll>();
            BurstUtility.InitializeJob<CollectChildEntities>();

            __colliderArchetype = state.EntityManager.CreateArchetype(
                ComponentType.ReadOnly<PhysicsShapeParent>(),
                ComponentType.ReadOnly<PhysicsShapeCollider>(), 
                ComponentType.ReadWrite<Translation>(),
                ComponentType.ReadWrite<Rotation>(),
                ComponentType.ReadWrite<PhysicsVelocity>(),
                ComponentType.ReadWrite<PhysicsShapeChildHit>());

            __triggerArchetype = state.EntityManager.CreateArchetype(
                ComponentType.ReadOnly<PhysicsShapeParent>(),
                ComponentType.ReadOnly<PhysicsCollider>(),
                ComponentType.ReadWrite<Translation>(),
                ComponentType.ReadWrite<Rotation>(),
                ComponentType.ReadWrite<PhysicsVelocity>(),
                ComponentType.ReadWrite<PhysicsTriggerEvent>());

            __groupToCreate = state.GetEntityQuery(
                ComponentType.ReadOnly<Translation>(),
                ComponentType.ReadOnly<Rotation>(),
                ComponentType.ReadOnly<PhysicsShapeChild>(),
                ComponentType.Exclude<PhysicsShapeChildEntity>());

            __groupToDestroy = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<PhysicsShapeChildEntity>()
                    },
                    None = new ComponentType[]
                    {
                        typeof(PhysicsShapeChild)
                    }
                },
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<PhysicsShapeChildEntity>(), 
                        ComponentType.ReadOnly<Disabled>()
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;

            if (!__groupToDestroy.IsEmptyIgnoreFilter)
            {
                //UnityEngine.Profiling.Profiler.BeginSample("Destroy Entities");

                var entitiesToDestroy = new NativeList<Entity>(Allocator.TempJob);
                {
                    state.CompleteDependency();

                    CollectEntitiesToDestroyEx collectEntitiesToDestroy;
                    collectEntitiesToDestroy.childEntityType = state.GetBufferTypeHandle<PhysicsShapeChildEntity>(true);
                    collectEntitiesToDestroy.entities = entitiesToDestroy;
                    collectEntitiesToDestroy.Run(__groupToDestroy);

                    entityManager.DestroyEntity(entitiesToDestroy.AsArray());
                }
                entitiesToDestroy.Dispose();

                entityManager.RemoveComponent<PhysicsShapeChildEntity>(__groupToDestroy);

                //UnityEngine.Profiling.Profiler.EndSample();
            }

            if (!__groupToCreate.IsEmptyIgnoreFilter)
            {
                var chunks = __groupToCreate.ToArchetypeChunkArray(Allocator.TempJob);
                {
                    //UnityEngine.Profiling.Profiler.BeginSample("Count");

                    NativeArray<int> counts = new NativeArray<int>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory);

                    state.CompleteDependency();

                    CountEx count;
                    count.counts = counts;
                    count.chunks = chunks;
                    count.childType = state.GetBufferTypeHandle<PhysicsShapeChild>(true);
                    count.Run();// (chunks.Length);
                    int colliderCount = counts[0], triggerCount = counts[1];
                    counts.Dispose();

                    //UnityEngine.Profiling.Profiler.EndSample();

                    if (colliderCount > 0 || triggerCount > 0)
                    {
                        //UnityEngine.Profiling.Profiler.BeginSample("Fill Colliders");

                        NativeArray<Entity> colliders = entityManager.CreateEntity(__colliderArchetype, colliderCount, Allocator.TempJob),
                            triggers = entityManager.CreateEntity(__triggerArchetype, triggerCount, Allocator.TempJob);
                        var tags = new UnsafeListEx<TagData>(Allocator.TempJob);
                        UnsafeListEx<Entity> tagedEntities = new UnsafeListEx<Entity>(Allocator.TempJob), disabledEntities = new UnsafeListEx<Entity>(Allocator.TempJob);

                        var entityType = state.GetEntityTypeHandle();

                        FillColliders fillColliders;
                        fillColliders.entityType = entityType;
                        fillColliders.translationType = state.GetComponentTypeHandle<Translation>(true);
                        fillColliders.rotationType = state.GetComponentTypeHandle<Rotation>(true);
                        fillColliders.childType = state.GetBufferTypeHandle<PhysicsShapeChild>(true);
                        fillColliders.chunks = chunks;
                        fillColliders.colliders = colliders;
                        fillColliders.triggers = triggers;
                        fillColliders.parents = state.GetComponentLookup<PhysicsShapeParent>();
                        fillColliders.physicsShapeColliders = state.GetComponentLookup<PhysicsShapeCollider>();
                        fillColliders.physicsColliders = state.GetComponentLookup<PhysicsCollider>();
                        fillColliders.translations = state.GetComponentLookup<Translation>();
                        fillColliders.rotations = state.GetComponentLookup<Rotation>();
                        fillColliders.tags = tags;
                        fillColliders.tagedEntities = tagedEntities;
                        fillColliders.disabledEntities = disabledEntities;

                        fillColliders.Run();

                        var entityArray = __groupToCreate.ToEntityArrayBurstCompatible(entityType, Allocator.TempJob);

                        entityManager.AddComponent<PhysicsShapeChildEntity>(__groupToCreate);

                        //UnityEngine.Profiling.Profiler.EndSample();

                        //UnityEngine.Profiling.Profiler.BeginSample("Disable And Tag");

                        entityManager.AddComponentBurstCompatible<Disabled>(disabledEntities.AsArray());

                        disabledEntities.Dispose();

                        entityManager.AddComponentBurstCompatible<TagData>(tagedEntities.AsArray());

                        var inputDeps = state.Dependency;

                        SetTags setTags;
                        setTags.entityArray = tagedEntities;
                        setTags.source = tags;
                        setTags.destination = state.GetComponentLookup<TagData>();
                        var jobHandle = setTags.Schedule(tagedEntities.length, InnerloopBatchCount, inputDeps);
                        //var jobHandle = tags.AsArray().Reinterpret<TagData>().MoveTo(tagedEntities, state.GetComponentLookup<TagData>(), 1, default);

                        DisposeAll disposeAll;
                        disposeAll.entities = tagedEntities;
                        disposeAll.tags = tags;
                        jobHandle = disposeAll.Schedule(jobHandle);
                        //jobHandle = JobHandle.CombineDependencies(tags.Dispose(jobHandle), tagedEntities.Dispose(jobHandle));

                        //UnityEngine.Profiling.Profiler.EndSample();

                        //UnityEngine.Profiling.Profiler.BeginSample("Collect Child Entities");

                        CollectChildEntities collectChildEntities;
                        collectChildEntities.entityArray = entityArray;
                        collectChildEntities.colliders = colliders;
                        collectChildEntities.triggers = triggers;
                        collectChildEntities.children = state.GetBufferLookup<PhysicsShapeChild>(true);
                        collectChildEntities.childEntities = state.GetBufferLookup<PhysicsShapeChildEntity>();

                        state.Dependency = JobHandle.CombineDependencies(jobHandle, collectChildEntities.Schedule(inputDeps));

                        //UnityEngine.Profiling.Profiler.EndSample();
                    }
                    else
                    {
                        chunks.Dispose();

                        entityManager.AddComponent<PhysicsShapeChildEntity>(__groupToCreate);
                    }
                }
            }
        }
    }
    
    //TODO: GamePhysicsWorldBuildSystem
    [BurstCompile, UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderFirst = true)/*, UpdateAfter(typeof(PhysicsShapeChildSystem))*/]
    public partial struct PhysicsShapeDynamicSystem : ISystem
    {
        /*[BurstCompile]
        private struct UpdateChildren : IJobParallelFor
        {
            public NativeArray<Translation> translations;

            public NativeArray<Rotation> rotations;

            [ReadOnly]
            public NativeArray<PhysicsShapeParent> parents;

            [ReadOnly]
            public BufferLookup<PhysicsShapeChild> children;

            [ReadOnly]
            public ComponentLookup<Translation> translationMap;

            [ReadOnly]
            public ComponentLookup<Rotation> rotationMap;

            public void Execute(int index)
            {
                var parent = parents[index];
                RigidTransform parentTransform = math.RigidTransform(rotationMap[parent.entity].Value, translationMap[parent.entity].Value), 
                    localTransform = children[parent.entity][parent.index].transform, transform = math.mul(parentTransform, localTransform);

                Translation translation;
                translation.Value = transform.pos;
                translations[index] = translation;

                Rotation rotation;
                rotation.Value = transform.rot;
                rotations[index] = rotation;
            }
        }*/

        private struct UpdateChildren
        {
            [ReadOnly]
            public NativeArray<Translation> translations;

            [ReadOnly]
            public NativeArray<Rotation> rotations;

            [ReadOnly]
            public BufferAccessor<PhysicsShapeChild> children;

            [ReadOnly]
            public BufferAccessor<PhysicsShapeChildEntity> childEntities;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Translation> translationMap;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Rotation> rotationMap;

            public void Execute(int index)
            {
                RigidTransform transform = math.RigidTransform(rotations[index].Value, translations[index].Value), childTransform;
                var children = this.children[index];
                var childEntities = this.childEntities[index];

                int length = children.Length;
                Entity entity;
                Translation translation;
                Rotation rotation;
                for (int i = 0; i < length; ++i)
                {
                    childTransform = math.mul(transform, children[i].transform);

                    translation.Value = childTransform.pos;

                    entity = childEntities[i].value;

                    translationMap[entity] = translation;

                    rotation.Value = childTransform.rot;

                    rotationMap[entity] = rotation;
                }
            }
        }

        [BurstCompile]
        private struct UpdateChildrenEx : IJobChunk
        {
            public uint lastSystemVersion;

            [ReadOnly]
            public ComponentTypeHandle<Translation> translationType;

            [ReadOnly]
            public ComponentTypeHandle<Rotation> rotationType;

            [ReadOnly]
            public BufferTypeHandle<PhysicsShapeChild> childType;

            [ReadOnly]
            public BufferTypeHandle<PhysicsShapeChildEntity> childEntityType;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Translation> translations;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Rotation> rotations;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                if (!chunk.DidChange(ref translationType, lastSystemVersion) &&
                    !chunk.DidChange(ref rotationType, lastSystemVersion) &&
                    !chunk.DidChange(ref childType, lastSystemVersion) &&
                    !chunk.DidChange(ref childEntityType, lastSystemVersion))
                    return;

                UpdateChildren updateChildren;
                updateChildren.translations = chunk.GetNativeArray(ref translationType);
                updateChildren.rotations = chunk.GetNativeArray(ref rotationType);
                updateChildren.children = chunk.GetBufferAccessor(ref childType);
                updateChildren.childEntities = chunk.GetBufferAccessor(ref childEntityType);
                updateChildren.translationMap = translations;
                updateChildren.rotationMap = rotations;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    updateChildren.Execute(i);
            }
        }

        private EntityQuery __group;

        public void OnCreate(ref SystemState state)
        {
            __group = state.GetEntityQuery(
                ComponentType.ReadOnly<Translation>(),
                ComponentType.ReadOnly<Rotation>(),
                //ComponentType.ReadOnly<PhysicsVelocity>(),
                ComponentType.ReadOnly<PhysicsShapeChild>(),
                ComponentType.ReadOnly<PhysicsShapeChildEntity>());
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            UpdateChildrenEx updateChildren;
            updateChildren.lastSystemVersion = state.LastSystemVersion;
            updateChildren.translationType = state.GetComponentTypeHandle<Translation>(true);
            updateChildren.rotationType = state.GetComponentTypeHandle<Rotation>(true);
            updateChildren.childType = state.GetBufferTypeHandle<PhysicsShapeChild>(true);
            updateChildren.childEntityType = state.GetBufferTypeHandle<PhysicsShapeChildEntity>(true);
            updateChildren.translations = state.GetComponentLookup<Translation>();
            updateChildren.rotations = state.GetComponentLookup<Rotation>();

            state.Dependency = updateChildren.ScheduleParallel(__group, state.Dependency);
        }
    }

    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderFirst = true)]
    public partial class PhysicsShapeDestroyColliderSystem : SystemBase
    {
        private EntityQuery __group;
        private EndFramePhysicsSystem __endFrameBarrier;
        protected override void OnCreate()
        {
            base.OnCreate();

            /*__group = GetEntityQuery(
                ComponentType.ReadOnly<PhysicsShapeDestroiedCollider>(), 
                ComponentType.Exclude<Translation>(),
                ComponentType.Exclude<Rotation>());*/
            __endFrameBarrier = World.GetOrCreateSystemManaged<EndFramePhysicsSystem>();
        }

        protected override void OnUpdate()
        {
            __endFrameBarrier.GetOutputDependency().Complete();

            Entities.ForEach((in DynamicBuffer<PhysicsShapeDestroiedCollider> colliders) =>
            {
                foreach (var collider in colliders)
                {
#if DEBUG
                    UnityEngine.Debug.Log($"Dispose Collider {collider.value.GetHashCode()}");
#endif

                    collider.value.Dispose();
                }
            }).
            WithAll<PhysicsShapeDestroiedCollider>().
            WithNone<Translation>().
            WithStoreEntityQueryInField(ref __group).Run();

            EntityManager.RemoveComponent<PhysicsShapeDestroiedCollider>(__group);
        }

        /*private void __Destroy(
#if DEBUG
            Entity entity,
#endif
            [ReadOnly]DynamicBuffer<PhysicsShapeDestroiedCollider> colliders)
        {
#if DEBUG
            var value = EntityManager.HasComponent<PhysicsCollider>(entity) ? EntityManager.GetComponentData<PhysicsCollider>(entity).Value : BlobAssetReference<Collider>.Null;
#endif

            foreach (var collider in colliders)
            {
#if DEBUG
                UnityEngine.Assertions.Assert.AreNotEqual(value, collider.value);

                UnityEngine.Debug.Log($"Dispose Collider {collider.value.GetHashCode()}");
#endif

                collider.value.Dispose();
            }
        }*/
    }

    [BurstCompile, UpdateInGroup(typeof(FixedStepSimulationSystemGroup)), UpdateAfter(typeof(ExportPhysicsWorld)), UpdateBefore(typeof(EndFramePhysicsSystem))]
    public partial struct PhysicsShapeColliderSystem : ISystem
    {
        public struct Collector : ICollector<DistanceHit>
        {
            public DynamicBuffer<PhysicsShapeChildHit> __hits;

            private NativeSlice<RigidBody> __rigidbodies;

            public bool EarlyOutOnFirstHit => false;

            public float MaxFraction { get; }

            public int NumHits { get; private set; }

            public Collector(float maxDistance, ref NativeSlice<RigidBody> rigidbodies, ref DynamicBuffer<PhysicsShapeChildHit> hits)
            {
                __hits = hits;
                __rigidbodies = rigidbodies;

                MaxFraction = maxDistance;
                NumHits = 0;
            }

#region IQueryResult implementation

            public bool AddHit(DistanceHit value)
            {
                PhysicsShapeChildHit hit;
                hit.rigidbody = __rigidbodies[value.RigidBodyIndex];
                hit.value = value;
                __hits.Add(hit);

                NumHits++;

                return true;
            }
            
#endregion
        }

        private struct CalculateDistance
        {
            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<CollisionWorldProxy> collisionWorldProxies;

            [ReadOnly]
            public NativeArray<PhysicsShapeCollider> instances;

            public BufferAccessor<PhysicsShapeChildHit> hits;

            public unsafe void Execute(int index)
            {
                var instance = instances[index];
                if (!instance.value.IsCreated)
                    return;

                var collisionWorld = collisionWorldProxies[index].ToCollisionWorld();
                int rigidbodyIndex = collisionWorld.GetRigidBodyIndex(entityArray[index]);
                if (rigidbodyIndex == -1)
                    return;

                var rigidbodies = collisionWorld.Bodies.Slice();
                var rigidbody = rigidbodies[rigidbodyIndex];
                
                ColliderDistanceInput colliderDistanceInput = default;
                colliderDistanceInput.MaxDistance = instance.contactTolerance;
                colliderDistanceInput.Transform = rigidbody.WorldFromBody;
                colliderDistanceInput.Collider = (Collider*)instance.value.GetUnsafePtr();

                var hits = this.hits[index];
                hits.Clear();

                Collector collector = new Collector(instance.contactTolerance, ref rigidbodies, ref hits);
                collisionWorld.CalculateDistance(colliderDistanceInput, ref collector);
            }
        }

        [BurstCompile]
        private struct CalculateDistanceEx : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<CollisionWorldProxy> collisionWorldProxyType;

            [ReadOnly]
            public ComponentTypeHandle<PhysicsShapeCollider> instanceType;

            public BufferTypeHandle<PhysicsShapeChildHit> hitType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CalculateDistance calculateDistance;
                calculateDistance.entityArray = chunk.GetNativeArray(entityType);
                calculateDistance.collisionWorldProxies = chunk.GetNativeArray(ref collisionWorldProxyType);
                calculateDistance.instances = chunk.GetNativeArray(ref instanceType);
                calculateDistance.hits = chunk.GetBufferAccessor(ref hitType);

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    calculateDistance.Execute(i);
            }
        }

        private EntityQuery __group;

        public void OnCreate(ref SystemState state)
        {
            __group = state.GetEntityQuery(
                ComponentType.ReadOnly<CollisionWorldProxy>(),
                ComponentType.ReadOnly<PhysicsShapeCollider>(),
                ComponentType.ReadWrite<PhysicsShapeChildHit>(), 
                ComponentType.Exclude<Disabled>());
        }

        public void OnDestroy(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            CalculateDistanceEx calculateDistance;
            calculateDistance.entityType = state.GetEntityTypeHandle();
            calculateDistance.collisionWorldProxyType = state.GetComponentTypeHandle<CollisionWorldProxy>(true);
            calculateDistance.instanceType = state.GetComponentTypeHandle<PhysicsShapeCollider>(true);
            calculateDistance.hitType = state.GetBufferTypeHandle<PhysicsShapeChildHit>();
            state.Dependency = calculateDistance.ScheduleParallel(__group, state.Dependency);
        }
    }

    //TODO: ToBurst
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup)), UpdateAfter(typeof(PhysicsTriggerEventSystem)), UpdateBefore(typeof(EndFramePhysicsSystem))]
    public partial class PhysicsShapeTriggerEventRevicerSystem : SystemBase
    {
        private struct CollectTriggerEvents
        {
            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<PhysicsShapeParent> parents;

            [ReadOnly]
            public BufferAccessor<PhysicsTriggerEvent> triggerEvents;

            [ReadOnly]
            public BufferLookup<PhysicsShapeTriggerEventRevicer> revicers;

            public NativeQueue<EntityData<PhysicsShapeTriggerEventRevicer>>.ParallelWriter results;

            public void Execute(int index)
            {
                EntityData<PhysicsShapeTriggerEventRevicer> result;

                var triggerEvents = this.triggerEvents[index];
                Entity entity = index < parents.Length ? parents[index].entity : entityArray[index];
                int numTriggerEvents = triggerEvents.Length;
                for (int i = 0; i < numTriggerEvents; ++i)
                {
                    result.entity = triggerEvents[i].entity;
                    if (revicers.HasBuffer(result.entity))
                    {
                        result.value.eventIndex = i;
                        result.value.entity = entity;

                        results.Enqueue(result);
                    }
                }
            }
        }

        [BurstCompile]
        private struct CollectTriggerEventsEx : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<PhysicsShapeParent> parentType;

            [ReadOnly]
            public BufferTypeHandle<PhysicsTriggerEvent> triggerEventType;

            [ReadOnly]
            public BufferLookup<PhysicsShapeTriggerEventRevicer> revicers;

            public NativeQueue<EntityData<PhysicsShapeTriggerEventRevicer>>.ParallelWriter results;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectTriggerEvents collectTriggerEvents;
                collectTriggerEvents.entityArray = chunk.GetNativeArray(entityType);
                collectTriggerEvents.parents = chunk.GetNativeArray(ref parentType);
                collectTriggerEvents.triggerEvents = chunk.GetBufferAccessor(ref triggerEventType);
                collectTriggerEvents.revicers = revicers;
                collectTriggerEvents.results = results;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    collectTriggerEvents.Execute(i);
            }
        }

        [BurstCompile]
        private struct ApplyRevicers : IJob
        {
            public NativeQueue<EntityData<PhysicsShapeTriggerEventRevicer>> inputs;

            public BufferLookup<PhysicsShapeTriggerEventRevicer> outputs;

            public void Execute()
            {
                while (inputs.TryDequeue(out var revicer))
                    outputs[revicer.entity].Add(revicer.value);
            }
        }

        private EntityQuery __group;
        private NativeQueue<EntityData<PhysicsShapeTriggerEventRevicer>> __results;

        protected override void OnCreate()
        {
            base.OnCreate();

            __group = GetEntityQuery(/*ComponentType.ReadOnly<PhysicsShapeParent>(), */ComponentType.ReadOnly<PhysicsTriggerEvent>());

            __results = new NativeQueue<EntityData<PhysicsShapeTriggerEventRevicer>>(Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            __results.Dispose();

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            Entities.ForEach((ref DynamicBuffer<PhysicsShapeTriggerEventRevicer> revicers) => revicers.Clear()).ScheduleParallel();

            CollectTriggerEventsEx collectTriggerEvents;
            collectTriggerEvents.entityType = GetEntityTypeHandle();
            collectTriggerEvents.parentType = GetComponentTypeHandle<PhysicsShapeParent>(true);
            collectTriggerEvents.triggerEventType = GetBufferTypeHandle<PhysicsTriggerEvent>(true);
            collectTriggerEvents.revicers = GetBufferLookup<PhysicsShapeTriggerEventRevicer>(true);
            collectTriggerEvents.results = __results.AsParallelWriter();

            JobHandle jobHandle = collectTriggerEvents.ScheduleParallel(__group, Dependency);

            ApplyRevicers applyRevicers;
            applyRevicers.inputs = __results;
            applyRevicers.outputs = GetBufferLookup<PhysicsShapeTriggerEventRevicer>();

            Dependency = applyRevicers.Schedule(jobHandle);
        }
    }
}