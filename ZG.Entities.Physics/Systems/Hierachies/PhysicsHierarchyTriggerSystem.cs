using System;
using Unity.Jobs;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Profiling;
using BitField = ZG.BitField<Unity.Collections.FixedBytes126>;

namespace ZG
{
    public struct PhysicsHierarchyTriggersBitField : IComponentData
    {
        public BitField value;
    }

    public struct PhysicsHierarchyInactiveTriggers : IBufferElementData
    {
        public int shapeIndex;
    }

    //[UpdateInGroup(typeof(EntityObjectSystemGroup), OrderLast = true), UpdateAfter(typeof(EndEntityObjectSystemGroupEntityCommandSystem))]
    [UpdateInGroup(typeof(InitializationSystemGroup))/*, UpdateAfter(typeof(BeginFrameEntityCommandSystem))*/]
    public partial class PhysicsHierarchyTriggerSystemGroup : ComponentSystemGroup
    {

    }

    [BurstCompile, UpdateInGroup(typeof(PhysicsHierarchyTriggerSystemGroup), OrderFirst = true)]
    public partial struct PhysicsHierarchyTriggerFactroySystem : ISystem
    {
        public static readonly int InnerloopBatchCount = 1;

        private EntityArchetype __hitArchetype;
        private EntityArchetype __eventArchetype;
        private EntityQuery __groupToDestroy;
        private EntityQuery __groupToCreate;

        private SingletonAssetContainer<BlobAssetReference<Collider>> __colliders;

        public SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>> prefabs
        {
            get;

            private set;
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            PhysicsHierarchyUtility.InitQueryAndArchetypes(
                ref state, 
                out __groupToCreate, 
                out __groupToDestroy, 
                out __eventArchetype, 
                out __hitArchetype);

            __colliders = SingletonAssetContainer<BlobAssetReference<Collider>>.Retain();

            prefabs = new SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>>(Allocator.Persistent);
        }

        //[BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            prefabs.Dispose();

            __colliders.Release();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var prefabs = this.prefabs;
            
            if(!__groupToDestroy.IsEmptyIgnoreFilter)
                PhysicsHierarchyUtility.Destroy(__groupToDestroy, ref prefabs, ref state);

