using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using Unity.Physics.Authoring;
using UnityEngine;
using ZG.Mathematics;
using Unity.Jobs;
using Unity.Burst;

namespace ZG
{
    [Serializable]
    public struct PhysicsShapeCompoundCollider : IComponentData
    {
        public BlobAssetReference<Unity.Physics.Collider> value;
    }

    [Serializable]
    public struct PhysicsShapeParent : IComponentData
    {
        public int index;
        public Entity entity;
    }

    [Serializable]
    public struct PhysicsShapeCollider : IComponentData
    {
        public float contactTolerance;
        public BlobAssetReference<Unity.Physics.Collider> value;
    }

    [Serializable]
    public struct PhysicsShapeColliderBlobInstance : IBufferElementData
    {
        public CompoundCollider.ColliderBlobInstance value;
    }

    [Serializable, InternalBufferCapacity(1)]
    public struct PhysicsShapeChild : IBufferElementData
    {
        [Flags]
        public enum Flag
        {
            ColliderDisabled = 0x01
        }

        public Flag flag;
        public int childIndex;
        public int triggerIndex;
        public int shapeIndex;
        public float contactTolerance;
        public FixedString32Bytes tag;
        public RigidTransform transform;
        public BlobAssetReference<Unity.Physics.Collider> collider;
    }

    [Serializable, InternalBufferCapacity(1)]
    public struct PhysicsShapeChildHit : IBufferElementData
    {
        public RigidBody rigidbody;
        public DistanceHit value;
    }

    [Serializable]
    public struct PhysicsShapeTriggerEventRevicer : IBufferElementData
    {
        public int eventIndex;

        public Entity entity;
    }

    [Serializable]
    public struct PhysicsShapeChildEntity : ICleanupBufferElementData
    {
        public Entity value;
    }

    [Serializable]
    public struct PhysicsShapeDestroiedCollider : ICleanupBufferElementData
    {
        public uint hash;
        public BlobAssetReference<Unity.Physics.Collider> value;
    }

    public interface IPhysicsShapeComponent
    {
        int groupIndex
        {
            get;
        }

        uint belongsTo
        {
            get;
        }

        public uint collidesWith
        {
            get;
        }

        CollisionFilter collisionFilter 
        { 
            get; 
        }
    }

    [EntityComponent(typeof(Translation))]
    [EntityComponent(typeof(Rotation))]
    [EntityComponent(typeof(PhysicsCollider))]
    [EntityComponent(typeof(PhysicsShapeCompoundCollider))]
    [EntityComponent(typeof(PhysicsShapeColliderBlobInstance))]
    [EntityComponent(typeof(PhysicsShapeChild))]
    [EntityComponent(typeof(PhysicsShapeDestroiedCollider))]
    public partial class PhysicsShapeComponent : EntityProxyComponent, IEntityComponent, IPhysicsHierarchyShape, IPhysicsComponent, ISerializationCallbackReceiver
    {
        [Flags]
        internal enum Flag
        {
            Baked = 0x01, 
            TriggerDisabled = 0x02
        }

        [Serializable]
        public struct Trigger
        {
            public int index;
            public int childIndex;
            public FixedString32Bytes tag;
        }

        [Serializable]
        private struct BuildTransform
        {
            public int parentIndex;
            public EntityTransform value;
        }

        [Serializable]
        private struct BuildCollider
        {
            public int parentIndex;
            public CompoundCollider.ColliderBlobInstance value;
        }

        [Serializable]
        private struct BuildChild
        {
            public int parentIndex;
            public PhysicsShapeChild value;
        }

