using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Physics;
using Unity.Mathematics;

namespace ZG
{

    // A bounding volume around a collection of rigid bodies
    [NoAlias]
    public struct BroadphaseLite : IDisposable
    {
        [NoAlias]
        private BroadphaseTree __staticTree;  // The tree of static rigid bodies
        [NoAlias]
        private BroadphaseTree __dynamicTree; // The tree of dynamic rigid bodies

        public BroadphaseTree staticTree => __staticTree;
        public BroadphaseTree dynamicTree => __dynamicTree;

        public Aabb domain =>
            Aabb.Union(__staticTree.boundingVolumeHierarchy.Domain, __dynamicTree.boundingVolumeHierarchy.Domain);

        public BroadphaseLite(int numStaticBodies, int numDynamicBodies, in AllocatorManager.AllocatorHandle allocator)
        {
            __staticTree = new BroadphaseTree(numStaticBodies, allocator);
            __dynamicTree = new BroadphaseTree(numDynamicBodies, allocator);
        }

        public void Reset(int numStaticBodies, int numDynamicBodies)
        {
            __staticTree.Reset(numStaticBodies);
            __dynamicTree.Reset(numDynamicBodies);
        }

        public void Dispose()
        {
            __staticTree.Dispose();
            __dynamicTree.Dispose();
        }

        public JobHandle ScheduleBuildJobs(
            int innerloopBatchCount, 
            float collisionTolerance,
            float timeStep, 
            in float3 gravity, 
            in NativeArray<int> buildStaticTree,
            in NativeArray<RigidBody> staticBodies,
            in NativeArray<RigidBody> dynamicBodies,
            in NativeArray<MotionVelocity> motionVelocities,
            in JobHandle inputDeps)
        {
            // +1 for main thread
            int threadCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + 1;
            float aabbMargin = collisionTolerance * 0.5f;

            return JobHandle.CombineDependencies(
                __staticTree.ScheduleStaticTreeBuildJobs(innerloopBatchCount, threadCount, aabbMargin, staticBodies, buildStaticTree, inputDeps),
                __dynamicTree.ScheduleDynamicTreeBuildJobs(innerloopBatchCount, threadCount, aabbMargin, timeStep, gravity, motionVelocities, dynamicBodies, inputDeps));
        }

        internal JobHandle CopyTo(
            ref Broadphase broadphase,
            in NativeArray<RigidBody> staticBodies,
            in NativeArray<RigidBody> dynamicBodies, 
            in JobHandle inputDeps)
        {
            var staticTree = broadphase.StaticTree;
            var staticJobHandle = __staticTree._CopyTo(ref staticTree, staticBodies, inputDeps);

            var dynamicTree = broadphase.DynamicTree;
            var dynamicJobHandle = __dynamicTree._CopyTo(ref dynamicTree, dynamicBodies, inputDeps);

            return JobHandle.CombineDependencies(staticJobHandle, dynamicJobHandle);
        }
    }

    public struct BroadphaseContainer
    {
        [NoAlias]
        private BroadphaseTree.Container __staticTree;  // The tree of static rigid bodies
        [NoAlias]
        private BroadphaseTree.Container __dynamicTree; // The tree of dynamic rigid bodies

        public BroadphaseTree.Container staticTree => __staticTree;

        public BroadphaseTree.Container dynamicTree => __dynamicTree;

        public static implicit operator BroadphaseContainer(BroadphaseLite value)
        {
            BroadphaseContainer container;
            container.__staticTree = value.staticTree.container;
            container.__dynamicTree = value.dynamicTree.container;

            return container;
        }

        internal Broadphase As()
        {
            return new Broadphase(__staticTree.As(), __dynamicTree.As());
        }
    }
}
