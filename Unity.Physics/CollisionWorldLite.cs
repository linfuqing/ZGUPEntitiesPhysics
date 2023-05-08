using System;
using Unity.Assertions;
using Unity.Jobs;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Physics;
using Math = Unity.Physics.Math;

namespace ZG
{
    [BurstCompile]
    public struct CopyHashMapJob<TKey, TValue> : IJob
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        [ReadOnly]
        public NativeParallelHashMap<TKey, TValue> source;
        [WriteOnly]
        public NativeParallelHashMap<TKey, TValue> destination;

        public void Execute()
        {
            destination.Clear();

            var keyValueArrays = source.GetKeyValueArrays(Allocator.Temp);
            int length = keyValueArrays.Length;
            for (int i = 0; i < length; ++i)
                destination[keyValueArrays.Keys[i]] = keyValueArrays.Values[i];
        }
    }

    public static partial class JobUtility
    {
        public static JobHandle CopyFrom<TKey, TValue>(this ref NativeParallelHashMap<TKey, TValue> destination, in NativeParallelHashMap<TKey, TValue> source, in JobHandle inputDeps)
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged
        {
            CopyHashMapJob<TKey, TValue> copyHashMapJob;
            copyHashMapJob.source = source;
            copyHashMapJob.destination = destination;

            return copyHashMapJob.Schedule(inputDeps);
        }
    }

    // A collection of rigid bodies wrapped by a bounding volume hierarchy.
    // This allows to do collision queries such as raycasting, overlap testing, etc.
    [NoAlias]
    public struct CollisionWorldLite : IDisposable
    {
        [BurstCompile]
        private struct CreateDefaultStaticRigidbody : IJob
        {
            public int rigidbodyIndex;

            [NativeDisableContainerSafetyRestriction]
            public NativeArray<RigidBody> rigidbodies;

            [NativeDisableContainerSafetyRestriction]
            public NativeParallelHashMap<Entity, int>.ParallelWriter entityBodyIndexMap;

            public void Execute()
            {
                rigidbodies[rigidbodyIndex] = new RigidBody
                {
                    WorldFromBody = new RigidTransform(quaternion.identity, float3.zero),
                    Collider = BlobAssetReference<Collider>.Null,
                    Entity = Entity.Null,
                    CustomTags = 0
                };
                entityBodyIndexMap.TryAdd(Entity.Null, rigidbodyIndex);
            }
        }

        [BurstCompile]
        private struct CheckStaticBodyChangesJob : IJobChunk
        {
            [ReadOnly] 
            public ComponentTypeHandle<LocalToWorld> localToWorldType;
            [ReadOnly] 
            public ComponentTypeHandle<Translation> positionType;
            [ReadOnly] 
            public ComponentTypeHandle<Rotation> rotationType;
            [ReadOnly] 
            public ComponentTypeHandle<PhysicsCollider> physicsColliderType;

            [NativeDisableParallelForRestriction]
            public NativeArray<int> result;

            public uint lastSystemVersion;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                bool didBatchChange =
                    chunk.DidChange(ref localToWorldType, lastSystemVersion) ||
                    chunk.DidChange(ref positionType, lastSystemVersion) ||
                    chunk.DidChange(ref rotationType, lastSystemVersion) ||
                    chunk.DidChange(ref physicsColliderType, lastSystemVersion) ||
                    chunk.DidOrderChange(lastSystemVersion);
                if (didBatchChange)
                {
                    // Note that multiple worker threads may be running at the same time.
                    // They either write 1 to Result[0] or not write at all.  In case multiple
                    // threads are writing 1 to this variable, in C#, reads or writes of int
                    // data type are atomic, which guarantees that Result[0] is 1.
                    result[0] = 1;
                }
            }
        }

        [BurstCompile]
        private struct CreateRigidbodies : IJobChunk
        {
            [ReadOnly] 
            public EntityTypeHandle entityType;
            [ReadOnly] 
            public ComponentTypeHandle<LocalToWorld> localToWorldType;
            [ReadOnly] 
            public ComponentTypeHandle<Parent> parentType;
            [ReadOnly] 
            public ComponentTypeHandle<Translation> positionType;
            [ReadOnly] 
            public ComponentTypeHandle<Rotation> rotationType;
            [ReadOnly] 
            public ComponentTypeHandle<PhysicsCollider> physicsColliderType;
            [ReadOnly] 
            public ComponentTypeHandle<PhysicsCustomTags> physicsCustomTagsType;

            [ReadOnly] 
            public int firstBodyIndex;

            [ReadOnly]
            public NativeArray<int> chunkBaseEntityIndices;

            [NativeDisableContainerSafetyRestriction]
            public NativeArray<RigidBody> rigidbodies;

            [NativeDisableContainerSafetyRestriction]
            public NativeParallelHashMap<Entity, int>.ParallelWriter entityBodyIndexMap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var chunkColliders = chunk.GetNativeArray(ref physicsColliderType);
                var chunkLocalToWorlds = chunk.GetNativeArray(ref localToWorldType);
                var chunkPositions = chunk.GetNativeArray(ref positionType);
                var chunkRotations = chunk.GetNativeArray(ref rotationType);
                var chunkEntities = chunk.GetNativeArray(entityType);
                var chunkCustomTags = chunk.GetNativeArray(ref physicsCustomTagsType);

                bool hasChunkPhysicsColliderType = chunk.Has(ref physicsColliderType);
                bool hasChunkPhysicsCustomTagsType = chunk.Has(ref physicsCustomTagsType);
                bool hasChunkParentType = chunk.Has(ref parentType);
                bool hasChunkLocalToWorldType = chunk.Has(ref localToWorldType);
                bool hasChunkPositionType = chunk.Has(ref positionType);
                bool hasChunkRotationType = chunk.Has(ref rotationType);

                RigidBody rigidbody;
                RigidTransform worldFromBody = RigidTransform.identity;
                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                int index = firstBodyIndex + chunkBaseEntityIndices[unfilteredChunkIndex];
                while (iterator.NextEntityIndex(out int i))
                {
                    // if entities are in a transform hierarchy then Translation/Rotation are in the space of their parents
                    // in that case, LocalToWorld is the only common denominator for world space
                    if (hasChunkParentType)
                    {
                        if (hasChunkLocalToWorldType)
                        {
                            var localToWorld = chunkLocalToWorlds[i];
                            worldFromBody = Math.DecomposeRigidBodyTransform(localToWorld.Value);
                        }
                    }
                    else
                    {
                        if (hasChunkPositionType)
                            worldFromBody.pos = chunkPositions[i].Value;
                        else if (hasChunkLocalToWorldType)
                            worldFromBody.pos = chunkLocalToWorlds[i].Position;

                        if (hasChunkRotationType)
                            worldFromBody.rot = chunkRotations[i].Value;
                        else if (hasChunkLocalToWorldType)
                        {
                            var localToWorld = chunkLocalToWorlds[i];
                            worldFromBody.rot = Math.DecomposeRigidBodyOrientation(localToWorld.Value);
                        }
                    }

                    rigidbody.WorldFromBody = new RigidTransform(worldFromBody.rot, worldFromBody.pos);
                    rigidbody.Collider = hasChunkPhysicsColliderType ? chunkColliders[i].Value : BlobAssetReference<Collider>.Null;
                    rigidbody.Entity = chunkEntities[i];
                    rigidbody.CustomTags = hasChunkPhysicsCustomTagsType ? chunkCustomTags[i].Value : (byte)0;

                    rigidbodies[index] = rigidbody;

                    entityBodyIndexMap.TryAdd(chunkEntities[i], index);

                    ++index;
                }
            }
        }

        [BurstCompile]
        private struct CopyRigidbodies : IJobParallelFor
        {
            [ReadOnly]
            public ComponentLookup<PhysicsCollider> physicsColliders;

            [ReadOnly]
            public NativeArray<RigidBody> source;
            [WriteOnly]
            public NativeArray<RigidBody> destination;

            public void Execute(int index)
            {
                var rigidbody = source[index];

                if(!physicsColliders.HasComponent(rigidbody.Entity) || physicsColliders[rigidbody.Entity].Value != rigidbody.Collider)
                    rigidbody.Collider = BlobAssetReference<Collider>.Null;

                destination[index] = rigidbody;
            }
        }

        [NoAlias]
        private BroadphaseLite __broadphase;             // bounding volume hierarchies around subsets of the rigid bodies
        [NoAlias] 
        private NativeArray<RigidBody> __rigidbodies;    // storage for all the rigid bodies
        [NoAlias] 
        private NativeParallelHashMap<Entity, int> __entityBodyIndexMap;

        public int rigidbodyCount => __broadphase.staticTree.bodyCount + __broadphase.dynamicTree.bodyCount;

        public int staticBodyCount => __broadphase.staticTree.bodyCount;

        public int dynamicBodyCount => __broadphase.dynamicTree.bodyCount;

        public NativeArray<RigidBody> rigidbodies => __rigidbodies;

        public NativeArray<RigidBody> staticBodies => rigidbodies.GetSubArray(dynamicBodyCount, staticBodyCount);

        public NativeArray<RigidBody> dynamicBodies => rigidbodies.GetSubArray(0, dynamicBodyCount);

        public NativeParallelHashMap<Entity, int> entityBodyIndexMap => __entityBodyIndexMap;

        public BroadphaseContainer broadphase => __broadphase;

        // Contacts are always created between rigid bodies if they are closer than this distance threshold.
        public float collisionTolerance => 0.1f; // todo - make this configurable?

        public readonly Allocator Allocator;

        // Construct a collision world with the given number of uninitialized rigid bodies
        public CollisionWorldLite(int numStaticBodies, int numDynamicBodies, Allocator allocator)
        {
            BurstUtility.InitializeJob<CreateDefaultStaticRigidbody>();

            __rigidbodies = new NativeArrayLite<RigidBody>(numStaticBodies + numDynamicBodies, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            __broadphase = new BroadphaseLite(numStaticBodies, numDynamicBodies, allocator);
            __entityBodyIndexMap = new NativeParallelHashMap<Entity, int>(__rigidbodies.Length, Allocator.Persistent);

            Allocator = allocator;
        }

        public void Reset(int numStaticBodies, int numDynamicBodies)
        {
            int numRigidbodies = numStaticBodies + numDynamicBodies;
            if (__rigidbodies.Length < numRigidbodies)
            {
                Allocator allocator = Allocator;

                __rigidbodies.Dispose();

                __rigidbodies = new NativeArrayLite<RigidBody>(numRigidbodies, allocator, NativeArrayOptions.UninitializedMemory);

                __entityBodyIndexMap.Capacity = __rigidbodies.Length;
            }

            __broadphase.Reset(numStaticBodies, numDynamicBodies);
            __entityBodyIndexMap.Clear();
        }

        // Free internal memory
        public void Dispose()
        {
            if (__rigidbodies.IsCreated)
                __rigidbodies.Dispose();

            __broadphase.Dispose();

            if (__entityBodyIndexMap.IsCreated)
                __entityBodyIndexMap.Dispose();
        }

        public int GetRigidBodyIndex(Entity entity)
        {
            return __entityBodyIndexMap.TryGetValue(entity, out var index) ? index : -1;
        }

        // Schedule a set of jobs to build the broadphase based on the given world.
        public JobHandle ScheduleBuildBroadphaseJobs(
            int innerloopBatchCount, 
            float timeStep, 
            in float3 gravity,
            in NativeArray<int> buildStaticTree,
            in NativeArray<MotionVelocity> motionVelocities,
            in JobHandle inputDeps)
        {
            return __broadphase.ScheduleBuildJobs(
                innerloopBatchCount, 
                collisionTolerance, 
                timeStep, 
                gravity, 
                buildStaticTree, 
                staticBodies,
                dynamicBodies,
                motionVelocities, 
                inputDeps);
        }

        public void ScheduleBuildJob(
            int innerloopBatchCount,
            int previousStaticBodyCount, 
            in float3 gravity,
            in EntityQuery dynamicEntityGroup, 
            in EntityQuery staticEntityGroup,
            in NativeArray<MotionVelocity> motionVelocities,
            in EntityTypeHandle entityType,
            in ComponentTypeHandle<Parent> parentType,
            in ComponentTypeHandle<LocalToWorld> localToWorldType,
            in ComponentTypeHandle<Translation> positionType,
            in ComponentTypeHandle<Rotation> rotationType,
            in ComponentTypeHandle<PhysicsCollider> physicsColliderType,
            in ComponentTypeHandle<PhysicsCustomTags> physicsCustomTagsType,
            in NativeArray<int> dynamicChunkBaseEntityIndices,
            in JobHandle inputDeps, 
            ref SystemState systemState)
        {
            var entityBodyIndexMap = __entityBodyIndexMap.AsParallelWriter();
            var rigidbodies = this.rigidbodies;
            int rigidbodyCount = this.rigidbodyCount;

            UnityEngine.Assertions.Assert.IsTrue(rigidbodyCount > 0);

            // Create the default static body at the end of the body list
            // TODO: could skip this if no joints present
            /*CreateDefaultStaticRigidbody createDefaultStaticRigidbody;
            createDefaultStaticRigidbody.rigidbodies = rigidbodies;
            createDefaultStaticRigidbody.rigidbodyIndex = rigidbodyCount - 1;
            createDefaultStaticRigidbody.entityBodyIndexMap = entityBodyIndexMap;

            if (rigidbodyCount < 2)
            {
                systemState.Dependency = createDefaultStaticRigidbody.Schedule(systemState.Dependency);

                // No bodies in the scene, no need to do anything else
                return;
            }*/

            // Extract types used by initialize jobs
            /*var entityType = systemState.GetEntityTypeHandle();
            var parentType = systemState.GetComponentTypeHandle<Parent>(true);
            var localToWorldType = systemState.GetComponentTypeHandle<LocalToWorld>(true);
            var positionType = systemState.GetComponentTypeHandle<Translation>(true);
            var rotationType = systemState.GetComponentTypeHandle<Rotation>(true);
            var physicsColliderType = systemState.GetComponentTypeHandle<PhysicsCollider>(true);
            var physicsCustomTagsType = systemState.GetComponentTypeHandle<PhysicsCustomTags>(true);*/

            // Determine if the static bodies have changed in any way that will require the static broadphase tree to be rebuilt
            var haveStaticBodiesChanged = new NativeArray<int>(1, Allocator.TempJob);

            JobHandle jobHandle;
            using (var jobHandles = new NativeList<JobHandle>(5, Allocator.Temp))
            {
                var dependency = systemState.Dependency;
                //jobHandles.Add(dependency);

                if (staticBodyCount != previousStaticBodyCount)
                    haveStaticBodiesChanged[0] = 1;
                else
                {
                    haveStaticBodiesChanged[0] = 0;

                    CheckStaticBodyChangesJob checkStaticBodyChangesJob;
                    checkStaticBodyChangesJob.localToWorldType = localToWorldType;
                    checkStaticBodyChangesJob.positionType = positionType;
                    checkStaticBodyChangesJob.rotationType = rotationType;
                    checkStaticBodyChangesJob.physicsColliderType = physicsColliderType;
                    checkStaticBodyChangesJob.lastSystemVersion = systemState.LastSystemVersion;
                    checkStaticBodyChangesJob.result = haveStaticBodiesChanged;
                    var staticBodiesCheckHandle = checkStaticBodyChangesJob.ScheduleParallel(staticEntityGroup, inputDeps);

                    // Static body changes check jobs
                    jobHandles.Add(staticBodiesCheckHandle);
                }

                // Create the default static body at the end of the body list
                // TODO: could skip this if no joints present
                CreateDefaultStaticRigidbody createDefaultStaticRigidbody;
                createDefaultStaticRigidbody.rigidbodies = rigidbodies;
                createDefaultStaticRigidbody.rigidbodyIndex = rigidbodyCount - 1;
                createDefaultStaticRigidbody.entityBodyIndexMap = entityBodyIndexMap;

                jobHandles.Add(createDefaultStaticRigidbody.Schedule(inputDeps));

                // Dynamic bodies.
                // Create these separately from static bodies to maintain a 1:1 mapping
                // between dynamic bodies and their motions.
                if (dynamicEntityGroup.IsEmptyIgnoreFilter)
                    jobHandles.Add(dependency);
                else
                {
                    CreateRigidbodies createRigidbodies;
                    createRigidbodies.entityType = entityType;
                    createRigidbodies.localToWorldType = localToWorldType;
                    createRigidbodies.parentType = parentType;
                    createRigidbodies.positionType = positionType;
                    createRigidbodies.rotationType = rotationType;
                    createRigidbodies.physicsColliderType = physicsColliderType;
                    createRigidbodies.physicsCustomTagsType = physicsCustomTagsType;

                    createRigidbodies.firstBodyIndex = 0;
                    createRigidbodies.chunkBaseEntityIndices = dynamicChunkBaseEntityIndices;
                    createRigidbodies.rigidbodies = rigidbodies;
                    createRigidbodies.entityBodyIndexMap = entityBodyIndexMap;

                    jobHandles.Add(createRigidbodies.ScheduleParallelByRef(dynamicEntityGroup, dependency));
                }

                // Now, schedule creation of static bodies, with FirstBodyIndex pointing after
                // the dynamic and kinematic bodies
                if (!staticEntityGroup.IsEmptyIgnoreFilter)
                {
                    var chunkBaseEntityIndices =
                        staticEntityGroup.CalculateBaseEntityIndexArrayAsync(systemState.WorldUpdateAllocator, inputDeps,
                            out var baseIndexJob);

                    CreateRigidbodies createRigidbodies;
                    createRigidbodies.entityType = entityType;
                    createRigidbodies.localToWorldType = localToWorldType;
                    createRigidbodies.parentType = parentType;
                    createRigidbodies.positionType = positionType;
                    createRigidbodies.rotationType = rotationType;
                    createRigidbodies.physicsColliderType = physicsColliderType;
                    createRigidbodies.physicsCustomTagsType = physicsCustomTagsType;

                    createRigidbodies.firstBodyIndex = dynamicBodyCount;
                    createRigidbodies.chunkBaseEntityIndices = chunkBaseEntityIndices;
                    createRigidbodies.rigidbodies = rigidbodies;
                    createRigidbodies.entityBodyIndexMap = entityBodyIndexMap;

                    jobHandles.Add(createRigidbodies.ScheduleParallel(staticEntityGroup, baseIndexJob));
                }

                jobHandle = JobHandle.CombineDependencies(jobHandles.AsArray());
            }

            // Build the broadphase
            // TODO: could optimize this by gathering the AABBs and filters at the same time as building the bodies above
            jobHandle = ScheduleBuildBroadphaseJobs(
                innerloopBatchCount, 
                systemState.WorldUnmanaged.Time.DeltaTime,
                gravity,
                haveStaticBodiesChanged,
                motionVelocities,
                jobHandle);

            systemState.Dependency = haveStaticBodiesChanged.DisposeOnJobCompletion(jobHandle);
        }

        public JobHandle CopyTo(
            int innerloopBatchCount, 
            ref CollisionWorld collisionWorld,
            in ComponentLookup<PhysicsCollider> physicsColliders, 
            in JobHandle inputDeps)
        {
            NativeArray<RigidBody> rigidbodies = collisionWorld.Bodies, 
                staticBodies = collisionWorld.StaticBodies, 
                dynamicBodies = collisionWorld.DynamicBodies;

            CopyRigidbodies copyRigidbodies;
            copyRigidbodies.physicsColliders = physicsColliders;
            copyRigidbodies.source = __rigidbodies;
            copyRigidbodies.destination = rigidbodies;

            var jobHandle = copyRigidbodies.Schedule(rigidbodyCount, innerloopBatchCount, inputDeps);

            jobHandle = __broadphase.CopyTo(ref collisionWorld.Broadphase, staticBodies, dynamicBodies, jobHandle);

            var entityBodyIndexMapJoHandle = collisionWorld.EntityBodyIndexMap.CopyFrom(__entityBodyIndexMap, inputDeps);

            return JobHandle.CombineDependencies(jobHandle, entityBodyIndexMapJoHandle);
        }
    }

    public struct CollisionWorldContainer
    {
        [NoAlias]
        private NativeArray<RigidBody> __rigidbodies;    // storage for all the rigid bodies
        [NoAlias]
        private BroadphaseContainer __broadphase;             // bounding volume hierarchies around subsets of the rigid bodies
        [NoAlias]
        private NativeParallelHashMap<Entity, int> __entityBodyIndexMap;

        public int rigidbodyCount => __broadphase.staticTree.bodyCount + __broadphase.dynamicTree.bodyCount;

        public int staticBodyCount => __broadphase.staticTree.bodyCount;

        public int dynamicBodyCount => __broadphase.dynamicTree.bodyCount;

        public BroadphaseContainer broadphase => __broadphase;

        public NativeArray<RigidBody>.ReadOnly dynamicBodies => __rigidbodies.GetSubArray(0, dynamicBodyCount).AsReadOnly();

        public NativeArray<RigidBody>.ReadOnly staticBodies => __rigidbodies.GetSubArray(dynamicBodyCount, staticBodyCount).AsReadOnly();


        public static implicit operator CollisionWorldContainer(CollisionWorldLite value)
        {
            CollisionWorldContainer container;
            container.__rigidbodies = value.rigidbodies;
            container.__broadphase = value.broadphase;
            container.__entityBodyIndexMap = value.entityBodyIndexMap;
            return container;
        }

        public static implicit operator CollisionWorld(CollisionWorldContainer lite)
        {
            return new CollisionWorld(lite.__rigidbodies, lite.__broadphase.As(), lite.__entityBodyIndexMap);
        }
    }
}