        [Serializable]
        private struct BuildRange
        {
            public int startIndex;
            public int count;
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct ColliderBuild : IJob
        {
            [ReadOnly]
            public NativeSlice<BuildCollider> colliders;

            [ReadOnly]
            public NativeArray<BuildTransform> transforms;

            public NativeArray<BlobAssetReference<Unity.Physics.Collider>> results;

            [Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction]
            public NativeList<CompoundCollider.ColliderBlobInstance> colliderBlobInstances;

            public static BlobAssetReference<Unity.Physics.Collider> Reset(
                in NativeSlice<BuildCollider> colliders, 
                in NativeArray<BuildTransform> transforms, 
                ref NativeArray<CompoundCollider.ColliderBlobInstance> colliderBlobInstances,
                ref bool isChanged)
            {
                int numColliders = colliders.Length;
                int parentIndex;
                BuildCollider collider;
                BuildTransform transform;
                float4x4 localToRoot;
                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                for (int i = 0; i < numColliders; ++i)
                {
                    collider = colliders[i];

                    localToRoot = float4x4.identity;

                    parentIndex = collider.parentIndex;
                    while (parentIndex != -1)
                    {
                        transform = transforms[parentIndex];

                        localToRoot = math.mul(transform.value.matrix, localToRoot);

                        parentIndex = transform.parentIndex;
                    }

                    collider.value.CompoundFromChild = math.mul(Unity.Physics.Math.DecomposeRigidBodyTransform(localToRoot), collider.value.CompoundFromChild);

                    if (!isChanged)
                    {
                        colliderBlobInstance = colliderBlobInstances[i];

                        isChanged = colliderBlobInstance.Collider != collider.value.Collider || !colliderBlobInstance.CompoundFromChild.Equals(collider.value.CompoundFromChild);
                    }

                    if (isChanged)
                        colliderBlobInstances[i] = collider.value;
                }

                if (!isChanged)
                    return BlobAssetReference<Unity.Physics.Collider>.Null;

                BlobAssetReference<Unity.Physics.Collider> result;
                switch (numColliders)
                {
                    case 0:
                        result = BlobAssetReference<Unity.Physics.Collider>.Null;
                        break;
                    case 1:
                        if (colliderBlobInstances[0].CompoundFromChild.Approximately(RigidTransform.identity))
                            result = colliderBlobInstances[0].Collider;
                        else
                            result = CompoundCollider.Create(colliderBlobInstances);
                        break;
                    default:
                        result = CompoundCollider.Create(colliderBlobInstances);
                        break;
                }

                return result;
            }

            public void Execute()
            {
                bool isChanged;
                int numColliders = colliders.Length;
                NativeArray<CompoundCollider.ColliderBlobInstance> colliderBlobInstanceArray;
                if (colliderBlobInstances.IsCreated)
                {
                    isChanged = numColliders != colliderBlobInstances.Length;
                    if (isChanged)
                        colliderBlobInstances.ResizeUninitialized(numColliders);

                    colliderBlobInstanceArray = colliderBlobInstances.AsArray();
                }
                else
                {
                    isChanged = true;

                    colliderBlobInstanceArray = new NativeArray<CompoundCollider.ColliderBlobInstance>(numColliders, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                }

                var result = Reset(colliders, transforms, ref colliderBlobInstanceArray, ref isChanged);

                if (!colliderBlobInstances.IsCreated)
                    colliderBlobInstanceArray.Dispose();

                if (isChanged)
                    results[0] = result;
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct ColliderBuildEx : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<BuildTransform> transforms;

            [ReadOnly]
            public NativeArray<BuildCollider> colliders;

            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<BuildRange> ranges;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [NativeDisableParallelForRestriction]
            public BufferLookup<PhysicsShapeColliderBlobInstance> colliderBlobInstances;

            [NativeDisableParallelForRestriction]
            public BufferLookup<PhysicsShapeDestroiedCollider> oldValues;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<PhysicsShapeCompoundCollider> values;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<PhysicsCollider> results;

            public void Execute(int index)
            {
                Entity entity = entityArray[index];
                if (!this.colliderBlobInstances.HasBuffer(entity))
                    return;

                var colliderBlobInstances = this.colliderBlobInstances[entity];

                int collliderCount = colliderBlobInstances.Length;
                var range = ranges[index];

                bool isChanged = collliderCount != range.count;
                if (isChanged)
                    colliderBlobInstances.ResizeUninitialized(range.count);

                var colliderBlobInstanceArray = colliderBlobInstances.Reinterpret<CompoundCollider.ColliderBlobInstance>().AsNativeArray();
                var value = ColliderBuild.Reset(
                    colliders.Slice(range.startIndex, range.count), 
                    transforms, 
                    ref colliderBlobInstanceArray, 
                    ref isChanged);

                if (isChanged)
                {
                    var oldValue = values[entity];
                    if(oldValue.value.IsCreated && oldValue.value.Value.Type == ColliderType.Compound)
                    {
                        PhysicsShapeDestroiedCollider destroiedCollider;
                        destroiedCollider.hash = 0;
                        destroiedCollider.value = oldValue.value;
                        oldValues[entity].Add(destroiedCollider);
                    }

                    PhysicsShapeCompoundCollider compoundCollider;
                    compoundCollider.value = value;
                    values[entity] = compoundCollider;

                    PhysicsCollider result;
                    result.Value = value;
                    results[entity] = result;
                }
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct ChildBuild : IJob
        {
            [ReadOnly]
            public NativeSlice<BuildChild> inputs;

            [ReadOnly]
            public NativeArray<BuildTransform> transforms;

            public NativeArray<int> result;

            public NativeList<PhysicsShapeChild> outputs;

            public void Execute()
            {
                int length = inputs.Length;
                bool isChanged = length != outputs.Length;
                if (isChanged)
                    outputs.ResizeUninitialized(length);

                int parentIndex;
                float4x4 localToRoot;
                BuildTransform transform;
                BuildChild input;
                PhysicsShapeChild output;
                for (int i = 0; i < length; ++i)
                {
                    input = inputs[i];

                    localToRoot = float4x4.identity;
                    parentIndex = input.parentIndex;
                    while (parentIndex != -1)
                    {
                        transform = transforms[parentIndex];

                        localToRoot = math.mul(transform.value.matrix, localToRoot);

                        parentIndex = transform.parentIndex;
                    }

                    input.value.transform = math.mul(Unity.Physics.Math.DecomposeRigidBodyTransform(localToRoot), input.value.transform);

                    if(!isChanged)
                    {
                        output = outputs[i];
                        isChanged = output.collider != input.value.collider || !output.transform.Equals(input.value.transform);
                    }

                    if(isChanged)
                        outputs[i] = input.value;
                }

                result[0] = isChanged ? 1 : 0;
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct ChildBuildEx : IJob
        {
            [ReadOnly]
            public NativeArray<BuildChild> inputs;

            [ReadOnly]
            public NativeArray<BuildTransform> transforms;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<int> counts;

            public BufferLookup<PhysicsShapeChild> outputs;

            public BufferLookup<PhysicsShapeChildEntity> childEntities;

            public NativeList<Entity> entitiesToDestroy;

            public NativeList<Entity> entitiesToRemove;

            public static bool Reset(
                in NativeSlice<BuildChild> inputs,
                in NativeArray<BuildTransform> transforms,
                ref DynamicBuffer<PhysicsShapeChild> outputs)
            {
                int length = inputs.Length;
                bool isChanged = length != outputs.Length;
                if (isChanged)
                    outputs.ResizeUninitialized(length);

                int parentIndex;
                float4x4 localToRoot;
                BuildTransform transform;
                BuildChild input;
                PhysicsShapeChild output;
                for (int i = 0; i < length; ++i)
                {
                    input = inputs[i];

                    localToRoot = float4x4.identity;
                    parentIndex = input.parentIndex;
                    while (parentIndex != -1)
                    {
                        transform = transforms[parentIndex];

                        localToRoot = math.mul(transform.value.matrix, localToRoot);

                        parentIndex = transform.parentIndex;
                    }

                    input.value.transform = math.mul(Unity.Physics.Math.DecomposeRigidBodyTransform(localToRoot), input.value.transform);

                    if (!isChanged)
                    {
                        output = outputs[i];
                        isChanged = output.collider != input.value.collider || !output.transform.Equals(input.value.transform);
                    }

                    if (isChanged)
                        outputs[i] = input.value;
                }

                return isChanged;
            }

            public void Execute()
            {
                int length = entityArray.Length, index = 0, count;
                Entity entity;
                DynamicBuffer<PhysicsShapeChild> outputs;
                DynamicBuffer<PhysicsShapeChildEntity> childEntities;
                for (int i = 0; i < length; ++i)
                {
                    count = counts[i];
                    entity = entityArray[i];
                    if (entity == Entity.Null)
                    {
                        index += count;

                        continue;
                    }

                    outputs = this.outputs[entity];
                    if (Reset(inputs.Slice(index, count), transforms, ref outputs) &&
                        this.childEntities.HasBuffer(entity))
                    {
                        childEntities = this.childEntities[entity];

                        entitiesToDestroy.AddRange(childEntities.Reinterpret<Entity>().AsNativeArray());

                        entitiesToRemove.Add(entity);
                    }

                    index += count;
                }

                UnityEngine.Assertions.Assert.AreEqual(inputs.Length, index);
            }
        }

        [UpdateInGroup(typeof(EntityCommandSharedSystemGroup))]
        public partial class System : SystemBase
        {
            private class Clear : EntityCommandManager.ICommander
            {
                public JobHandle jobHandle;

                public NativeList<Entity> entitiesToDestroy;
                public NativeList<Entity> entitiesToRemove;

                public Clear()
                {
                    entitiesToDestroy = new NativeList<Entity>(Allocator.Persistent);
                    entitiesToRemove = new NativeList<Entity>(Allocator.Persistent);
                }

                public void Execute(ComponentSystemBase system, ref NativeParallelHashMap<ComponentType, JobHandle> dependency, in JobHandle jobHandle)
                {
                    this.jobHandle.Complete();
                    this.jobHandle = default;

                    var entityManager = system.EntityManager;
                    entityManager.DestroyEntity(entitiesToDestroy.AsArray());
                    entityManager.RemoveComponent<PhysicsShapeChildEntity>(entitiesToRemove.AsArray());

                    entitiesToDestroy.Clear();
                    entitiesToRemove.Clear();
                }

                public void Dispose()
                {
                    entitiesToDestroy.Dispose();
                    entitiesToRemove.Dispose();
                }
            }

            [BurstCompile]
            private struct SetColliders : IJobParallelFor
            {
                [ReadOnly]
                public NativeArray<Entity> entityArray;

                [ReadOnly]
                public ComponentLookup<PhysicsShapeCompoundCollider> inputs;

                [NativeDisableParallelForRestriction]
                public ComponentLookup<PhysicsCollider> outputs;

                public void Execute(int index)
                {
                    Entity entity = entityArray[index];

                    PhysicsCollider result;
                    result.Value = inputs[entity].value;
                    outputs[entity] = result;
                }
            }

            public int innerloopBatchCount = 64;

            private HashSet<PhysicsShapeComponent> __shapesToRebuild;
            private HashSet<PhysicsShapeComponent> __shapesToReset;
            private HashSet<PhysicsShapeComponent> __shapesToRefresh;

            private List<PhysicsShapeComponent> __shapes;

            private Clear __clear;

            public bool MaskRebuild(PhysicsShapeComponent shape)
            {
                UnityEngine.Profiling.Profiler.BeginSample("Mask Rebuild");

                __shapesToReset.Remove(shape);

                bool result = __shapesToRebuild.Add(shape);

                UnityEngine.Profiling.Profiler.EndSample();

                return result;
            }

            public bool MaskReset(PhysicsShapeComponent shape)
            {
                UnityEngine.Profiling.Profiler.BeginSample("Mask Reset");

                bool result;
                if (__shapesToRebuild.Contains(shape))
                    result = false;
                else
                    result = __shapesToReset.Add(shape);

                UnityEngine.Profiling.Profiler.EndSample();

                return result;
            }

            public bool MaskRefresh(PhysicsShapeComponent shape)
            {
                UnityEngine.Profiling.Profiler.BeginSample("Mask Refresh");

                bool result = __shapesToRefresh.Add(shape);

                UnityEngine.Profiling.Profiler.EndSample();

                return result;
            }

            protected override void OnCreate()
            {
                base.OnCreate();

                __shapesToRebuild = new HashSet<PhysicsShapeComponent>();
                __shapesToReset = new HashSet<PhysicsShapeComponent>();
                __shapesToRefresh = new HashSet<PhysicsShapeComponent>();
                __shapes = new List<PhysicsShapeComponent>();
            }

            protected override void OnStartRunning()
            {
                base.OnStartRunning();

                __clear = new Clear();
                World.GetOrCreateSystemManaged<EndEntityObjectSystemGroupEntityCommandSystem>().Create(EntityCommandManager.QUEUE_DESTROY, __clear);
            }

            protected override void OnUpdate()
            {
                JobHandle? jobHandle = null;
                JobHandle inputDeps = Dependency, physicsColliderJobHandle = inputDeps;
                var physicsColliders = GetComponentLookup<PhysicsCollider>();
                var compoundColliders = GetComponentLookup<PhysicsShapeCompoundCollider>();
                if (__shapesToRebuild != null && __shapesToRebuild.Count > 0 || __shapesToReset != null && __shapesToReset.Count > 0)
                {
                    var transforms = new NativeList<BuildTransform>(Allocator.TempJob);
                    var children = new NativeList<BuildChild>(Allocator.TempJob);
                    var entityArray = new NativeList<Entity>(Allocator.TempJob);
                    var childCounts = new NativeList<int>(Allocator.TempJob);
                    int childCount = 0, count;
                    GameObjectEntity gameObjectEntity;
                    NativeList<BuildCollider> colliders = default;
                    NativeArray<BuildRange> ranges = default;
                    if (__shapesToRebuild != null)
                    {
                        __shapes.Clear();

                        int hash;
                        foreach (var shapeToBuild in __shapesToRebuild)
                        {
                            if (shapeToBuild == null || shapeToBuild._parent != null || !shapeToBuild.isActiveAndEnabled)
                                continue;

                            hash = shapeToBuild.hash;
                            if (hash == shapeToBuild.__oldHash)
                            {
                                __shapesToRefresh.Add(shapeToBuild);

                                continue;
                            }

                            shapeToBuild.__oldHash = hash;

                            __shapes.Add(shapeToBuild);
                        }

                        __shapesToRebuild.Clear();

                        int numShapes = __shapes.Count;

                        if (numShapes > 0)
                        {
                            childCounts.ResizeUninitialized(numShapes);
                            entityArray.ResizeUninitialized(numShapes);

                            colliders = new NativeList<BuildCollider>(Allocator.TempJob);
                            ranges = new NativeArray<BuildRange>(numShapes, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                            BuildRange range;
                            range.startIndex = 0;

                            PhysicsShapeComponent shape;
                            for (int i = 0; i < numShapes; ++i)
                            {
                                shape = __shapes[i];

                                __CreateCollider(-1, 0, shape.transform, shape, ref transforms, ref colliders, ref children);

                                count = colliders.Length;
                                range.count = count - range.startIndex;
                                ranges[i] = range;
                                range.startIndex = count;

                                count = children.Length;
                                childCounts[i] = count - childCount;
                                childCount = count;

                                gameObjectEntity = shape.gameObjectEntity;
                                entityArray[i] = gameObjectEntity.isCreated ? gameObjectEntity.entity : Entity.Null;
                            }
                        }
                    }

                    if (__shapesToReset != null)
                    {
                        foreach (var shapeToReset in __shapesToReset)
                        {
                            if (shapeToReset == null || shapeToReset._parent != null || shapeToReset.__system == null || !shapeToReset.isActiveAndEnabled)
                                continue;

                            shapeToReset.__CreateTriggers(-1, 0, shapeToReset.transform, ref transforms, ref children);

                            count = children.Length;
                            childCounts.Add(count - childCount);
                            childCount = count;

                            entityArray.Add(shapeToReset.entity);
                        }

                        __shapesToReset.Clear();
                    }

                    if (ranges.IsCreated)
                    {
                        ColliderBuildEx colliderBuild;
                        colliderBuild.transforms = transforms.AsArray();
                        colliderBuild.colliders = colliders.AsArray();
                        colliderBuild.ranges = ranges;
                        colliderBuild.entityArray = entityArray.AsArray();
                        colliderBuild.colliderBlobInstances = GetBufferLookup<PhysicsShapeColliderBlobInstance>();
                        colliderBuild.oldValues = GetBufferLookup<PhysicsShapeDestroiedCollider>();
                        colliderBuild.values = compoundColliders;
                        colliderBuild.results = physicsColliders;
                        physicsColliderJobHandle = colliderBuild.Schedule(ranges.Length, innerloopBatchCount, inputDeps);

                        jobHandle = colliders.Dispose(physicsColliderJobHandle);
                    }

                    if (childCount > 0)
                    {
                        ChildBuildEx childBuild;
                        childBuild.inputs = children.AsArray();
                        childBuild.transforms = transforms.AsArray();
                        childBuild.entityArray = entityArray.AsArray();
                        childBuild.counts = childCounts.AsArray();
                        childBuild.outputs = GetBufferLookup<PhysicsShapeChild>();
                        childBuild.childEntities = GetBufferLookup<PhysicsShapeChildEntity>();
                        childBuild.entitiesToDestroy = __clear.entitiesToDestroy;
                        childBuild.entitiesToRemove = __clear.entitiesToRemove;
                        inputDeps = childBuild.Schedule(inputDeps);

                        __clear.jobHandle = inputDeps;

                        inputDeps = jobHandle == null ? inputDeps : JobHandle.CombineDependencies(inputDeps, jobHandle.Value);
                    }
                    else if (jobHandle != null)
                        inputDeps = jobHandle.Value;

                    jobHandle = JobHandle.CombineDependencies(
                        childCounts.Dispose(inputDeps),
                        JobHandle.CombineDependencies(entityArray.Dispose(inputDeps), children.Dispose(inputDeps), transforms.Dispose(inputDeps)));
                }

                if (__shapesToRefresh != null && __shapesToRefresh.Count > 0)
                {
                    var entitiesToRefresh = new NativeList<Entity>(Allocator.TempJob);

                    foreach (var shapeToRefresh in __shapesToRefresh)
                    {
                        if (shapeToRefresh == null || shapeToRefresh._parent != null || shapeToRefresh.__system == null || !shapeToRefresh.isActiveAndEnabled)
                            continue;

                        entitiesToRefresh.Add(shapeToRefresh.entity);
                    }

                    __shapesToRefresh.Clear();

                    SetColliders setColliders;
                    setColliders.entityArray = entitiesToRefresh.AsArray();
                    setColliders.inputs = compoundColliders;
                    setColliders.outputs = physicsColliders;
                    physicsColliderJobHandle = setColliders.Schedule(entitiesToRefresh.Length, innerloopBatchCount, physicsColliderJobHandle);

                    physicsColliderJobHandle = entitiesToRefresh.Dispose(physicsColliderJobHandle);

                    jobHandle = jobHandle == null ? physicsColliderJobHandle : JobHandle.CombineDependencies(physicsColliderJobHandle, jobHandle.Value);
                }

                if (jobHandle != null)
                    Dependency = jobHandle.Value;
            }
        }

        //private static HashSet<PhysicsShapeComponent> __shapesToRebuild;

        [Mask, SerializeField]
        internal Flag _flag = 0;

        [SerializeField]
        internal LayerMask _collidesWith = 23608834;

        [SerializeField]
        internal LayerMask _belongsTo = 0;

        [SerializeField]
        internal int _groupIndex = 0;

        //[SerializeField]
        internal float _contactTolerance = 0.0f;

        [SerializeField]
        internal PhysicsShapeComponent _parent;

        [SerializeField, HideInInspector]
        private PhysicsColliders __physicsColliders;
        [SerializeField, HideInInspector]
        private List<UnityEngine.Collider> __colliders;
        [SerializeField, HideInInspector]
        private List<PhysicsShapeAuthoring> __shapes;
        [SerializeField, HideInInspector]
        private List<Trigger> __triggers;
        private HashSet<PhysicsShapeComponent> __children;
        //private NativeList<CompoundCollider.ColliderBlobInstance> __colliderBlobInstances;

        private System __system;

        private int __oldHash;
        private int __hash;

        private int __childIndex = -1;

        public int childIndex
        {
            get
            {
                if(__childIndex == -1)
                {
                    if (_parent != null)
                        __childIndex = _parent.transform.GetLeafIndex(transform);
                }

                return __childIndex;
            }
        }

        public int hash
        {
            get
            {
                if (__children == null || __children.Count < 1)
                {
                    if (__hash == 0)
                        __hash = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

                    return __hash;
                }

                int value = 0;
                foreach (var child in __children)
                    value ^= child.hash;

                return value;
            }
        }

        //public event Action<BlobAssetReference<Unity.Physics.Collider>> onChanged;

        public int groupIndex
        {
            get
            {
                if (_groupIndex == 0)
                {
                    /*var collider = __isBuilding ? BlobAssetReference<Unity.Physics.Collider>.Null : colliders.value;
                    if (collider.IsCreated)
                        return collider.Value.Filter.GroupIndex;*/

                    if (_parent != null)
                        return _parent.groupIndex;
                }

                return _groupIndex;
            }
        }

        public uint belongsTo
        {
            get
            {
                if (_belongsTo == 0)
                {
                    /*var collider = __isBuilding ? BlobAssetReference<Unity.Physics.Collider>.Null : colliders.value;
                    if (collider.IsCreated)
                        return collider.Value.Filter.BelongsTo;*/

                    if (_parent != null)
                        return _parent.belongsTo;
                }

                return (uint)(int)_belongsTo;
            }
        }

        public uint collidesWith
        {
            get
            {
                if (_collidesWith == 0)
                {
                    /*var collider = __isBuilding ? BlobAssetReference<Unity.Physics.Collider>.Null : colliders.value;
                    if (collider.IsCreated)
                        return collider.Value.Filter.CollidesWith;*/

                    if (_parent != null)
                        return _parent.collidesWith;
                }

                return (uint)(int)_collidesWith;
            }
        }

        public CollisionFilter collisionFilter
        {
            get
            {
                if (gameObjectEntity.isCreated)
                {
                    var collider = this.collider;
                    if (collider.IsCreated)
                        return collider.Value.Filter;
                }

                if (_collidesWith == 0 && _parent != null)
                    return _parent.collisionFilter;

                CollisionFilter collisionFilter;
                collisionFilter.GroupIndex = _groupIndex;
                collisionFilter.BelongsTo = (uint)(int)_belongsTo;
                collisionFilter.CollidesWith = (uint)(int)_collidesWith;

                return collisionFilter;
            }
        }

        /*public float3 position
        {
            get
            {
                UnityEngine.Assertions.Assert.IsNull(_parent);

                return this.GetComponentData<Translation>().Value;
            }

            set
            {
                UnityEngine.Assertions.Assert.IsNull(_parent);

                Translation translation;
                translation.Value = value;
                this.SetComponentData(translation);
            }
        }

        public quaternion orientation
        {
            get
            {
                UnityEngine.Assertions.Assert.IsNull(_parent);

                return this.GetComponentData<Rotation>().Value;
            }

            set
            {
                UnityEngine.Assertions.Assert.IsNull(_parent);

                Rotation rotation;
                rotation.Value = value;
                this.SetComponentData(rotation);
            }
        }*/

        public PhysicsShapeComponent parent
        {
            get
            {
                return _parent;
            }

            set
            {
                if (_parent == value)
                    return;

                if (_parent != null)
                {
                    _parent.__children.Remove(this);

                    //_parent.__Rebuild();
                    if(_parent.__system != null)
                        _parent.__system.MaskRebuild(_parent.root);
                }

                _parent = value;

                if (isActiveAndEnabled)
                {
                    if (value != null)
                    {
                        if (value.__children == null)
                            value.__children = new HashSet<PhysicsShapeComponent>();

                        value.__children.Add(this);
                    }

                    //__Rebuild();
                    if (__system != null)
                        __system.MaskRebuild(root);
                }
            }
        }

        public PhysicsShapeComponent root
        {
            get
            {
                if (_parent == null)
                    return this;

                return _parent.root;
            }
        }

        public PhysicsColliders colliders
        {
            get
            {
                /*if (!__Build())
                    __DestroyColliders();*/

                __Build();

                return __physicsColliders;
            }
        }

        public
#if UNITY_EDITOR
            new
#endif
        BlobAssetReference<Unity.Physics.Collider> collider
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    return BlobAssetReference<Unity.Physics.Collider>.Null;
#endif
                return this.GetComponentData<PhysicsShapeCompoundCollider>().value;
            }
        }

        public IReadOnlyList<Trigger> triggers
        {
            get
            {
                /*if (!__Build())
                    __DestroyColliders();*/

                __Build();

                return __triggers;
            }
        }

        public IReadOnlyCollection<PhysicsShapeComponent> children => __children;

        public PhysicsShapeComponent Find(Predicate<PhysicsShapeComponent> predicate)
        {
            if (predicate(this))
                return this;

            if (__children != null)
            {
                PhysicsShapeComponent result;
                foreach (var child in __children)
                {
                    result = child.Find(predicate);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        public PhysicsShapeComponent Get(ref int nodeIndex)
        {
            if (nodeIndex == 0)
                return this;

            --nodeIndex;
            if (__children != null)
            {
                PhysicsShapeComponent result;
                foreach (var child in __children)
                {
                    result = child.Get(ref nodeIndex);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        public void Refresh()
        {
            if (__system == null)
                return;

            if (_parent == null)
            {
                if (isActiveAndEnabled)
                {
                    __system.MaskRefresh(this);

                    //this.RemoveComponent<PhysicsExclude>();
                }
                /*else
                    this.AddComponent<PhysicsExclude>();*/
            }
            else
                _parent.Refresh();
        }

        public BlobAssetReference<Unity.Physics.Collider> CreateCollider()
        {
            BlobAssetReference<Unity.Physics.Collider> result;
            var transforms = new NativeList<BuildTransform>(Allocator.TempJob);
            var children = new NativeList<BuildChild>(Allocator.TempJob);
            {
                result = __CreateCollider(ref transforms, ref children);
            }
            transforms.Dispose();
            children.Dispose();
            return result;
        }

        protected void Awake()
        {
#if UNITY_EDITOR
            if (UnityEditor.PrefabUtility.GetPrefabAssetType(this) != UnityEditor.PrefabAssetType.NotAPrefab)
                return;
#endif

            __Build();

            __DestroyColliders();

            gameObjectEntity.onCreated += __OnCreated;

            //onDispose += __OnDispose;
        }

        /*protected void OnDestroy()
        {
            if (__colliderBlobInstances.IsCreated)
                __colliderBlobInstances.Dispose();
        }*/

        protected void OnRefresh()
        {
            var gameObjectEntity = this.gameObjectEntity;
            Transform transform = gameObjectEntity == null ? null : gameObjectEntity.transform;

            Translation translation;
            translation.Value = transform.position;
            this.SetComponentData(translation);

            Rotation rotation;
            rotation.Value = transform.rotation;
            this.SetComponentData(rotation);
        }

        protected void OnEnable()
        {
            if (_parent == null)
            {
                if (__system != null)
                {
                    //this.RemoveComponent<PhysicsExclude>();

                    __system.MaskRebuild(this);
                }
            }
            else
            {
                if (_parent.__children == null)
                    _parent.__children = new HashSet<PhysicsShapeComponent>();

                if (!_parent.__children.Add(this))
                {
                    Debug.LogError($"{name} of {transform.root.name} attach parent failed!");

                    return;
                }

                if (__system != null)
                    __system.MaskRebuild(root);
            }
        }

        protected void OnDisable()
        {
            if (_parent == null)
            {
                if (__system != null && world.IsCreated)
                {
                    //this.AddComponent<PhysicsExclude>();
                }
            }
            else
            {
                if (_parent.__children != null)
                {
                    if (!_parent.__children.Remove(this))
                    {
                        Debug.LogError($"{name} of {transform.root.name} detach parent failed!");

                        return;
                    }
                }

                if (__system != null)
                    __system.MaskRebuild(root);
            }
        }

        private void __Rebuild()
        {
            if (_parent != null)
                return;

            UnityEngine.Profiling.Profiler.BeginSample("Rebuild");

            var transforms = new NativeList<BuildTransform>(Allocator.TempJob);
            var children = new NativeList<BuildChild>(Allocator.TempJob);
            {
                var collider = __CreateCollider(ref transforms, ref children);

                PhysicsCollider physicsCollider;
                physicsCollider.Value = collider;
                this.SetComponentData(physicsCollider);

                PhysicsShapeCompoundCollider compoundCollider;
                compoundCollider.value = collider;
                this.SetComponentData(compoundCollider);

                __SetChildren(transforms, children);
            }
            children.Dispose();
            transforms.Dispose();

            UnityEngine.Profiling.Profiler.EndSample();
        }

        private bool __Build()
        {
            if (__physicsColliders == null || !__physicsColliders.isCreated)
            {
                UnityEngine.Profiling.Profiler.BeginSample("Build");

                bool isBaked = (_flag & Flag.Baked) == Flag.Baked;
                using (var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.TempJob))
                {
                    var root = this.root.transform;
                    Transform transform = base.transform;

                    Trigger trigger;
                    trigger.index = 0;
                    string tag;

                    UnityEngine.Profiling.Profiler.BeginSample("Build Colliders");

                    __colliders = new List<UnityEngine.Collider>();
                    transform.GetComponentsInChildren<UnityEngine.Collider>(true, __colliders.Add, typeof(PhysicsShapeComponent));

                    if (__colliders.Count > 0)
                    {
                        CollisionFilter collisionFilter = default;
                        collisionFilter.CollidesWith = collidesWith;
                        //这没错
                        collisionFilter.BelongsTo = (uint)(int)_belongsTo;
                        collisionFilter.GroupIndex = _groupIndex;

                        Unity.Physics.Material material = Unity.Physics.Material.Default;
                        material.CollisionResponse = CollisionResponsePolicy.Collide;
                        material.FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.Minimum;
                        material.RestitutionCombinePolicy = Unity.Physics.Material.CombinePolicy.Minimum;
                        material.CustomTags = 0;
                        material.Friction = 0.0f;
                        material.Restitution = 0.0f;
                        __colliders.Convert(
                            colliderBlobInstances,
                            transform,
                            material,
                            collisionFilter,
                            0.0f,
                            0, 
                            0, 
                            isBaked
#if UNITY_EDITOR
                        , false
#endif
                        );

                        foreach (var collider in __colliders)
                        {
                            if (collider.isTrigger)
                            {
                                tag = collider.tag;

                                trigger.tag = tag == "Untagged" ? string.Empty : tag;

                                trigger.childIndex = root.GetLeafIndex(collider.transform);

                                if (__triggers == null)
                                    __triggers = new List<Trigger>();

                                __triggers.Add(trigger);
                            }

                            //Destroy(collider);

                            ++trigger.index;
                        }
                    }

                    UnityEngine.Profiling.Profiler.EndSample();

                    UnityEngine.Profiling.Profiler.BeginSample("Build Shapes");

                    __shapes = new List<PhysicsShapeAuthoring>();
                    transform.GetComponentsInChildren<PhysicsShapeAuthoring>(true, __shapes.Add, typeof(PhysicsShapeComponent));

                    if (__shapes.Count > 0)
                    {
                        __shapes.Convert(
                           colliderBlobInstances,
                           transform,
                           _groupIndex,
                           0, 
                           0, 
                           isBaked
#if UNITY_EDITOR
                            , false
#endif
                        );

                        foreach (var shape in __shapes)
                        {
                            if (shape.CollisionResponse == CollisionResponsePolicy.RaiseTriggerEvents)
                            {
                                tag = shape.tag;

                                trigger.tag = tag == "Untagged" ? string.Empty : tag;

                                trigger.childIndex = root.GetLeafIndex(shape.transform);

                                if (__triggers == null)
                                    __triggers = new List<Trigger>();

                                __triggers.Add(trigger);
                            }

                            //Destroy(shape);

                            ++trigger.index;
                        }
                    }

                    UnityEngine.Profiling.Profiler.EndSample();

                    UnityEngine.Profiling.Profiler.BeginSample("Create ColliderBlobInstances");

                    __physicsColliders = PhysicsColliders.Create(colliderBlobInstances.AsArray(), true);

                    UnityEngine.Profiling.Profiler.EndSample();
                }

#if UNITY_EDITOR
                UnityEngine.Profiling.Profiler.BeginSample("Name");

                __physicsColliders.name = transform.root.name;

                UnityEngine.Profiling.Profiler.EndSample();
#endif

                UnityEngine.Profiling.Profiler.EndSample();

                return true;
            }

            return false;
        }

        private void __DestroyChildren()
        {
            UnityEngine.Profiling.Profiler.BeginSample("Destroy Children");

            var childEntities = new NativeList<PhysicsShapeChildEntity>(Allocator.Temp);
            {
                NativeListWriteOnlyWrapper<PhysicsShapeChildEntity> wrapper;
                if (this.TryGetBuffer<PhysicsShapeChildEntity, NativeList<PhysicsShapeChildEntity>, NativeListWriteOnlyWrapper<PhysicsShapeChildEntity>>(
                    ref childEntities,
                    ref wrapper))
                {
                    UnityEngine.Profiling.Profiler.BeginSample("Destroy Child Entities");

                    this.DestroyEntity(childEntities.AsArray().Reinterpret<Entity>());

                    UnityEngine.Profiling.Profiler.EndSample();

                    UnityEngine.Profiling.Profiler.BeginSample("Remove Child Entity Components");

                    this.RemoveComponent<PhysicsShapeChildEntity>();

                    UnityEngine.Profiling.Profiler.EndSample();
                }
            }
            childEntities.Dispose();

            UnityEngine.Profiling.Profiler.EndSample();
        }

        private void __DestroyColliders()
        {
            if (__colliders != null)
            {
                foreach (var collider in __colliders)
                    Destroy(collider);

                __colliders = null;
            }

            if (__shapes != null)
            {
                foreach (var shape in __shapes)
                    Destroy(shape);

                __shapes = null;
            }

        }

        private BlobAssetReference<Unity.Physics.Collider> __CreateCollider(
            ref NativeList<BuildTransform> transforms, 
            ref NativeList<BuildChild> children)
        {
            BlobAssetReference<Unity.Physics.Collider> collider;

            //int length = __colliderBlobInstances.IsCreated ? __colliderBlobInstances.Length : 1;
            var colliders = new NativeList<BuildCollider>(/*length, */Allocator.TempJob);
            {
                UnityEngine.Profiling.Profiler.BeginSample("Set ColliderBlobInstances");

                __CreateCollider(-1, 0, transform, this, ref transforms, ref colliders, ref children);

                UnityEngine.Profiling.Profiler.EndSample();

                var result = new NativeArray<BlobAssetReference<Unity.Physics.Collider>>(1, Allocator.TempJob);
                {
                    result[0] = this.collider;

                    /*if (!__colliderBlobInstances.IsCreated)
                        __colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.Persistent);*/

                    ColliderBuild build;
                    build.transforms = transforms.AsArray();
                    build.colliders = colliders.AsArray();
                    build.colliderBlobInstances = default;// __colliderBlobInstances;
                    build.results = result;
                    build.Run();

                    collider = result[0];
                }
                result.Dispose();
            }
            colliders.Dispose();

            return collider;
        }

        private int __CreateTriggers(
            int parentIndex, 
            int nodeIndex, 
            Transform root, 
            ref NativeList<BuildTransform> transforms, 
            ref NativeList<BuildChild> children)
        {
            var transform = base.transform;
            parentIndex = __GetHirectory(parentIndex, transform, root, ref transforms);

            int length = __triggers == null ? 0 : __triggers.Count;
            if (length > 0)
            {
                //Transform root = gameObjectEntity.transform, transform = base.transform;
                //var childTransform = math.RigidTransform(transform.GetRotationOf(root), transform.GetPositionOf(root));
                Trigger trigger;
                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                BuildChild child;
                child.parentIndex = parentIndex;
                for (int i = 0; i < length; ++i)
                {
                    trigger = __triggers[i];

                    colliderBlobInstance = __physicsColliders.values[trigger.index];
                    //colliderBlobInstance.CompoundFromChild = math.mul(childTransform, colliderBlobInstance.CompoundFromChild);

                    child.value.flag = 0;
                    if ((_flag & Flag.TriggerDisabled) == Flag.TriggerDisabled)
                        child.value.flag |= PhysicsShapeChild.Flag.ColliderDisabled;

                    child.value.childIndex = trigger.childIndex;
                    child.value.triggerIndex = i;
                    child.value.shapeIndex = nodeIndex;
                    child.value.contactTolerance = _contactTolerance;
                    child.value.tag = trigger.tag;
                    child.value.transform = colliderBlobInstance.CompoundFromChild;
                    child.value.collider = colliderBlobInstance.Collider;

                    children.Add(child);
                }
            }

            ++nodeIndex;

            if (__children != null)
            {
                foreach (var child in __children)
                    nodeIndex = child.__CreateTriggers(
                        parentIndex,
                        nodeIndex, 
                        transform, 
                        ref transforms, 
                        ref children);
            }

            return nodeIndex;
        }

        private void __SetChildren(in NativeList<BuildTransform> transforms, in NativeList<BuildChild> children)
        {
            UnityEngine.Profiling.Profiler.BeginSample("Set Children");

            var outputs = new NativeList<PhysicsShapeChild>(children.Length, Allocator.TempJob);
            {
                bool isCreated = gameObjectEntity.isCreated;
                NativeListWriteOnlyWrapper<PhysicsShapeChild> wrapper;
                if (!isCreated || 
                    this.TryGetBuffer<PhysicsShapeChild, NativeList<PhysicsShapeChild>, NativeListWriteOnlyWrapper<PhysicsShapeChild>>(
                    ref outputs,
                    ref wrapper))
                {
                    var result = new NativeArray<int>(1, Allocator.TempJob);
                    {
                        UnityEngine.Profiling.Profiler.BeginSample("Child Build");

                        ChildBuild build;
                        build.transforms = transforms.AsArray();
                        build.inputs = children.AsArray();
                        build.result = result;
                        build.outputs = outputs;
                        build.Run();

                        UnityEngine.Profiling.Profiler.EndSample();

                        UnityEngine.Profiling.Profiler.BeginSample("Set Buffer");

                        if (isCreated && result[0] != 0)
                        {
                            __DestroyChildren();

                            this.SetBuffer(outputs.AsArray());
                        }

                        UnityEngine.Profiling.Profiler.EndSample();
                    }
                    result.Dispose();
                }
            }
            outputs.Dispose();

            UnityEngine.Profiling.Profiler.EndSample();
        }

        private void __OnCreated()
        {
            __system = world.GetExistingSystemManaged<System>();
            __system.MaskRebuild(root);
        }

        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            if (_parent == null)
            {
                var transform = gameObjectEntity.transform;

                Translation translation;
                translation.Value = transform.position;
                assigner.SetComponentData(entity, translation);

                //必须
                Rotation rotation;
                rotation.Value = transform.rotation;
                assigner.SetComponentData(entity, rotation);
            }

            /*__system = world.GetExistingSystem<System>();
            __system.MaskRebuild(root);*/
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            if (!Application.isPlaying)
                return;

            __Build();
        }

        private static int __CreateCollider(
            int parentIndex,
            int nodeIndex,
            Transform root, 
            PhysicsShapeComponent instance,
            ref NativeList<BuildTransform> transforms, 
            ref NativeList<BuildCollider> colliders,
            ref NativeList<BuildChild> children)
        {
            instance.__Build();

            var transform = instance.transform;
            parentIndex = __GetHirectory(parentIndex, transform, root, ref transforms);

            /*Transform transform = instance.transform;
            BuildTransform buildTransform;
            buildTransform.parentIndex = parentIndex++;
            buildTransform.value = transform.GetEntityTransform();
            transforms.Add(buildTransform);*/

            UnityEngine.Profiling.Profiler.BeginSample("Create Colliders");

            int length = instance.__physicsColliders == null ? 0 : instance.__physicsColliders.length;
            if (length > 0)
            {
                int numTriggers = instance.__triggers == null ? 0 : instance.__triggers.Count;
                Trigger trigger;
                if (numTriggers > 0)
                    trigger = instance.__triggers[0];
                else
                {
                    trigger.index = -1;
                    trigger.childIndex = 0;
                    trigger.tag = string.Empty;
                }

                int index = 0;
                //CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                BuildChild child;
                BuildCollider collider;
                collider.parentIndex = parentIndex;
                child.parentIndex = parentIndex;
                //RigidTransform childTransform = math.RigidTransform(transform.GetRotationOf(root), transform.GetPositionOf(root));
                for (int i = 0; i < length; ++i)
                {
                    collider.value = instance.__physicsColliders[i];
                    UnityEngine.Assertions.Assert.IsTrue(collider.value.Collider.IsCreated, instance.name);
                    UnityEngine.Assertions.Assert.IsFalse(collider.value.Collider.Value.Filter.IsEmpty, instance.name);
                    //colliderBlobInstance.CompoundFromChild = math.mul(childTransform, colliderBlobInstance.CompoundFromChild);
                    if (i == trigger.index)
                    {
                        if (children.IsCreated)
                        {
                            child.value.flag = 0;
                            if ((instance._flag & Flag.TriggerDisabled) == Flag.TriggerDisabled)
                                child.value.flag |= PhysicsShapeChild.Flag.ColliderDisabled;

                            child.value.childIndex = trigger.childIndex;
                            child.value.triggerIndex = index;
                            child.value.shapeIndex = nodeIndex;
                            child.value.contactTolerance = instance._contactTolerance;
                            child.value.tag = trigger.tag;
                            child.value.transform = collider.value.CompoundFromChild;
                            child.value.collider = collider.value.Collider;

                            children.Add(child);
                        }

                        if (++index < numTriggers)
                            trigger = instance.__triggers[index];
                    }
                    else
                        colliders.Add(collider);
                }
            }

            UnityEngine.Profiling.Profiler.EndSample();

            ++nodeIndex;

            if (instance.__children != null)
            {
                foreach (var child in instance.__children)
                    nodeIndex = __CreateCollider(parentIndex, nodeIndex, transform, child, ref transforms, ref colliders, ref children);
            }

            return nodeIndex;
        }

        private static int __GetHirectory(int parentIndex, Transform transform, Transform root, ref NativeList<BuildTransform> transforms)
        {
            if (transform == root)
                return parentIndex;

            parentIndex = __GetHirectory(parentIndex, transform.parent, root, ref transforms);

            BuildTransform buildTransform;
            buildTransform.parentIndex = parentIndex;
            buildTransform.value = transform.GetEntityTransform();

            parentIndex = transforms.Length;

            transforms.Add(buildTransform);

            return parentIndex;
        }

        CollisionFilter IPhysicsHierarchyShape.collisionFilter
        {
            get
            {

                CollisionFilter collisionFilter = default;
                collisionFilter.CollidesWith = collidesWith;
                //这没错
                collisionFilter.BelongsTo = (uint)(int)_belongsTo;
                collisionFilter.GroupIndex = _groupIndex;

                return collisionFilter;
            }
        }

        Unity.Physics.Material IPhysicsHierarchyShape.material
        {
            get
            {
                Unity.Physics.Material material = Unity.Physics.Material.Default;
                material.CollisionResponse = CollisionResponsePolicy.Collide;
                material.FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.Minimum;
                material.RestitutionCombinePolicy = Unity.Physics.Material.CombinePolicy.Minimum;
                material.CustomTags = 0;
                material.Friction = 0.0f;
                material.Restitution = 0.0f;

                return material;
            }
        }

        float IPhysicsHierarchyShape.contactTolerance
        {
            get => _contactTolerance;
        }

        /*PhysicsHierarchyTriggerFlag IPhysicsHierarchyShape.triggerFlag
        {
            get => (_flag & Flag.TriggerDisabled) == Flag.TriggerDisabled ? PhysicsHierarchyTriggerFlag.Disabled : 0;
        }*/
        /*#if UNITY_EDITOR
            private class DrawComponent : DisplayBodyColliders.DrawComponent
            {
                public new static Mesh CachedReferenceSphere
                {
                    get
                    {
                        return DisplayBodyColliders.DrawComponent.CachedReferenceSphere;
                    }
                }

                public new static Mesh CachedReferenceCylinder
                {
                    get
                    {
                        return DisplayBodyColliders.DrawComponent.CachedReferenceCylinder;
                    }
                }
            }

            private unsafe void OnDrawGizmos()
            {
                if (!Application.isPlaying || !isActiveAndEnabled || _parent != null)
                    return;

                var gameObjectEntity = base.gameObjectEntity;
                if (gameObjectEntity.status != ZG.GameObjectEntity.Status.Init)
                    return;

                foreach (var gameObject in UnityEditor.Selection.gameObjects)
                {
                    if (gameObject.GetComponentInChildren<PhysicsShapeComponent>() != null)
                        return;
                }

                EntityManager entityManager = gameObjectEntity.entityManager;
                Entity entity = gameObjectEntity.entity;
                if (entityManager.HasComponent<PhysicsCollider>(entity))
                {
                    Gizmos.color = world.Name == "Client" ? Color.grey : Color.black;
                    var collider = entityManager.GetComponentData<PhysicsCollider>(entity).ColliderPtr;
                    if(collider == null)
                    {
                        if (entityManager.GetBuffer<PhysicsShapeChildEntity>(entity).Length < 1)
                            Debug.LogError(base.transform.root.name);

                        return;
                    }

                    var displayResults = DrawComponent.BuildDebugDisplayMesh(collider);
                    var transform = math.RigidTransform(orientation, position);
                    foreach (var dr in displayResults)
                    {
                        Vector3 position = math.transform(transform, dr.Position);
                        Quaternion orientation = math.mul(transform.rot, dr.Orientation);
                        Gizmos.DrawMesh(dr.Mesh, position, math.normalize(orientation), dr.Scale);
                        if (dr.Mesh != DrawComponent.CachedReferenceSphere && dr.Mesh != DrawComponent.CachedReferenceCylinder)
                            Destroy(dr.Mesh);
                    }
                }
            }

            private unsafe void OnDrawGizmosSelected()
            {
                if (!Application.isPlaying || !isActiveAndEnabled || _parent != null)
                    return;

                var gameObjectEntity = base.gameObjectEntity;
                if (gameObjectEntity.status != ZG.GameObjectEntity.Status.Init)
                    return;

                EntityManager entityManager = gameObjectEntity.entityManager;
                Entity entity = gameObjectEntity.entity;
                if (entityManager.HasComponent<PhysicsCollider>(entity))
                {
                    Gizmos.color = Color.white;
                    var displayResults = DrawComponent.BuildDebugDisplayMesh(entityManager.GetComponentData<PhysicsCollider>(entity).ColliderPtr);
                    var transform = math.RigidTransform(orientation, position);
                    foreach (var dr in displayResults)
                    {
                        Vector3 position = math.transform(transform, dr.Position);
                        Quaternion orientation = math.mul(transform.rot, dr.Orientation);
                        Gizmos.DrawWireMesh(dr.Mesh, position, orientation, dr.Scale);
                        if (dr.Mesh != DrawComponent.CachedReferenceSphere && dr.Mesh != DrawComponent.CachedReferenceCylinder)
                            Destroy(dr.Mesh);
                    }
                }

                if (entityManager.HasComponent<PhysicsShapeChildEntity>(entity))
                {
                    var children = entityManager.GetBuffer<PhysicsShapeChildEntity>(entity);
                    int numChildren = children.Length;
                    for (int i = 0; i < numChildren; ++i)
                    {
                        entity = children[i].value;

                        BlobAssetReference<Unity.Physics.Collider> collider;
                        if (entityManager.HasComponent<PhysicsTriggerEvent>(entity))
                        {
                            Gizmos.color = entityManager.GetBuffer<PhysicsTriggerEvent>(entity).Length > 0 ? Color.green : Color.red;

                            collider = entityManager.GetComponentData<PhysicsCollider>(entity).Value;
                        }
                        else if (entityManager.HasComponent<PhysicsShapeChildHit>(entity))
                        {
                            Gizmos.color = entityManager.GetBuffer<PhysicsShapeChildHit>(entity).Length > 0 ? Color.green : Color.red;

                            collider = entityManager.GetComponentData<PhysicsShapeCollider>(entity).value;
                        }
                        else
                        {
                            Gizmos.color = Color.yellow;

                            collider = BlobAssetReference<Unity.Physics.Collider>.Null;
                        }

                        var displayResults = DrawComponent.BuildDebugDisplayMesh((Unity.Physics.Collider*)collider.GetUnsafePtr());
                        var transform = math.RigidTransform(entityManager.GetComponentData<Rotation>(entity).Value, entityManager.GetComponentData<Translation>(entity).Value);
                        foreach (var dr in displayResults)
                        {
                            Vector3 position = math.transform(transform, dr.Position);
                            Quaternion orientation = math.mul(transform.rot, dr.Orientation);
                            Gizmos.DrawWireMesh(dr.Mesh, position, orientation, dr.Scale);
                            if (dr.Mesh != DrawComponent.CachedReferenceSphere && dr.Mesh != DrawComponent.CachedReferenceCylinder)
                                Destroy(dr.Mesh);
                        }
                    }
                }
            }
        #endif*/
    }
}