            if (!__groupToCreate.IsEmptyIgnoreFilter)
                PhysicsHierarchyUtility.Create(
                    InnerloopBatchCount, 
                    __eventArchetype, 
                    __hitArchetype, 
                    __groupToCreate,
                    ref __colliders,
                    ref prefabs,
                    ref state);
        }
    }

    [BurstCompile, CreateAfter(typeof(PhysicsHierarchyTriggerFactroySystem)), UpdateInGroup(typeof(PhysicsHierarchyTriggerSystemGroup))]
    public partial struct PhysicsHierarchyTriggerSystem : ISystem
    {
        private struct Key : IEquatable<Key>
        {
            public int instanceID;

            public int shapeIndex;

            public bool Equals(Key other)
            {
                return instanceID == other.instanceID && shapeIndex == other.shapeIndex;
            }

            public override int GetHashCode()
            {
                return instanceID ^ shapeIndex;
            }
        }

        private struct Value
        {
            public BitField bitField;
            public Entity entity;
        }

        private struct Disable
        {
            public NativeArray<PhysicsHierarchyTriggersBitField> bitFields;

            public BufferAccessor<PhysicsShapeChild> shapeChildren;

            public BufferAccessor<PhysicsShapeChildEntity> shapeChildEntities;

            public NativeList<Entity> triggerEntitiesToDestroy;

            public void Execute(int index)
            {
                PhysicsHierarchyTriggersBitField bitField;
                bitField.value = BitField.Max;
                bitFields[index] = bitField;

                if (index < shapeChildren.Length)
                    shapeChildren[index].Clear();

                if (index < shapeChildEntities.Length)
                {
                    var shapeChildEntities = this.shapeChildEntities[index];
                    triggerEntitiesToDestroy.AddRange(shapeChildEntities.Reinterpret<Entity>().AsNativeArray());
                    shapeChildEntities.Clear();
                }
            }
        }

        [BurstCompile]
        private struct DisableEx : IJobChunk
        {
            public ComponentTypeHandle<PhysicsHierarchyTriggersBitField> bitFieldType;

            public BufferTypeHandle<PhysicsShapeChild> shapeChildType;

            public BufferTypeHandle<PhysicsShapeChildEntity> shapeChildEntityType;

            public NativeList<Entity> triggerEntitiesToDestroy;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Disable disable;
                disable.bitFields = chunk.GetNativeArray(ref bitFieldType);
                disable.shapeChildren = chunk.GetBufferAccessor(ref shapeChildType);
                disable.shapeChildEntities = chunk.GetBufferAccessor(ref shapeChildEntityType);
                disable.triggerEntitiesToDestroy = triggerEntitiesToDestroy;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    disable.Execute(i);
            }
        }

        private struct Enable
        {
            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<PhysicsHierarchyData> instances;

            [ReadOnly]
            public NativeArray<PhysicsHierarchyTriggersBitField> bitFields;

            [ReadOnly]
            public BufferAccessor<PhysicsHierarchyInactiveTriggers> inactiveTriggers;

            [ReadOnly]
            public BufferAccessor<PhysicsShapeChild> shapeChildren;

            [ReadOnly]
            public BufferAccessor<PhysicsShapeChildEntity> shapeChildEntities;

            public NativeArray<int> triggerCount;

            public NativeParallelMultiHashMap<Key, Entity> triggerEntitiesToCreate;

            public NativeList<Entity> triggerEntitiesToDestroy;

            public NativeList<Value> values;

            public void Execute(int index)
            {
                ref var definition = ref instances[index].definition.Value;

                BitField destination = default;

                int numShapes = definition.shapes.Length;
                if (index < this.inactiveTriggers.Length)
                {
                    var inactiveTriggers = this.inactiveTriggers[index];

                    int inactiveShapeIndex,
                        numInactiveShapes = inactiveTriggers.Length;
                    for (int i = 0; i < numInactiveShapes; ++i)
                    {
                        inactiveShapeIndex = inactiveTriggers[i].shapeIndex;
                        if (inactiveShapeIndex < 0 || inactiveShapeIndex >= numShapes)
                            continue;

                        destination.Set(inactiveShapeIndex);
                    }
                }

                var source = bitFields[index];
                if (source.value == destination)
                    return;

                Entity entity = entityArray[index];

                Value value;
                value.bitField = destination;
                value.entity = entity;
                values.Add(value);

                Key key;
                key.instanceID = definition.instanceID;

                var diff = source.value ^ destination;
                int highestBit = math.min(diff.highestBit, numShapes), triggerCount = 0, numTriggers;

                //UnityEngine.Debug.LogError($"highestBit {highestBit} : {numShapes}, s: {source.value.value.offset0000.byte0000}, d : {destination.value.offset0000.byte0000}");

                for (int i = diff.lowerstBit - 1; i < highestBit; ++i)
                {
                    if (!diff.Test(i))
                    {
                        //UnityEngine.Debug.LogError($"Diff {i}");

                        continue;
                    }

                    numTriggers = definition.shapes[i].triggers.Length;
                    if (numTriggers < 1)
                    {
                        //UnityEngine.Debug.LogError($"No Triggers {i}");

                        continue;
                    }

                    if(source.value.Test(i))
                    {
                        //UnityEngine.Debug.LogError($"Add {i}");

                        triggerCount += numTriggers;

                        key.shapeIndex = i;

                        triggerEntitiesToCreate.Add(key, entity);
                    }
                    /*else
                    {
                        UnityEngine.Debug.LogError($"Remove {i}");
                    }*/
                }

                this.triggerCount[0] += triggerCount;

                var shapeChildEntities = this.shapeChildEntities[index];
                //if (shapeChildEntities.Length > index)
                {
                    var shapeChildren = this.shapeChildren[index];
                    int numShapeChildren = shapeChildren.Length;
                    for (int i = 0; i < numShapeChildren; ++i)
                    {
                        if (destination.Test(shapeChildren[i].shapeIndex))
                            triggerEntitiesToDestroy.Add(shapeChildEntities[i].value);
                    }
                }
            }
        }

        [BurstCompile]
        private struct EnableEx : IJobChunk
        {
            //public uint lastSystemVersion;

            [ReadOnly]
            public EntityTypeHandle entityType;

            [ReadOnly]
            public ComponentTypeHandle<PhysicsHierarchyData> instanceType;

            [ReadOnly]
            public ComponentTypeHandle<PhysicsHierarchyTriggersBitField> bitFieldType;

            [ReadOnly]
            public BufferTypeHandle<PhysicsHierarchyInactiveTriggers> inactiveTriggersType;

            [ReadOnly]
            public BufferTypeHandle<PhysicsShapeChild> shapeChildType;

            [ReadOnly]
            public BufferTypeHandle<PhysicsShapeChildEntity> shapeChildEntityType;

            public NativeArray<int> triggerCount;

            public NativeParallelMultiHashMap<Key, Entity> triggerEntitiesToCreate;

            public NativeList<Entity> triggerEntitiesToDestroy;

            public NativeList<Value> values;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                /*if (!batchInChunk.DidOrderChange(lastSystemVersion) && 
                    !batchInChunk.DidChange(bitFieldType, lastSystemVersion) &&
                    !batchInChunk.DidChange(inactiveTriggersType, lastSystemVersion))
                    return;*/

                Enable enable;
                enable.entityArray = chunk.GetNativeArray(entityType);
                enable.instances = chunk.GetNativeArray(ref instanceType);
                enable.bitFields = chunk.GetNativeArray(ref bitFieldType);
                enable.inactiveTriggers = chunk.GetBufferAccessor(ref inactiveTriggersType);
                enable.shapeChildren = chunk.GetBufferAccessor(ref shapeChildType);
                enable.shapeChildEntities = chunk.GetBufferAccessor(ref shapeChildEntityType);
                enable.triggerCount = triggerCount;
                enable.triggerEntitiesToCreate = triggerEntitiesToCreate;
                enable.triggerEntitiesToDestroy = triggerEntitiesToDestroy;
                enable.values = values;

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    enable.Execute(i);
            }
        }

        [BurstCompile]
        private struct Init : IJobParallelFor
        {
            [ReadOnly]
            public SingletonAssetContainer<BlobAssetReference<Collider>>.Reader colliders;

            [ReadOnly]
            public NativeParallelMultiHashMap<Key, Entity> triggerEntities;

            [ReadOnly]
            public NativeParallelHashMap<Key, int> triggerEntityOffsets;

            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<Entity> triggerEntityArray;

            [ReadOnly]
            public NativeArray<Value> values;

            [ReadOnly]
            public ComponentLookup<PhysicsHierarchyData> instances;

            [NativeDisableParallelForRestriction]
            public BufferLookup<PhysicsShapeDestroiedCollider> destroiedColliders;

            [NativeDisableParallelForRestriction]
            public BufferLookup<PhysicsShapeChild> children;

            [NativeDisableParallelForRestriction]
            public BufferLookup<PhysicsShapeChildEntity> childEntities;

            /*public NativeArray<PhysicsShapeCompoundCollider> compoundColliders;

            public NativeArray<PhysicsCollider> results;*/

            [NativeDisableParallelForRestriction]
            public ComponentLookup<PhysicsHierarchyTriggersBitField> bitFields;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<PhysicsShapeParent> parents;

            public void Execute(int index)
            {
                var value = values[index];
                ref var definition = ref instances[value.entity].definition.Value;

                var bitField = bitFields[value.entity];
                if (bitField.value == value.bitField)
                    return;

                Key key;
                key.instanceID = definition.instanceID;

                SingletonAssetContainerHandle handle;
                handle.instanceID = definition.instanceID;
                
                var childEntities = this.childEntities[value.entity];
                var children = this.children[value.entity];
                //var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.Temp);
                {
                    bool isSource, isDestination;
                    int i, j, 
                        triggerEntityOffset, 
                        numTriggerEntities, 
                        numColliders, 
                        numTriggers, 
                        triggerIndex, 
                        childIndex = 0, 
                        nextChildIndex = 0, 
                        numShapes = definition.shapes.Length;
                    //uint hash = 0;
                    Entity triggerEntity;
                    NativeParallelMultiHashMapIterator<Key> iterator;
                    PhysicsShapeParent parent;
                    PhysicsShapeChild child;
                    PhysicsShapeChildEntity childEntity;
                    //CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                    for (i = 0; i < numShapes; ++i)
                    {
                        ref var shape = ref definition.shapes[i];

                        isSource = bitField.value.Test(i);
                        isDestination = value.bitField.Test(i);

                        numTriggers = shape.triggers.Length;

                        if(isDestination == isSource)
                        {
                            childIndex += numTriggers;

                            if (!isDestination)
                            {
                                numColliders = shape.colliders.Length;
                                if (numTriggers > 0)
                                {
                                    triggerIndex = 0;
                                    for (j = 0; j < numColliders; ++j)
                                    {
                                        ref var collider = ref shape.colliders[j];
                                        ref var trigger = ref shape.triggers[triggerIndex];

                                        handle.index = collider.index;
                                        if (trigger.index == j)
                                        {
                                            parent.index = nextChildIndex;
                                            parent.entity = value.entity;
                                            parents[childEntities[nextChildIndex].value] = parent;

                                            ++nextChildIndex;

                                            ++triggerIndex;
                                        }
                                        /*else
                                        {
                                            hash ^= collider.hash;

                                            colliderBlobInstance.CompoundFromChild = collider.transform;
                                            colliderBlobInstance.Collider = colliders[handle];
                                            colliderBlobInstances.Add(colliderBlobInstance);
                                        }*/
                                    }
                                }
                                /*else
                                {
                                    for (j = 0; j < numColliders; ++j)
                                    {
                                        ref var collider = ref shape.colliders[j];

                                        handle.index = collider.index;

                                        hash ^= collider.hash;

                                        colliderBlobInstance.CompoundFromChild = collider.transform;
                                        colliderBlobInstance.Collider = colliders[handle];
                                        colliderBlobInstances.Add(colliderBlobInstance);
                                    }
                                }*/
                            }
                        }
                        else if (isSource)
                        {
                            numColliders = shape.colliders.Length;
                            if (numTriggers > 0)
                            {
                                key.shapeIndex = i;

                                /*if(!triggerEntityOffsets.ContainsKey(key))
                                {
                                    UnityEngine.Debug.LogError($"??? {key.shapeIndex}, s: {bitField.value.value.offset0000.byte0000}, d: {value.bitField.value.offset0000.byte0000}");
                                }*/

                                triggerEntityOffset = triggerEntityOffsets[key];

                                numTriggerEntities = 0;
                                if (triggerEntities.TryGetFirstValue(key, out triggerEntity, out iterator))
                                {
                                    isSource = false;
                                    do
                                    {
                                        if (!isSource)
                                        {
                                            if (triggerEntity == value.entity)
                                                isSource = true;
                                            else
                                                ++triggerEntityOffset;
                                        }

                                        ++numTriggerEntities;
                                    } while (triggerEntities.TryGetNextValue(out triggerEntity, ref iterator));
                                }

                                if (!isSource)
                                    UnityEngine.Debug.LogError($"WTF??? {key.shapeIndex}, s: {bitField.value.value.offset0000.byte0000}, d: {value.bitField.value.offset0000.byte0000}");

                                triggerIndex = 0;
                                for (j = 0; j < numColliders; ++j)
                                {
                                    ref var collider = ref shape.colliders[j];
                                    ref var trigger = ref shape.triggers[triggerIndex];

                                    handle.index = collider.index;
                                    if (trigger.index == j)
                                    {
                                        child.flag = 0;
                                        /*if ((trigger.flag & PhysicsHierarchyTriggerFlag.Disabled) == PhysicsHierarchyTriggerFlag.Disabled)
                                            child.flag |= PhysicsShapeChild.Flag.ColliderDisabled;*/

                                        child.childIndex = childIndex++;
                                        child.triggerIndex = triggerIndex++;
                                        child.shapeIndex = i;
                                        child.contactTolerance = trigger.contactTolerance;
                                        child.tag = trigger.tag;
                                        child.transform = collider.transform;

                                        child.collider = colliders[handle];
                                        children.Insert(nextChildIndex, child);

                                        childEntity.value = triggerEntityArray[triggerEntityOffset];
                                        childEntities.Insert(nextChildIndex, childEntity);

                                        parent.index = nextChildIndex;
                                        parent.entity = value.entity;
                                        parents[childEntity.value] = parent;

                                        triggerEntityOffset += numTriggerEntities;

                                        ++nextChildIndex;
                                    }
                                    /*else
                                    {
                                        hash ^= collider.hash;

                                        colliderBlobInstance.CompoundFromChild = collider.transform;
                                        colliderBlobInstance.Collider = colliders[handle];
                                        colliderBlobInstances.Add(colliderBlobInstance);
                                    }*/
                                }
                            }
                            /*else
                            {
                                for (j = 0; j < numColliders; ++j)
                                {
                                    ref var collider = ref shape.colliders[j];

                                    handle.index = collider.index;

                                    colliderBlobInstance.CompoundFromChild = collider.transform;
                                    colliderBlobInstance.Collider = colliders[handle];
                                    colliderBlobInstances.Add(colliderBlobInstance);
                                }
                            }*/
                        }
                        else
                        {
                            childIndex += numTriggers;

                            for (j = 0; j < numTriggers; ++j)
                            {
                                children.RemoveAt(nextChildIndex);

                                childEntities.RemoveAt(nextChildIndex);
                            }
                        }
                    }

                    /*BlobAssetReference<Collider> result;
                    var compoundCollider = compoundColliders[index];
                    if (compoundCollider.hash == hash)
                        result = compoundCollider.value;
                    else
                    {
                        compoundCollider.hash = hash;

                        if (compoundCollider.value.IsCreated && compoundCollider.value.Value.CollisionType == CollisionType.Composite)
                        {
                            PhysicsShapeDestroiedCollider destroiedCollider;
                            destroiedCollider.value = compoundCollider.value;
                            destroiedColliders[index].Add(destroiedCollider);
                        }

                        switch (colliderBlobInstances.Length)
                        {
                            case 0:
                                result = BlobAssetReference<Collider>.Null;
                                break;
                            case 1:
                                ref var colliderBlobInstanceTemp = ref colliderBlobInstances.ElementAt(0);
                                if (Mathematics.Math.Approximately(colliderBlobInstanceTemp.CompoundFromChild, RigidTransform.identity))
                                    result = colliderBlobInstanceTemp.Collider;
                                else
                                    result = CompoundCollider.Create(colliderBlobInstances);
                                break;
                            default:
                                result = CompoundCollider.Create(colliderBlobInstances);
                                break;
                        }

                        compoundCollider.value = result;

                        compoundColliders[index] = compoundCollider;
                    }

                    if (index < results.Length)
                    {
                        PhysicsCollider collider;
                        collider.Value = result;
                        results[index] = collider;
                    }*/
                }
                /*if(colliderBlobInstances.IsCreated)
                    colliderBlobInstances.Dispose();*/
                
                bitField.value = value.bitField;
                bitFields[value.entity] = bitField;
            }
        }

        public static readonly int InnerloopBatchCount = 1;

        private EntityQuery __groupToInit;
        private EntityQuery __groupToDisable;
        private EntityQuery __groupToEnable;

        private EntityTypeHandle __entityType;

        private ComponentTypeHandle<PhysicsHierarchyData> __instanceType;

        private ComponentTypeHandle<PhysicsHierarchyTriggersBitField> __bitFieldType;

        private BufferTypeHandle<PhysicsHierarchyInactiveTriggers> __inactiveTriggersType;

        private BufferTypeHandle<PhysicsShapeChild> __shapeChildType;

        private BufferTypeHandle<PhysicsShapeChildEntity> __shapeChildEntityType;

        private ComponentLookup<PhysicsHierarchyData> __instances;

        private BufferLookup<PhysicsShapeDestroiedCollider> __destroiedColliders;

        private BufferLookup<PhysicsShapeChild> __children;

        private BufferLookup<PhysicsShapeChildEntity> __childEntities;

        private ComponentLookup<PhysicsHierarchyTriggersBitField> __bitFields;

        private ComponentLookup<PhysicsShapeParent> __parents;

        private SingletonAssetContainer<BlobAssetReference<Collider>> __colliders;
        private SharedHashMap<int, BlobAssetReference<PhysicsHierarchyPrefab>> __prefabs;
        private NativeParallelHashMap<Key, int> __triggerEntityOffsets;
        private NativeParallelMultiHashMap<Key, Entity> __triggerEntitiesToCreate;
        private NativeList<Entity> __triggerEntitiesToDestroy;
        private NativeArray<int> __triggerCount;
        private NativeList<Value> __values;

