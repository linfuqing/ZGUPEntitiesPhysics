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
            None, 
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
                            if (triggerIndex < numTriggers)
                            {
                                ref var trigger = ref shape.triggers[triggerIndex];

                                if (trigger.index == j)
                                {
                                    ++triggerIndex;

                                    continue;
                                }
                            }

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

                //hash ^= (uint)destination.GetHashCode();
                //if (hash != source.hash)
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

                    if (compoundCollider.value.IsCreated)
                        source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.Compound;
                    else
                    {
                        if (source.colliderType == PhysicsHierarchyCollidersBitField.ColliderType.Compound)
                        {
                            PhysicsShapeDestroiedCollider destroiedCollider;
                            destroiedCollider.value = compoundColliders[index].value;
                            if (destroiedCollider.value.IsCreated)
                            {
                                destroiedCollider.hash = source.hash;
                                destroiedColliders.Add(destroiedCollider);
                            }
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
                                            if (triggerIndex < numTriggers)
                                            {
                                                ref var trigger = ref shape.triggers[triggerIndex];

                                                if (trigger.index == j)
                                                {
                                                    ++triggerIndex;

                                                    continue;
                                                }
                                            }

                                            ref var collider = ref shape.colliders[j];

                                            handle.index = collider.index;

                                            colliderBlobInstance.CompoundFromChild = collider.transform;
                                            colliderBlobInstance.Collider = colliders[handle];
                                            colliderBlobInstances[colliderCount++] = colliderBlobInstance;
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
                                    source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.None;

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
                        {
                            source.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.None;

                            compoundCollider.value = BlobAssetReference<Collider>.Null;
                        }
                    }

                    compoundColliders[index] = compoundCollider;

                    if (index < results.Length)
                    {
                        PhysicsCollider collider;
                        collider.Value = compoundCollider.value;
                        results[index] = collider;
                    }

                    source.hash = hash;
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

        private ComponentTypeHandle<PhysicsHierarchyData> __instanceType;

        private BufferTypeHandle<PhysicsHierarchyInactiveColliders> __inactiveCollidersType;

        private BufferTypeHandle<PhysicsShapeDestroiedCollider> __destroiedColliderType;

        private ComponentTypeHandle<PhysicsShapeCompoundCollider> __compoundColliderType;

        private ComponentTypeHandle<PhysicsCollider> __resultType;

        private ComponentTypeHandle<PhysicsHierarchyCollidersBitField> __bitFieldType;

        private SingletonAssetContainer<BlobAssetReference<Collider>> __colliders;

        public PhysicsHierarchyColliderSystemCore(ref SystemState state)
        {
            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __group = builder
                        .WithAll<PhysicsHierarchyData, PhysicsHierarchyInactiveColliders>()
                        .WithAllRW<PhysicsHierarchyCollidersBitField, PhysicsShapeCompoundCollider>()
                        .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                        .Build(ref state);

            __group.SetChangedVersionFilter(ComponentType.ReadOnly<PhysicsHierarchyInactiveColliders>());

            __instanceType = state.GetComponentTypeHandle<PhysicsHierarchyData>(true);
            __inactiveCollidersType = state.GetBufferTypeHandle<PhysicsHierarchyInactiveColliders>(true);
            __destroiedColliderType = state.GetBufferTypeHandle<PhysicsShapeDestroiedCollider>();
            __compoundColliderType = state.GetComponentTypeHandle<PhysicsShapeCompoundCollider>();
            __resultType = state.GetComponentTypeHandle<PhysicsCollider>();
            __bitFieldType = state.GetComponentTypeHandle<PhysicsHierarchyCollidersBitField>();

            __colliders = SingletonAssetContainer<BlobAssetReference<Collider>>.Retain();
        }

        public void Dispose()
        {
            __colliders.Release();
        }

        public void Update(ref SystemState state)
        {
            ChangeEx change;
            change.colliders = __colliders.reader;
            change.instanceType = __instanceType.UpdateAsRef(ref state);
            change.inactiveCollidersType = __inactiveCollidersType.UpdateAsRef(ref state);
            change.destroiedColliderType = __destroiedColliderType.UpdateAsRef(ref state);
            change.compoundColliderType = __compoundColliderType.UpdateAsRef(ref state);
            change.resultType = __resultType.UpdateAsRef(ref state);
            change.bitFieldType = __bitFieldType.UpdateAsRef(ref state);

            var jobHandle = change.ScheduleParallelByRef(__group, state.Dependency);

            __colliders.AddDependency(state.GetSystemID(), jobHandle);

            state.Dependency = jobHandle;
        }
    }
}