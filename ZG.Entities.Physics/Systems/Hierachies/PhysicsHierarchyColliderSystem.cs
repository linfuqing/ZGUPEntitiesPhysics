using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using BitField = ZG.BitField<Unity.Collections.FixedBytes126>;

namespace ZG
{
    public struct PhysicsHierarchyCollidersBitField : IComponentData
    {
        public enum ColliderType
        {
            Convex, 
            Compound
        }

        public ColliderType colliderType;
        public uint hash;
        public BitField value;
    }

    public struct PhysicsHierarchyInactiveColliders : IBufferElementData
    {
        public int shapeIndex;
    }

    public struct PhysicsHierarchyColliderSystemCore
    {
        private struct Change
        {
            [ReadOnly]
            public SingletonAssetContainer<BlobAssetReference<Collider>>.Reader colliders;

            [ReadOnly]
            public NativeArray<PhysicsHierarchyData> instances;

            [ReadOnly]
            public BufferAccessor<PhysicsHierarchyInactiveColliders> inactiveColliders;

            public BufferAccessor<PhysicsShapeDestroiedCollider> destroiedColliders;

            public NativeArray<PhysicsShapeCompoundCollider> compoundColliders;

            public NativeArray<PhysicsCollider> results;

            public NativeArray<PhysicsHierarchyCollidersBitField> bitFields;

            public void Execute(int index)
            {
                ref var definition = ref instances[index].definition.Value;

                BitField destination = default;

                int i, numShapes = definition.shapes.Length;
                if (index < inactiveColliders.Length)
                {
                    var inactiveColliders = this.inactiveColliders[index];

                    int inactiveShapeIndex,
                        numInactiveColliders = inactiveColliders.Length;
                    for (i = 0; i < numInactiveColliders; ++i)
                    {
                        inactiveShapeIndex = inactiveColliders[i].shapeIndex;
                        if (inactiveShapeIndex < 0 || inactiveShapeIndex >= numShapes)
                            continue;

                        destination.Set(inactiveShapeIndex);
                    }
                }

                var source = bitFields[index];
                if (source.value == destination)
                    return;

                uint hash = 0;
                int j, numColliders, numTriggers, triggerIndex, colliderCount = 0;
                for (i = 0; i < numShapes; ++i)
                {
                    if (destination.Test(i))
                        continue;

                    ref var shape = ref definition.shapes[i];

                    numColliders = shape.colliders.Length;
                    numTriggers = shape.triggers.Length;
                    if (numTriggers > 0)
                    {
                        triggerIndex = 0;
                        for (j = 0; j < numColliders; ++j)
                        {
                            ref var trigger = ref shape.triggers[triggerIndex];

                            if (trigger.index == j)
                                ++triggerIndex;
                            else
                                hash ^= shape.colliders[j].hash;
                        }

                        numColliders -= numTriggers;
                    }
                    else
                    {
                        for (j = 0; j < numColliders; ++j)
                            hash ^= shape.colliders[j].hash;
                    }

                    colliderCount += numColliders;
                }

                if (hash != source.hash)
                {
                    PhysicsShapeCompoundCollider compoundCollider;
                    compoundCollider.value = BlobAssetReference<Collider>.Null;

                    var destroiedColliders = this.destroiedColliders[index];
                    int numDestroiedColliders = destroiedColliders.Length;
                    for (i = 0; i < numDestroiedColliders; ++i)
                    {
                        ref var destroiedCollider = ref destroiedColliders.ElementAt(i);
                        if (destroiedCollider.hash == hash)
                        {
                            compoundCollider.value = destroiedCollider.value;

                            destroiedColliders.RemoveAtSwapBack(i);

                            //--numDestroiedColliders;

                            break;
                        }
                    }

                    source.hash = hash;
                    if (compoundCollider.value.IsCreated)
                        source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.Compound;
                    else
                    {
                        if (source.colliderType == PhysicsHierarchyCollidersBitField.ColliderType.Compound)
                        {
                            PhysicsShapeDestroiedCollider destroiedCollider;
                            destroiedCollider.hash = source.hash;
                            destroiedCollider.value = compoundColliders[index].value;
                            destroiedColliders.Add(destroiedCollider);
                        }

                        NativeArray<CompoundCollider.ColliderBlobInstance> colliderBlobInstances;
                        if (colliderCount > 0)
                        {
                            colliderBlobInstances = new NativeArray<CompoundCollider.ColliderBlobInstance>(colliderCount, Allocator.Temp);
                            {
                                colliderCount = 0;

                                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                                SingletonAssetContainerHandle handle;
                                handle.instanceID = definition.instanceID;
                                for (i = 0; i < numShapes; ++i)
                                {
                                    if (destination.Test(i))
                                        continue;

                                    ref var shape = ref definition.shapes[i];

                                    numColliders = shape.colliders.Length;
                                    numTriggers = shape.triggers.Length;
                                    if (numTriggers > 0)
                                    {
                                        triggerIndex = 0;
                                        for (j = 0; j < numColliders; ++j)
                                        {
                                            ref var trigger = ref shape.triggers[triggerIndex];

                                            if (trigger.index == j)
                                                ++triggerIndex;
                                            else
                                            {
                                                ref var collider = ref shape.colliders[j];

                                                handle.index = collider.index;

                                                colliderBlobInstance.CompoundFromChild = collider.transform;
                                                colliderBlobInstance.Collider = colliders[handle];
                                                colliderBlobInstances[colliderCount++] = colliderBlobInstance;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        for (j = 0; j < numColliders; ++j)
                                        {
                                            ref var collider = ref shape.colliders[j];

                                            handle.index = collider.index;

                                            colliderBlobInstance.CompoundFromChild = collider.transform;
                                            colliderBlobInstance.Collider = colliders[handle];
                                            colliderBlobInstances[colliderCount++] = colliderBlobInstance;
                                        }
                                    }
                                }
                            }
                        }
                        else
                            colliderBlobInstances = default;

                        if (colliderBlobInstances.IsCreated)
                        {
                            switch (colliderBlobInstances.Length)
                            {
                                case 0:
                                    source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.Convex;

                                    compoundCollider.value = BlobAssetReference<Collider>.Null;
                                    break;
                                case 1:
                                    var colliderBlobInstance = colliderBlobInstances[0];
                                    if (Mathematics.Math.Approximately(colliderBlobInstance.CompoundFromChild, RigidTransform.identity))
                                    {
                                        source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.Convex;

                                        compoundCollider.value = colliderBlobInstance.Collider;
                                    }
                                    else
                                    {
                                        source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.Compound;

                                        compoundCollider.value = CompoundCollider.Create(colliderBlobInstances);
                                    }
                                    break;
                                default:
                                    source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.Compound;

                                    compoundCollider.value = CompoundCollider.Create(colliderBlobInstances);
                                    break;
                            }

                            colliderBlobInstances.Dispose();
                        }
                        else
                            compoundCollider.value = BlobAssetReference<Collider>.Null;
                    }

                    compoundColliders[index] = compoundCollider;

                    if (index < results.Length)
                    {
                        PhysicsCollider collider;
                        collider.Value = compoundCollider.value;
                        results[index] = collider;
                    }
                }

                source.value = destination;
                bitFields[index] = source;
            }
        }

        [BurstCompile]
        private struct ChangeEx : IJobChunk
        {
            [ReadOnly]
            public SingletonAssetContainer<BlobAssetReference<Collider>>.Reader colliders;

            [ReadOnly]
            public ComponentTypeHandle<PhysicsHierarchyData> instanceType;

            [ReadOnly]
            public BufferTypeHandle<PhysicsHierarchyInactiveColliders> inactiveCollidersType;

            public BufferTypeHandle<PhysicsShapeDestroiedCollider> destroiedColliderType;

            public ComponentTypeHandle<PhysicsShapeCompoundCollider> compoundColliderType;

            public ComponentTypeHandle<PhysicsCollider> resultType;

            public ComponentTypeHandle<PhysicsHierarchyCollidersBitField> bitFieldType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Change change;

                change.colliders = colliders;
                change.instances = chunk.GetNativeArray(ref instanceType);
                change.inactiveColliders = chunk.GetBufferAccessor(ref inactiveCollidersType);
                change.destroiedColliders = chunk.GetBufferAccessor(ref destroiedColliderType);
                change.compoundColliders = chunk.GetNativeArray(ref compoundColliderType);
                change.results = chunk.GetNativeArray(ref resultType);
                change.bitFields = chunk.GetNativeArray(ref bitFieldType);

                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                    change.Execute(i);
            }
        }

