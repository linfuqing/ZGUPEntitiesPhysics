using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Entities;
using Unity.Transforms;
using Unity.Physics;

namespace ZG
{
    [NoAlias]
    public struct PhysicsWorldLite
    {
        [NoAlias]
        private CollisionWorldLite __collisionWorld;   // stores rigid bodies and broadphase

        [NoAlias]
        private DynamicsWorldLite __dynamicsWorld;

        public CollisionWorldLite collisionWorld => __collisionWorld;

        public DynamicsWorldLite dynamicsWorld => __dynamicsWorld;

        // Construct a world with the given number of uninitialized bodies and joints
        public unsafe PhysicsWorldLite(int numStaticBodies, int numDynamicBodies, in AllocatorManager.AllocatorHandle allocator)
        {
            __collisionWorld = new CollisionWorldLite(numStaticBodies, numDynamicBodies, allocator);
            __dynamicsWorld = new DynamicsWorldLite(numDynamicBodies, allocator);
        }

        // Reset the number of bodies in the world
        internal void _Reset(int numStaticBodies, int numDynamicBodies)
        {
            __collisionWorld.Reset(numStaticBodies, numDynamicBodies);

            __dynamicsWorld.Reset(numDynamicBodies);
        }

        // Free internal memory
        internal void _Dispose()
        {
            __collisionWorld.Dispose();

            __dynamicsWorld.Dispose();
        }

        internal void _ScheduleBuildJob(
            int innerloopBatchCount, 
            in float3 gravity,
            in EntityQuery dynamicEntityGroup,
            in EntityQuery staticEntityGroup,
            in EntityTypeHandle entityType,
            in ComponentTypeHandle<Parent> parentType,
            in ComponentTypeHandle<LocalToWorld> localToWorldType,
            in ComponentTypeHandle<Translation> positionType,
            in ComponentTypeHandle<Rotation> rotationType,
            in ComponentTypeHandle<PhysicsCollider> physicsColliderType,
            in ComponentTypeHandle<PhysicsCustomTags> physicsCustomTagsType,
            in ComponentTypeHandle<PhysicsVelocity> physicsVelocityType,
            in ComponentTypeHandle<PhysicsMass> physicsMassType,
            in ComponentTypeHandle<PhysicsMassOverride> physicsMassOverrideType,
            in ComponentTypeHandle<PhysicsGravityFactor> physicsGravityFactorType,
            in ComponentTypeHandle<PhysicsDamping> physicsDampingType,
            ref SystemState systemState)
        {
            int numDynamicBodies = dynamicEntityGroup.CalculateEntityCount();
            int numStaticBodies = staticEntityGroup.CalculateEntityCount();

            int previousStaticBodyCount = __collisionWorld.staticBodyCount;

            //TODO: To Job;
            // Resize the world's native arrays
            _Reset(
                numStaticBodies + 1, // +1 for the default static body
                numDynamicBodies);

            var motionVelocities = __dynamicsWorld.motionVelocities;

            JobHandle inputDeps = systemState.Dependency;
            var chunkBaseEntityIndices =
                dynamicEntityGroup.CalculateBaseEntityIndexArrayAsync(systemState.WorldUpdateAllocator, inputDeps,
                    out var dependency);

            if (__dynamicsWorld.ScheduleBuildJob(
                dynamicEntityGroup,
                positionType,
                rotationType,
                physicsVelocityType,
                physicsMassType,
                physicsMassOverrideType,
                physicsGravityFactorType,
                physicsDampingType,
                chunkBaseEntityIndices,
                ref dependency))
                systemState.Dependency = dependency;

            __collisionWorld.ScheduleBuildJob(
                innerloopBatchCount, 
                previousStaticBodyCount,
                gravity,
                dynamicEntityGroup,
                staticEntityGroup,
                motionVelocities,
                entityType, 
                parentType, 
                localToWorldType, 
                positionType, 
                rotationType, 
                physicsColliderType, 
                physicsCustomTagsType,
                chunkBaseEntityIndices, 
                inputDeps, 
                ref systemState);
        }

        internal void _CopyTo(
            int innerloopBatchCount, 
            in EntityQuery jointGroup,
            ref PhysicsWorld physicsWorld,
            ref SystemState systemState)
        {
            int dynamicBodyCount = __collisionWorld.dynamicBodyCount;
            physicsWorld.Reset(__collisionWorld.staticBodyCount, dynamicBodyCount, jointGroup.CalculateEntityCount());

            var collisionWorld = physicsWorld.CollisionWorld;

            var inputDeps = systemState.Dependency;
            var jobHandle = __collisionWorld.CopyTo(
                innerloopBatchCount, 
                ref collisionWorld,
                systemState.GetComponentLookup<PhysicsCollider>(true), 
                inputDeps);

            systemState.Dependency = inputDeps;
            var dynamicsWorld = physicsWorld.DynamicsWorld;
            __dynamicsWorld.CopyTo(
                __collisionWorld.rigidbodies.Length - 1,
                dynamicBodyCount, 
                jointGroup,
                __collisionWorld.entityBodyIndexMap,
                ref dynamicsWorld,
                ref systemState);

            systemState.Dependency = JobHandle.CombineDependencies(jobHandle, systemState.Dependency);
        }
    }