#if ENABLE_PROFILER
        private ProfilerMarker __init;
        private ProfilerMarker __disable;
        private ProfilerMarker __enable;
        private ProfilerMarker __instantiate;
#endif

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            BurstUtility.InitializeJobParallelFor<Init>();

            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __groupToInit = builder
                        .WithAll<PhysicsHierarchyTriggersBitField, PhysicsShapeChild>()
                        .WithNone<PhysicsShapeChildEntity>()
                        .Build(ref state);

            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __groupToDisable = builder
                    .WithAll<Disabled>()
                    .WithAllRW<PhysicsHierarchyTriggersBitField, PhysicsShapeChildEntity>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                    .Build(ref state);
            //__groupToDisable.SetChangedVersionFilter(ComponentType.ReadOnly<Disabled>());

            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __groupToEnable = builder
                    .WithAll<PhysicsHierarchyData, PhysicsHierarchyInactiveTriggers>()
                    .WithAllRW<PhysicsHierarchyTriggersBitField>()
                    .Build(ref state);
            __groupToEnable.SetChangedVersionFilter(ComponentType.ReadOnly<PhysicsHierarchyInactiveTriggers>());
            __groupToEnable.AddOrderVersionFilter();

            __entityType = state.GetEntityTypeHandle();
            __instanceType = state.GetComponentTypeHandle<PhysicsHierarchyData>(true);
            __bitFieldType = state.GetComponentTypeHandle<PhysicsHierarchyTriggersBitField>();
            __inactiveTriggersType = state.GetBufferTypeHandle<PhysicsHierarchyInactiveTriggers>();
            __shapeChildType = state.GetBufferTypeHandle<PhysicsShapeChild>();
            __shapeChildEntityType = state.GetBufferTypeHandle<PhysicsShapeChildEntity>();

            __instances = state.GetComponentLookup<PhysicsHierarchyData>(true);
            __destroiedColliders = state.GetBufferLookup<PhysicsShapeDestroiedCollider>();
            __children = state.GetBufferLookup<PhysicsShapeChild>();
            __childEntities = state.GetBufferLookup<PhysicsShapeChildEntity>();
            __bitFields = state.GetComponentLookup<PhysicsHierarchyTriggersBitField>();
            __parents = state.GetComponentLookup<PhysicsShapeParent>();

            __colliders = SingletonAssetContainer<BlobAssetReference<Collider>>.instance;

            __prefabs = state.WorldUnmanaged.GetExistingSystemUnmanaged<PhysicsHierarchyTriggerFactroySystem>().prefabs;

            __triggerCount = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            __triggerEntitiesToCreate = new NativeParallelMultiHashMap<Key, Entity>(1, Allocator.Persistent);
            __triggerEntityOffsets = new NativeParallelHashMap<Key, int>(1, Allocator.Persistent);
            __triggerEntitiesToDestroy = new NativeList<Entity>(Allocator.Persistent);
            __values = new NativeList<Value>(Allocator.Persistent);

