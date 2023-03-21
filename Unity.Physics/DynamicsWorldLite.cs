using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Entities;
using Unity.Transforms;
using Unity.Physics;
using Unity.Mathematics;
using UnityEngine.Assertions;

[assembly: RegisterGenericJobType(typeof(ZG.CopyArrayJob<MotionData>))]
[assembly: RegisterGenericJobType(typeof(ZG.CopyArrayJob<MotionVelocity>))]

namespace ZG
{
    [BurstCompile]
    public struct CopyArrayJob<T> : IJob where T : struct
    {
        public int length;
        [ReadOnly]
        public NativeArray<T> source;
        [WriteOnly]
        public NativeArray<T> destination;

        public void Execute()
        {
            NativeArray<T>.Copy(source, destination, length);
        }
    }

    public static partial class JobUtility
    {
        public static JobHandle CopyFrom<T>(
            this ref NativeArray<T> destination,
            in NativeArray<T> source,
            int length,
            in JobHandle inputDeps) where T : struct
        {
            CopyArrayJob<T> copyArrayJob;
            copyArrayJob.length = length;
            copyArrayJob.source = source;
            copyArrayJob.destination = destination;

            return copyArrayJob.Schedule(inputDeps);
        }
    }

    [NoAlias]
    public struct DynamicsWorldLite : System.IDisposable
    {
        [BurstCompile]
        private struct CreateMotions : IJobChunk
        {
            [ReadOnly] 
            public ComponentTypeHandle<Translation> positionType;
            [ReadOnly] 
            public ComponentTypeHandle<Rotation> rotationType;
            [ReadOnly]
            public ComponentTypeHandle<PhysicsVelocity> physicsVelocityType;
            [ReadOnly]
            public ComponentTypeHandle<PhysicsMass> physicsMassType;
            [ReadOnly]
            public ComponentTypeHandle<PhysicsMassOverride> physicsMassOverrideType;
            [ReadOnly]
            public ComponentTypeHandle<PhysicsGravityFactor> physicsGravityFactorType;
            [ReadOnly]
            public ComponentTypeHandle<PhysicsDamping> physicsDampingType;

            [ReadOnly] 
            public NativeArray<int> chunkBaseEntityIndices;

            [NativeDisableParallelForRestriction]
            public NativeArray<MotionVelocity> motionVelocities;
            [NativeDisableParallelForRestriction]
            public NativeArray<MotionData> motionDatas;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var chunkPositions = chunk.GetNativeArray(ref positionType);
                var chunkRotations = chunk.GetNativeArray(ref rotationType);
                var chunkVelocities = chunk.GetNativeArray(ref physicsVelocityType);
                var chunkMasses = chunk.GetNativeArray(ref physicsMassType);
                var chunkMassOverrides = chunk.GetNativeArray(ref physicsMassOverrideType);
                var chunkGravityFactors = chunk.GetNativeArray(ref physicsGravityFactorType);
                var chunkDampings = chunk.GetNativeArray(ref physicsDampingType);

                bool hasChunkPhysicsGravityFactorType = chunk.Has(ref physicsGravityFactorType);
                bool hasChunkPhysicsMassType = chunk.Has(ref physicsMassType);
                bool hasChunkPhysicsMassOverrideType = chunk.Has(ref physicsMassOverrideType);
                bool hasChunkPhysicsDampingType = chunk.Has(ref physicsDampingType);

                // Note: Transform and AngularExpansionFactor could be calculated from PhysicsCollider.MassProperties
                // However, to avoid the cost of accessing the collider we assume an infinite mass at the origin of a ~1m^3 box.
                // For better performance with spheres, or better behavior for larger and/or more irregular colliders
                // you should add a PhysicsMass component to get the true values
                PhysicsMass defaultPhysicsMass;
                defaultPhysicsMass.Transform = RigidTransform.identity;
                defaultPhysicsMass.InverseMass = 0.0f;
                defaultPhysicsMass.InverseInertia = float3.zero;
                defaultPhysicsMass.AngularExpansionFactor = 1.0f;

