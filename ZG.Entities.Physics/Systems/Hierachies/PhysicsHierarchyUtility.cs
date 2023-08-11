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
    public struct PhysicsHierarchyPrefab
    {
        public struct Shape
        {
            public BlobArray<Entity> triggers;
        }

        public int instanceCount;
        public BlobArray<Shape> shapes;
    }

    public struct PhysicsHierarchyDefinition
    {
        public struct Collider
        {
            public int index;

            public uint hash;

            public RigidTransform transform;
        }

        public struct Trigger
        {
            public int index;
            public float contactTolerance;
            public FixedString32Bytes tag;
        }

        public struct Shape
        {
            public BlobArray<Collider> colliders;
            public BlobArray<Trigger> triggers;
        }

        public int instanceID;
        public BlobArray<Shape> shapes;
    }

    public struct PhysicsHierarchyData : IComponentData
    {
        public BlobAssetReference<PhysicsHierarchyDefinition> definition;
    }

    public struct PhysicsHierarchyID : ICleanupComponentData
    {
        public int value;
    }

    [BurstCompile]
    public static class PhysicsHierarchyUtility
    {
        private struct Result
        {
            public BlobAssetReference<PhysicsHierarchyDefinition> definition;
        }

        private struct CollectToDestroy
        {
            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<PhysicsHierarchyID> ids;

            public SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>>.Writer prefabs;

            public NativeList<Entity> entities;

            public void Execute(int index)
            {
                int instanceID = ids[index].value;
                if (!prefabs.TryGetValue(instanceID, out var prefab))
                    return;

                ref var value = ref prefab.Value;
                if(--value.instanceCount > 0)
                    prefabs[instanceID] = prefab;
                else
                {
                    prefabs.Remove(instanceID);

                    int numShapes = value.shapes.Length;
                    for(int i = 0; i < numShapes; ++i)
                        entities.AddRange(value.shapes[i].triggers.AsArray());

                    prefab.Dispose();
                }
            }
        }

        [BurstCompile]
        private struct CollectToDestroyEx : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<PhysicsHierarchyID> idType;

            public SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>>.Writer prefabs;

            public NativeList<Entity> entities;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectToDestroy collect;
                collect.entityArray = chunk.GetNativeArray(entityType);
                collect.ids = chunk.GetNativeArray(ref idType);
                collect.prefabs = prefabs;
                collect.entities = entities;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    collect.Execute(i);
            }
        }

        private struct CollectToCreate
        {
            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<PhysicsHierarchyData> instances;

            public SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>>.Writer prefabs;

            public UnsafeListEx<Result> results;

            public NativeArray<int> eventAndHitCounts;

            public int Execute(int index)
            {
                Result result;
                result.definition = instances[index].definition;
                ref var definition = ref result.definition.Value;
                if (prefabs.TryGetValue(definition.instanceID, out var prefab))
                    ++prefab.Value.instanceCount;
                else
                {
                    using (var blobBuilder = new BlobBuilder(Allocator.Temp))
                    {
                        ref var root = ref blobBuilder.ConstructRoot<PhysicsHierarchyPrefab>();
                        root.instanceCount = 1;

                        int numShapes = numShapes = definition.shapes.Length;
                        var shapes = blobBuilder.Allocate(ref root.shapes, numShapes);

                        BlobBuilderArray<Entity> triggers;
                        int i, j, numTriggers, numEvents = 0, numHits = 0;
                        for(i = 0; i < numShapes; ++i)
                        {
                            ref var sourceShape = ref definition.shapes[i];
                            ref var destinationShape = ref shapes[i];

                            numTriggers = sourceShape.triggers.Length;
                            triggers = blobBuilder.Allocate(ref destinationShape.triggers, numTriggers);
                            for (j = 0; j < numTriggers; ++j)
                            {
                                if (sourceShape.triggers[j].contactTolerance > math.FLT_MIN_NORMAL)
                                    ++numHits;
                                else
                                    ++numEvents;
                            }
                        }

                        eventAndHitCounts[0] += numEvents;
                        eventAndHitCounts[1] += numHits;

                        prefabs[definition.instanceID] = blobBuilder.CreateBlobAssetReference<PhysicsHierarchyPrefab>(Allocator.Persistent);
                    }

                    results.Add(result);
                }

                return definition.instanceID;
            }
        }

        [BurstCompile]
        private struct CollectToCreateEx : IJobChunk
        {
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<int> baseEntityIndexArray;

            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<PhysicsHierarchyData> instanceType;

            public SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>>.Writer prefabs;

            public UnsafeListEx<Result> results;

            public NativeArray<int> eventAndHitCounts;

            public NativeArray<PhysicsHierarchyID> ids;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                CollectToCreate collect;
                collect.entityArray = chunk.GetNativeArray(entityType);
                collect.instances = chunk.GetNativeArray(ref instanceType);
                collect.prefabs = prefabs;
                collect.results = results;
                collect.eventAndHitCounts = eventAndHitCounts;

                PhysicsHierarchyID id;

                int index = baseEntityIndexArray[unfilteredChunkIndex];
                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                {
                    id.value = collect.Execute(i);

                    ids[index++] = id;
                }
            }
        }

        [BurstCompile]
        private struct Init : IJobParallelFor
        {
            [ReadOnly]
            public UnsafeListEx<Result> results;

            [ReadOnly]
            public SingletonAssetContainer<BlobAssetReference<Collider>>.Reader colliders;

            [ReadOnly]
            public SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>>.Reader prefabs;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<PhysicsShapeCollider> physicsShapeColliders;
            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<PhysicsCollider> physicsColliders;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Rotation> rotations;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<Translation> translations;

            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<TagData> tags;

            public void Execute(int index)
            {
                ref var definition = ref results.ElementAt(index).definition.Value;
                SingletonAssetContainerHandle handle;
                handle.instanceID = definition.instanceID;

                ref var prefab = ref prefabs[definition.instanceID].Value;
                PhysicsShapeCollider physicsShapeCollider;
                PhysicsCollider physicsCollider;
                Rotation rotation;
                Translation translation;
                TagData tag;
                Entity entity;
                int i, j, numTriggers, numShapes = definition.shapes.Length;
                for (i = 0; i < numShapes; ++i)
                {
                    ref var sourceShape = ref definition.shapes[i];
                    ref var destinationShape = ref prefab.shapes[i];

                    numTriggers = sourceShape.triggers.Length;
                    for (j = 0; j < numTriggers; ++j)
                    {
                        ref var trigger = ref sourceShape.triggers[j];

                        ref var collider = ref sourceShape.colliders[trigger.index];

                        entity = destinationShape.triggers[j];

                        handle.index = collider.index;

                        if (trigger.contactTolerance > math.FLT_MIN_NORMAL)
                        {
                            physicsShapeCollider.contactTolerance = trigger.contactTolerance;
                            physicsShapeCollider.value = colliders[handle];
                            physicsShapeColliders[entity] = physicsShapeCollider;
                        }
                        else
                        {
                            physicsCollider.Value = colliders[handle];
                            physicsColliders[entity] = physicsCollider;
                        }

                        translation.Value = collider.transform.pos;
                        translations[entity] = translation;

                        rotation.Value = collider.transform.rot;
                        rotations[entity] = rotation;

                        if(!trigger.tag.IsEmpty)
                        {
                            tag.value = trigger.tag;
                            tags[entity] = tag;
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private struct DisposeAll : IJob
        {
            public UnsafeListEx<Result> results;

            public void Execute()
            {
                results.Dispose();
            }
        }

        public delegate void DestroyDelegate(
            in EntityQuery group,
            ref SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>> prefabs,
            ref SystemState systemState);

        public delegate void CreateDelegate(
            int innerloopBatchCount,
            in EntityArchetype eventArchetype,
            in EntityArchetype hitArchetype,
            in EntityQuery group, 
            ref SingletonAssetContainer<BlobAssetReference<Collider>> colliders,
            ref SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>> prefabs,
            ref SystemState systemState);

        //public static readonly DestroyDelegate DestroyFunction = BurstCompiler.CompileFunctionPointer<DestroyDelegate>(Destroy).Invoke;
        //public static readonly CreateDelegate CreateFunction = BurstCompiler.CompileFunctionPointer<CreateDelegate>(Create).Invoke;

        public static void InitQueryAndArchetypes(
            ref SystemState systemState, 
            out EntityQuery groupToCreate,
            out EntityQuery groupToDestroy, 
            out EntityArchetype eventArchetype, 
            out EntityArchetype hitArchetype)
        {
            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                groupToDestroy = builder
                        .WithAll<PhysicsHierarchyID>()
                        .WithNone<PhysicsHierarchyData>()
                        .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                        .Build(ref systemState);

            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                groupToCreate = builder
                    .WithAll<PhysicsHierarchyData>()
                    .WithNone<PhysicsHierarchyID>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                    .Build(ref systemState);

            var entityManager = systemState.EntityManager;

            using (var types = new NativeList<ComponentType>(Allocator.Temp)
            {
                ComponentType.ReadOnly<Prefab>(),
                ComponentType.ReadOnly<PhysicsShapeParent>(),
                ComponentType.ReadOnly<PhysicsCollider>(),
                ComponentType.ReadWrite<Translation>(),
                ComponentType.ReadWrite<Rotation>(),
                ComponentType.ReadWrite<PhysicsVelocity>(),
                ComponentType.ReadWrite<PhysicsTriggerEvent>()
            })
                eventArchetype = entityManager.CreateArchetype(types.AsArray());

            using (var types = new NativeList<ComponentType>(Allocator.Temp)
            {
                ComponentType.ReadOnly<Prefab>(),
                ComponentType.ReadOnly<PhysicsShapeParent>(),
                ComponentType.ReadOnly<PhysicsShapeCollider>(),
                ComponentType.ReadWrite<Translation>(),
                ComponentType.ReadWrite<Rotation>(),
                ComponentType.ReadWrite<PhysicsVelocity>(),
                ComponentType.ReadWrite<PhysicsShapeChildHit>()
            })
                hitArchetype = entityManager.CreateArchetype(types.AsArray());
        }

        //[BurstCompile]
        //[AOT.MonoPInvokeCallback(typeof(DestroyDelegate))]
        public static void Destroy(
            in EntityQuery group,
            ref SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>> prefabs,
            ref SystemState systemState)
        {
            using (var entities = new NativeList<Entity>(Allocator.TempJob))
            {
                prefabs.lookupJobManager.CompleteReadWriteDependency();

                systemState.CompleteDependency();

                CollectToDestroyEx collect;
                collect.entityType = systemState.GetEntityTypeHandle();
                collect.idType = systemState.GetComponentTypeHandle<PhysicsHierarchyID>(true);
                collect.prefabs = prefabs.writer;
                collect.entities = entities;
                collect.Run(group);

                var entityManager = systemState.EntityManager;
                entityManager.DestroyEntity(entities.AsArray());
                entityManager.RemoveComponent<PhysicsHierarchyID>(group);
            }
        }

        //[BurstCompile]
        //[AOT.MonoPInvokeCallback(typeof(CreateDelegate))]
        public static void Create(
            int innerloopBatchCount,
            in EntityArchetype eventArchetype, 
            in EntityArchetype hitArchetype, 
            in EntityQuery group,
            ref SingletonAssetContainer<BlobAssetReference<Collider>> colliders,
            ref SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>> prefabs,
            ref SystemState systemState)
        {
            int entityCount = group.CalculateEntityCount();
            if (entityCount < 1)
                return;

            var results = new UnsafeListEx<Result>(Allocator.TempJob);

            var entityManager = systemState.EntityManager;
            var writer = prefabs.writer;
            int numEvents, numHits;
            using (var eventAndHitCounts = new NativeArray<int>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory))
            using (var ids = new NativeArray<PhysicsHierarchyID>(entityCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
            {
                prefabs.lookupJobManager.CompleteReadWriteDependency();

                systemState.CompleteDependency();

                CollectToCreateEx collect;
                collect.baseEntityIndexArray = group.CalculateBaseEntityIndexArray(Allocator.TempJob);
                collect.entityType = systemState.GetEntityTypeHandle();
                collect.instanceType = systemState.GetComponentTypeHandle<PhysicsHierarchyData>(true);
                collect.prefabs = writer;
                collect.results = results;
                collect.eventAndHitCounts = eventAndHitCounts;
                collect.ids = ids;
                collect.Run(group);

                entityManager.AddComponentDataBurstCompatible(group, ids);

                numEvents = eventAndHitCounts[0];
                numHits = eventAndHitCounts[1];
            }

            int count = numEvents + numHits;
            if (count > 0)
            {
                using (var entityArray = new NativeArray<Entity>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                {
                    entityManager.CreateEntity(eventArchetype, entityArray.GetSubArray(0, numEvents));
                    entityManager.CreateEntity(hitArchetype, entityArray.GetSubArray(numEvents, numHits));
                    {
                        var tagedEntities = new NativeArray<Entity>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

                        Entity entity;
                        int i, j, k, numTriggers, numShapes, numResults = results.length, eventIndex = 0, hitIndex = 0, tagIndex = 0;
                        for (i = 0; i < numResults; ++i)
                        {
                            ref var definition = ref results.ElementAt(i).definition.Value;
                            ref var prefab = ref writer[definition.instanceID].Value;

                            numShapes = definition.shapes.Length;
                            for (j = 0; j < numShapes; ++j)
                            {
                                ref var sourceShape = ref definition.shapes[j];
                                ref var destinationShape = ref prefab.shapes[j];

                                numTriggers = sourceShape.triggers.Length;
                                for (k = 0; k < numTriggers; ++k)
                                {
                                    ref var trigger = ref sourceShape.triggers[k];

                                    entity = entityArray[trigger.contactTolerance > math.FLT_MIN_NORMAL ? numEvents + hitIndex++ : eventIndex++];
                                    destinationShape.triggers[k] = entity;

                                    if (!trigger.tag.IsEmpty)
                                        tagedEntities[tagIndex++] = entity;
                                }
                            }
                        }

                        if (tagedEntities.IsCreated)
                        {
                            var tagedEntityArray = tagedEntities.GetSubArray(0, tagIndex);

                            entityManager.AddComponentBurstCompatible<TagData>(tagedEntityArray);
                            entityManager.AddComponentBurstCompatible<Disabled>(tagedEntityArray);
                        }

                        tagedEntities.Dispose();
                    }
                }

                Init init;
                init.results = results;
                init.colliders = colliders.reader;
                init.prefabs = prefabs.reader;
                init.physicsShapeColliders = systemState.GetComponentLookup<PhysicsShapeCollider>();
                init.physicsColliders = systemState.GetComponentLookup<PhysicsCollider>();
                init.rotations = systemState.GetComponentLookup<Rotation>();
                init.translations = systemState.GetComponentLookup<Translation>();
                init.tags = systemState.GetComponentLookup<TagData>();

                var jobHandle = init.Schedule(results.length, innerloopBatchCount, systemState.Dependency);

                prefabs.lookupJobManager.AddReadOnlyDependency(jobHandle);

                colliders.AddDependency(systemState.GetSystemID(), jobHandle);

                DisposeAll disposeAll;
                disposeAll.results = results;
                systemState.Dependency = disposeAll.Schedule(jobHandle);
            }
            else
                results.Dispose();
        }
    }
}