#if ENABLE_PROFILER
            __init = new ProfilerMarker("Physics Triggers Init");
            __disable = new ProfilerMarker("Physics Triggers Enable");
            __enable = new ProfilerMarker("Physics Triggers Disable");
            __instantiate = new ProfilerMarker("Physics Triggers Instantiate");
#endif
        }

        //[BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            __values.Dispose();
            __triggerEntitiesToDestroy.Dispose();
            __triggerEntitiesToCreate.Dispose();
            __triggerEntityOffsets.Dispose();
            __triggerCount.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;

#if ENABLE_PROFILER
            using (__init.Auto())
#endif
            {
                entityManager.AddComponent<PhysicsShapeChildEntity>(__groupToInit);
            }

            __triggerEntitiesToDestroy.Clear();

#if ENABLE_PROFILER
            using (__disable.Auto())
#endif
            {
                if (!__groupToDisable.IsEmpty)
                {
                    state.CompleteDependency();

                    DisableEx disable;
                    disable.bitFieldType = __bitFieldType.UpdateAsRef(ref state);
                    disable.shapeChildType = __shapeChildType.UpdateAsRef(ref state);
                    disable.shapeChildEntityType = __shapeChildEntityType.UpdateAsRef(ref state);
                    disable.triggerEntitiesToDestroy = __triggerEntitiesToDestroy;

                    disable.RunByRef(__groupToDisable);
                }
            }