                int baseEntityIndex = chunkBaseEntityIndices[unfilteredChunkIndex], index = baseEntityIndex;
                // Note: if a dynamic body infinite mass then assume no gravity should be applied
                float defaultGravityFactor = hasChunkPhysicsMassType ? 1.0f : 0.0f;
                MotionVelocity motionVelocity;
                PhysicsMass physicsMass;
                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while(iterator.NextEntityIndex(out int i))
                {
                    var isKinematic = !hasChunkPhysicsMassType || hasChunkPhysicsMassOverrideType && chunkMassOverrides[i].IsKinematic != 0;
                    physicsMass = isKinematic ? defaultPhysicsMass : chunkMasses[i];

                    motionVelocity.LinearVelocity = chunkVelocities[i].Linear;
                    motionVelocity.AngularVelocity = chunkVelocities[i].Angular;
                    motionVelocity.InverseInertia = physicsMass.InverseInertia;
                    motionVelocity.InverseMass = physicsMass.InverseMass;
                    motionVelocity.AngularExpansionFactor = physicsMass.AngularExpansionFactor;
                    motionVelocity.GravityFactor = isKinematic ? 0 : hasChunkPhysicsGravityFactorType ? chunkGravityFactors[i].Value : defaultGravityFactor;

                    motionVelocities[index++] = motionVelocity;
                }

                index = baseEntityIndex;

                // Note: these defaults assume a dynamic body with infinite mass, hence no damping
                PhysicsDamping defaultPhysicsDamping;
                defaultPhysicsDamping.Linear = 0.0f;
                defaultPhysicsDamping.Angular = 0.0f;

                PhysicsDamping physicsDamping;
                MotionData motionData;
                // Create motion datas
                iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                {
                    physicsMass = hasChunkPhysicsMassType ? chunkMasses[i] : defaultPhysicsMass;
                    physicsDamping = hasChunkPhysicsDampingType ? chunkDampings[i] : defaultPhysicsDamping;

                    /*motionData.WorldFromMotion = new RigidTransform(
                            math.mul(chunkRotations[i].Value, physicsMass.InertiaOrientation),
                            math.rotate(chunkRotations[i].Value, physicsMass.CenterOfMass) + chunkPositions[i].Value);*/
                    motionData.BodyFromMotion = math.RigidTransform(physicsMass.InertiaOrientation, physicsMass.CenterOfMass);
                    motionData.WorldFromMotion = math.mul(math.RigidTransform(chunkRotations[i].Value, chunkPositions[i].Value), motionData.BodyFromMotion);
                    motionData.LinearDamping = physicsDamping.Linear;
                    motionData.AngularDamping = physicsDamping.Angular;

                    motionDatas[index++] = motionData;
                }
            }
        }

        [BurstCompile]
        private struct CreateJoints : IJobChunk
        {
            [ReadOnly] 
            public ComponentTypeHandle<PhysicsConstrainedBodyPair> constrainedBodyPairComponentType;
            [ReadOnly] 
            public ComponentTypeHandle<PhysicsJoint> jointComponentType;
            [ReadOnly] 
            public EntityTypeHandle entityType;
            [ReadOnly] 
            public NativeParallelHashMap<Entity, int> entityBodyIndexMap;

            public NativeParallelHashMap<Entity, int>.ParallelWriter entityJointIndexMap;

            public NativeArray<Joint> joints;

            [ReadOnly]
            public NativeArray<int> chunkBaseEntityIndices;