        private EntityQuery __group;
        private SingletonAssetContainer<BlobAssetReference<Collider>> __colliders;

        public PhysicsHierarchyColliderSystemCore(ref SystemState state)
        {
            __group = state.GetEntityQuery(
                new EntityQueryDesc()
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<PhysicsHierarchyData>(),
                        ComponentType.ReadOnly<PhysicsHierarchyInactiveColliders>(),
                        ComponentType.ReadWrite<PhysicsHierarchyCollidersBitField>(),
                        ComponentType.ReadWrite<PhysicsShapeCompoundCollider>()
                    }, 
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
            __group.SetChangedVersionFilter(typeof(PhysicsHierarchyInactiveColliders));

            __colliders = SingletonAssetContainer<BlobAssetReference<Collider>>.instance;
        }

        public void Update(ref SystemState state)
        {
            ChangeEx change;
            change.colliders = __colliders.reader;
            change.instanceType = state.GetComponentTypeHandle<PhysicsHierarchyData>(true);
            change.inactiveCollidersType = state.GetBufferTypeHandle<PhysicsHierarchyInactiveColliders>(true);
            change.destroiedColliderType = state.GetBufferTypeHandle<PhysicsShapeDestroiedCollider>();
            change.compoundColliderType = state.GetComponentTypeHandle<PhysicsShapeCompoundCollider>();
            change.resultType = state.GetComponentTypeHandle<PhysicsCollider>();
            change.bitFieldType = state.GetComponentTypeHandle<PhysicsHierarchyCollidersBitField>();

            var jobHandle = change.ScheduleParallel(__group, state.Dependency);

            __colliders.AddDependency(state.GetSystemID(), jobHandle);

            state.Dependency = jobHandle;
        }
    }
}