#if ENABLE_PROFILER
            using (__enable.Auto())
#endif
            {
                if (__groupToEnable.IsEmpty)
                    entityManager.DestroyEntity(__triggerEntitiesToDestroy.AsArray());
                else
                {
                    state.CompleteDependency();

                    __triggerEntitiesToCreate.Clear();

                    __values.Clear();

                    __triggerCount[0] = 0;

                    EnableEx enable;
                    enable.entityType = __entityType.UpdateAsRef(ref state);
                    enable.instanceType = __instanceType.UpdateAsRef(ref state);
                    enable.bitFieldType = __bitFieldType.UpdateAsRef(ref state);
                    enable.inactiveTriggersType = __inactiveTriggersType.UpdateAsRef(ref state);
                    enable.shapeChildType = __shapeChildType.UpdateAsRef(ref state);
                    enable.shapeChildEntityType = __shapeChildEntityType.UpdateAsRef(ref state);
                    enable.triggerCount = __triggerCount;
                    enable.triggerEntitiesToCreate = __triggerEntitiesToCreate;
                    enable.triggerEntitiesToDestroy = __triggerEntitiesToDestroy;
                    enable.values = __values;
                    enable.RunByRef(__groupToEnable);

                    entityManager.DestroyEntity(__triggerEntitiesToDestroy.AsArray());

                    if (__values.Length > 0)
                    {
                        var triggerEntityArray = new NativeArray<Entity>(__triggerCount[0], Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                        __triggerEntityOffsets.Clear();

#if ENABLE_PROFILER
                        using (__instantiate.Auto())
#endif
                        using (var keys = __triggerEntitiesToCreate.GetKeyArray(Allocator.Temp))
                        {
                            __prefabs.lookupJobManager.CompleteReadOnlyDependency();

                            var prefabs = __prefabs.reader;
                            Key key;
                            int count = keys.ConvertToUniqueArray(), entityOffset = 0, numEntities, numTriggers, i, j;
                            for (i = 0; i < count; ++i)
                            {
                                key = keys[i];

                                __triggerEntityOffsets[key] = entityOffset;

                                numEntities = __triggerEntitiesToCreate.CountValuesForKey(key);

                                ref var shape = ref prefabs[key.instanceID].Value.shapes[key.shapeIndex];
                                numTriggers = shape.triggers.Length;
                                for (j = 0; j < numTriggers; ++j)
                                {
                                    entityManager.Instantiate(shape.triggers[j], triggerEntityArray.GetSubArray(entityOffset, numEntities));

                                    entityOffset += numEntities;
                                }
                            }
                        }

                        Init init;
                        init.colliders = __colliders.reader;
                        init.triggerEntities = __triggerEntitiesToCreate;
                        init.triggerEntityOffsets = __triggerEntityOffsets;
                        init.triggerEntityArray = triggerEntityArray;
                        init.values = __values.AsArray();
                        init.instances = __instances.UpdateAsRef(ref state);
                        init.destroiedColliders = __destroiedColliders.UpdateAsRef(ref state);
                        init.children = __children.UpdateAsRef(ref state);
                        init.childEntities = __childEntities.UpdateAsRef(ref state);
                        init.bitFields = __bitFields.UpdateAsRef(ref state);
                        init.parents = __parents.UpdateAsRef(ref state);

                        var jobHandle = init.ScheduleByRef(__values.Length, InnerloopBatchCount, state.Dependency);

                        __colliders.AddDependency(state.GetSystemID(), jobHandle);

                        state.Dependency = jobHandle;
                    }
                }
            }
        }
    }
}