    public struct PhysicsWorldContainer
    {
        private CollisionWorldContainer __collisionWorld;

        private DynamicsWorldContainer __dynamicsWorld;

        public int rigidbodyCount => __collisionWorld.rigidbodyCount;

        public int staticBodyCount => __collisionWorld.staticBodyCount;

        public int dynamicBodyCount => __collisionWorld.dynamicBodyCount;

        public CollisionWorld collisionWorld => __collisionWorld;

        public NativeArray<MotionData> motionDatas => __dynamicsWorld.motionDatas;

        public NativeArray<MotionVelocity> motionVelocities => __dynamicsWorld.motionVelocities;

        public static implicit operator PhysicsWorldContainer(PhysicsWorldLite value)
        {
            PhysicsWorldContainer container;
            container.__collisionWorld = value.collisionWorld;
            container.__dynamicsWorld = value.dynamicsWorld;

            return container;
        }
    }

    public unsafe struct SharedPhysicsWorld
    {
        private struct Data
        {
            public PhysicsWorldLite value;

            public LookupJobManager lookupJobManager;
        }

        private Data* __data;

        public readonly AllocatorManager.AllocatorHandle allocator;

        public ref LookupJobManager lookupJobManager => ref __data->lookupJobManager;

        public NativeArray<MotionData> motionDatas => __data->value.dynamicsWorld.motionDatas;

        public NativeArray<MotionVelocity> motionVelocities => __data->value.dynamicsWorld.motionVelocities;

        public CollisionWorldContainer collisionWorld => __data->value.collisionWorld;

        public PhysicsWorldContainer container => __data->value;

        // Construct a world with the given number of uninitialized bodies and joints
        public unsafe SharedPhysicsWorld(int numStaticBodies, int numDynamicBodies, in AllocatorManager.AllocatorHandle allocator)
        {
            this.allocator = allocator;

            __data = AllocatorManager.Allocate<Data>(allocator);
            __data->value = new PhysicsWorldLite(numStaticBodies, numDynamicBodies, allocator);
            __data->lookupJobManager = new LookupJobManager();
        }

        // Free internal memory
        public void Dispose()
        {
            __data->value._Dispose();

            AllocatorManager.Free(allocator, __data);

            __data = null;
        }

        public void ScheduleBuildJob(
            int innerloopBatchCount, 
            in float3 gravity,
            in EntityQuery dynamicEntityGroup,
            in EntityQuery staticEntityGroup,
            in EntityTypeHandle entityType,
            in ComponentTypeHandle<Parent> parentType,
            in ComponentTypeHandle<LocalToWorld> localToWorldType,
            in ComponentTypeHandle<Translation> positionType,
            in ComponentTypeHandle<Rotation> rotationType,
            in ComponentTypeHandle<PhysicsCollider> physicsColliderType,
            in ComponentTypeHandle<PhysicsCustomTags> physicsCustomTagsType,
            in ComponentTypeHandle<PhysicsVelocity> physicsVelocityType,
            in ComponentTypeHandle<PhysicsMass> physicsMassType,
            in ComponentTypeHandle<PhysicsMassOverride> physicsMassOverrideType,
            in ComponentTypeHandle<PhysicsGravityFactor> physicsGravityFactorType,
            in ComponentTypeHandle<PhysicsDamping> physicsDampingType,
            ref SystemState systemState)
        {
            __data->lookupJobManager.CompleteReadWriteDependency();

            __data->value._ScheduleBuildJob(
                innerloopBatchCount, 
                gravity, 
                dynamicEntityGroup, 
                staticEntityGroup,
                entityType, 
                parentType, 
                localToWorldType, 
                positionType, 
                rotationType, 
                physicsColliderType, 
                physicsCustomTagsType,
                physicsVelocityType,
                physicsMassType,
                physicsMassOverrideType,
                physicsGravityFactorType,
                physicsDampingType, 
                ref systemState);

            __data->lookupJobManager.readWriteJobHandle = systemState.Dependency;
        }

        public void CopyTo(
            int innerloopBatchCount, 
            in EntityQuery jointGroup,
            ref PhysicsWorld physicsWorld,
            ref SystemState systemState)
        {
            systemState.Dependency = JobHandle.CombineDependencies(systemState.Dependency, __data->lookupJobManager.readOnlyJobHandle);

            __data->value._CopyTo(innerloopBatchCount, jointGroup, ref physicsWorld, ref systemState);

            __data->lookupJobManager.AddReadOnlyDependency(systemState.Dependency);
        }
    }

    public static partial class PhysicsWorldUtility
    {
        // Get the linear velocity of a rigid body at a given point (in world space)

        // Get the linear velocity of a rigid body at a given point (in world space)
        public static float3 GetLinearVelocity(this in PhysicsWorldContainer world, int rigidbodyIndex, in float3 point)
        {
            return DynamicsWorldWorldUtility.GetLinearVelocity(world.motionDatas, world.motionVelocities, rigidbodyIndex, point);
        }
    }
}