            public int defaultStaticBodyIndex;
            public int dynamicBodyCount;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var chunkBodyPair = chunk.GetNativeArray(ref constrainedBodyPairComponentType);
                var chunkJoint = chunk.GetNativeArray(ref jointComponentType);
                var chunkEntities = chunk.GetNativeArray(entityType);
                PhysicsConstrainedBodyPair bodyPair;
                PhysicsJoint physicsJoint;
                Joint joint;
                Entity entityA, entityB;
                int index = chunkBaseEntityIndices[unfilteredChunkIndex];
                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out int i))
                {
                    bodyPair = chunkBodyPair[i];
                    entityA = bodyPair.EntityA;
                    entityB = bodyPair.EntityB;
                    Assert.AreNotEqual(entityA, entityB);

                    physicsJoint = chunkJoint[i];

                    // TODO find a reasonable way to look up the constraint body indices
                    // - stash body index in a component on the entity? But we don't have random access to Entity data in a job
                    // - make a map from entity to rigid body index? Sounds bad and I don't think there is any NativeArray-based map data structure yet

                    // If one of the entities is null, use the default static entity
                    var pair = new BodyIndexPair
                    {
                        BodyIndexA = entityA == Entity.Null ? defaultStaticBodyIndex : -1,
                        BodyIndexB = entityB == Entity.Null ? defaultStaticBodyIndex : -1,
                    };

                    // Find the body indices
                    pair.BodyIndexA = entityBodyIndexMap.TryGetValue(entityA, out var idxA) ? idxA : -1;
                    pair.BodyIndexB = entityBodyIndexMap.TryGetValue(entityB, out var idxB) ? idxB : -1;

                    bool isInvalid = false;
                    // Invalid if we have not found the body indices...
                    isInvalid |= (pair.BodyIndexA == -1 || pair.BodyIndexB == -1);
                    // ... or if we are constraining two static bodies
                    // Mark static-static invalid since they are not going to affect simulation in any way.
                    isInvalid |= (pair.BodyIndexA >= dynamicBodyCount && pair.BodyIndexB >= dynamicBodyCount);
                    if (isInvalid)
                        pair = BodyIndexPair.Invalid;

                    joint.BodyPair = pair;
                    joint.Entity = chunkEntities[i];
                    joint.EnableCollision = (byte)chunkBodyPair[i].EnableCollision;
                    joint.AFromJoint = physicsJoint.BodyAFromJoint.AsMTransform();
                    joint.BFromJoint = physicsJoint.BodyBFromJoint.AsMTransform();
                    joint.Version = physicsJoint.Version;
                    joint.Constraints = physicsJoint.GetConstraints();

                    joints[index] = joint;

                    entityJointIndexMap.TryAdd(chunkEntities[i], index);

                    ++index;
                }
            }
        }

        [NoAlias]
        private NativeArrayLite<MotionData> __motionDatas;

        [NoAlias]
        private NativeArrayLite<MotionVelocity> __motionVelocities;

        public NativeArray<MotionData> motionDatas => __motionDatas;

        public NativeArray<MotionVelocity> motionVelocities => __motionVelocities;

        // Construct a world with the given number of uninitialized bodies and joints
        public unsafe DynamicsWorldLite(int numDynamicBodies, Allocator allocator)
        {
            __motionDatas = new NativeArrayLite<MotionData>(numDynamicBodies, allocator, NativeArrayOptions.UninitializedMemory);
            __motionVelocities = new NativeArrayLite<MotionVelocity>(numDynamicBodies, allocator, NativeArrayOptions.UninitializedMemory);
        }

        public void Reset(int numDynamicBodies)
        {
            if (__motionDatas.Length < numDynamicBodies)
            {
                Allocator allocator = __motionDatas.allocator;

                __motionDatas.Dispose();

                __motionDatas = new NativeArrayLite<MotionData>(numDynamicBodies, allocator, NativeArrayOptions.UninitializedMemory);
            }

            if (__motionVelocities.Length < numDynamicBodies)
            {
                Allocator allocator = __motionVelocities.allocator;

                __motionVelocities.Dispose();

                __motionVelocities = new NativeArrayLite<MotionVelocity>(numDynamicBodies, allocator, NativeArrayOptions.UninitializedMemory);
            }
        }

        public void Dispose()
        {
            if (__motionDatas.isCreated)
                __motionDatas.Dispose();

            if (__motionVelocities.isCreated)
                __motionVelocities.Dispose();
        }

        public bool ScheduleBuildJob(
            in EntityQuery dynamicEntityGroup,
            in ComponentTypeHandle<Translation> positionType, 
            in ComponentTypeHandle<Rotation> rotationType, 
            in ComponentTypeHandle<PhysicsVelocity> physicsVelocityType,
            in ComponentTypeHandle<PhysicsMass> physicsMassType,
            in ComponentTypeHandle<PhysicsMassOverride> physicsMassOverrideType,
            in ComponentTypeHandle<PhysicsGravityFactor> physicsGravityFactorType,
            in ComponentTypeHandle<PhysicsDamping> physicsDampingType,
            in NativeArray<int> chunkBaseEntityIndices, 
            ref JobHandle dependency)
        {
            if (dynamicEntityGroup.IsEmptyIgnoreFilter)
                // No bodies in the scene, no need to do anything else
                return false;

            CreateMotions createMotions;
            createMotions.positionType = positionType;
            createMotions.rotationType = rotationType;
            createMotions.physicsVelocityType = physicsVelocityType;
            createMotions.physicsMassType = physicsMassType;
            createMotions.physicsMassOverrideType = physicsMassOverrideType;
            createMotions.physicsGravityFactorType = physicsGravityFactorType;
            createMotions.physicsDampingType = physicsDampingType;
            createMotions.chunkBaseEntityIndices = chunkBaseEntityIndices;
            createMotions.motionDatas = __motionDatas;
            createMotions.motionVelocities = __motionVelocities;

            dependency = createMotions.ScheduleParallel(dynamicEntityGroup, dependency);

            return true;
        }

        public void CopyTo(
            int defaultStaticBodyIndex, 
            int dynamicBodyCount, 
            in EntityQuery jointGroup,
            in NativeParallelHashMap<Entity, int> entityBodyIndexMap,
            ref DynamicsWorld dynamicsWorld,
            ref SystemState systemState)
        {
            //dynamicsWorld.Reset(__motionVelocities.Length, jointGroup.CalculateEntityCount());

            var inputDeps = systemState.Dependency;

            var motionDatas = dynamicsWorld.MotionDatas;
            var copyMotionDatasJobHandle = motionDatas.CopyFrom(__motionDatas, dynamicBodyCount, inputDeps);

            var motionVelocities = dynamicsWorld.MotionVelocities;
            var copyMotionVelocitiesJobHandle = motionVelocities.CopyFrom(__motionVelocities, dynamicBodyCount, inputDeps);

            var chunkBaseEntityIndices =
                jointGroup.CalculateBaseEntityIndexArrayAsync(systemState.WorldUpdateAllocator, inputDeps,
                    out var baseIndexJob);

            CreateJoints createJoints;
            createJoints.constrainedBodyPairComponentType = systemState.GetComponentTypeHandle<PhysicsConstrainedBodyPair>(true);
            createJoints.jointComponentType = systemState.GetComponentTypeHandle<PhysicsJoint>(true);
            createJoints.entityType = systemState.GetEntityTypeHandle();
            createJoints.entityBodyIndexMap = entityBodyIndexMap;
            createJoints.entityJointIndexMap = dynamicsWorld.EntityJointIndexMap.AsParallelWriter();
            createJoints.joints = dynamicsWorld.Joints;
            createJoints.chunkBaseEntityIndices = chunkBaseEntityIndices;
            createJoints.defaultStaticBodyIndex = defaultStaticBodyIndex;
            createJoints.dynamicBodyCount = dynamicBodyCount;

            var createJointsJobHandle = createJoints.ScheduleParallel(jointGroup, baseIndexJob);

            systemState.Dependency = JobHandle.CombineDependencies(copyMotionDatasJobHandle, copyMotionVelocitiesJobHandle, createJointsJobHandle);
        }
    }

    [NoAlias]
    public struct DynamicsWorldContainer
    {
        [NoAlias]
        public NativeArray<MotionData> __motionDatas;

        [NoAlias]
        public NativeArray<MotionVelocity> __motionVelocities;

        public NativeArray<MotionData> motionDatas => __motionDatas;

        public NativeArray<MotionVelocity> motionVelocities => __motionVelocities;


        public static implicit operator DynamicsWorldContainer(DynamicsWorldLite value)
        {
            DynamicsWorldContainer container;
            container.__motionDatas = value.motionDatas;
            container.__motionVelocities = value.motionVelocities;

            return container;
        }
    }

    public static class DynamicsWorldWorldUtility
    {
        public static float3 GetLinearVelocity(
            in NativeArray<MotionData> motionDatas, 
            in NativeArray<MotionVelocity> motionVelocities, 
            int rigidbodyIndex, 
            in float3 point)
        {
            var md = motionDatas[rigidbodyIndex];
            var mv = motionVelocities[rigidbodyIndex];

            return Unity.Physics.Extensions.PhysicsWorldExtensions.GetLinearVelocityImpl(md.WorldFromMotion, mv.AngularVelocity, mv.LinearVelocity, point);
        }
    }
}