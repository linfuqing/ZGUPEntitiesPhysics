using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Entities;
using Unity.Physics;
using Unity.Mathematics;
using UnityEngine.Assertions;
using static Unity.Physics.BoundingVolumeHierarchy;

[assembly: RegisterGenericJobType(typeof(ZG.DisposeNativeArray<int>))]

[assembly: RegisterGenericJobType(typeof(ZG.CopySameLengthArrayJob<BoundingVolumeHierarchy.Node>))]
[assembly: RegisterGenericJobType(typeof(ZG.CopySameLengthArrayJob<CollisionFilter>))]
[assembly: RegisterGenericJobType(typeof(ZG.CopySameLengthArrayJob<BoundingVolumeHierarchy.Builder.Range>))]
[assembly: RegisterGenericJobType(typeof(ZG.CopySameLengthArrayJob<int>))]

namespace ZG
{
    [BurstCompile]
    public struct CopySameLengthArrayJob<T> : IJob where T : struct
    {
        [ReadOnly]
        public NativeArray<T> source;
        [WriteOnly]
        public NativeArray<T> destination;

        public void Execute()
        {
            NativeArray<T>.Copy(source, destination);
        }
    }

    [BurstCompile]
    public struct DisposeNativeArray<T> : IJob where T : struct
    {
        [DeallocateOnJobCompletion]
        public NativeArray<T> value;

        public void Execute()
        {

        }
    }

    public static partial class JobUtility
    {
        public static JobHandle CopyFrom<T>(this ref NativeArray<T> destination, in NativeArray<T> source, in JobHandle inputDeps) where T : struct
        {
            CopySameLengthArrayJob<T> copyArrayJob;
            copyArrayJob.source = source;
            copyArrayJob.destination = destination;

            return copyArrayJob.Schedule(inputDeps);
        }

        public static JobHandle DisposeOnJobCompletion<T>(this ref NativeArray<T> value, in JobHandle inputDeps) where T : struct
        {
            DisposeNativeArray<T> disposeNativeArray;
            disposeNativeArray.value = value;

            return disposeNativeArray.Schedule(inputDeps);
        }
    }

    // A tree of rigid bodies
    [NoAlias]
    public struct BroadphaseTree : IDisposable
    {
        [BurstCompile]
        private struct BuildFirstNLevelsJob : IJob
        {
            public NativeArray<PointAndIndex> points;
            public NativeArray<Node> nodes;
            public NativeArray<CollisionFilter> nodeFilters;
            public NativeArray<Builder.Range> ranges;
            public NativeArray<int> branchNodeOffsets;
            public NativeArray<int> branchCount;
            public NativeArray<int> oldBranchCount;

            [ReadOnly]
            public NativeArray<int> shouldDoWork;

            public int threadCount;

            public void Execute()
            {
                // Save old branch count for finalize tree job
                oldBranchCount[0] = this.branchCount[0];

                if (shouldDoWork[0] == 0)
                {
                    // If we need to to skip tree building tasks, than set BranchCount to zero so
                    // that BuildBranchesJob also gets early out in runtime.
                    this.branchCount[0] = 0;

                    return;
                }

                var bvh = new BoundingVolumeHierarchy(nodes, nodeFilters);
                bvh.BuildFirstNLevels(points, ranges, branchNodeOffsets, threadCount, out int branchCount);
                this.branchCount[0] = branchCount;
            }
        }

        [BurstCompile]
        private struct BuildBranchesJob : IJobParallelForDefer
        {
            [ReadOnly]
            public NativeArray<Aabb> aabbs;
            [ReadOnly]
            public NativeArray<CollisionFilter> bodyFilters;
            [ReadOnly]
            public NativeArray<Builder.Range> ranges;
            [ReadOnly]
            public NativeArray<int> branchNodeOffsets;

            [NativeDisableParallelForRestriction]
            public NativeArray<Node> nodes;
            [NativeDisableParallelForRestriction]
            public NativeArray<CollisionFilter> nodeFilters;

            [NativeDisableContainerSafetyRestriction]
            [DeallocateOnJobCompletion]
            public NativeArray<PointAndIndex> points;

            public void Execute(int index)
            {
                Assert.IsTrue(branchNodeOffsets[index] >= 0);

                var bvh = new BoundingVolumeHierarchy(nodes, nodeFilters);
                int lastNode = bvh.BuildBranch(points, aabbs, ranges[index], branchNodeOffsets[index]);

                if (nodeFilters.IsCreated)
                {
                    bvh.BuildCombinedCollisionFilter(bodyFilters, branchNodeOffsets[index], lastNode);

                    BuildCombinedCollisionFilter(ranges[index].Root);
                }
            }

            unsafe void BuildCombinedCollisionFilter(int nodeIndex)
            {
                var currentNode = nodes[nodeIndex];

                Assert.IsTrue(currentNode.IsInternal);

                CollisionFilter combinedFilter = new CollisionFilter();
                for (int j = 0; j < 4; j++)
                {
                    combinedFilter = CollisionFilter.CreateUnion(combinedFilter, nodeFilters[currentNode.Data[j]]);
                }

                nodeFilters[nodeIndex] = combinedFilter;
            }
        }

        [BurstCompile]
        private struct FinalizeTreeJob : IJob
        {
            [ReadOnly]
            [DeallocateOnJobCompletion]
            public NativeArray<Aabb> aabbs;
            [ReadOnly]
            [DeallocateOnJobCompletion]
            public NativeArray<int> branchNodeOffsets;
            [ReadOnly]
            public NativeArray<CollisionFilter> leafFilters;
            [ReadOnly]
            public NativeArray<int> shouldDoWork;
            public NativeArray<Node> nodes;
            public NativeArray<CollisionFilter> nodeFilters;
            [DeallocateOnJobCompletion]
            [ReadOnly]
            public NativeArray<int> oldBranchCount;
            public NativeArray<int> branchCount;

            public void Execute()
            {
                if (shouldDoWork[0] == 0)
                {
                    // Restore original branch count
                    this.branchCount[0] = oldBranchCount[0];

                    return;
                }

                int minBranchNodeIndex = branchNodeOffsets[0] - 1;
                int branchCount = this.branchCount[0];
                for (int i = 1; i < branchCount; i++)
                    minBranchNodeIndex = math.min(branchNodeOffsets[i] - 1, minBranchNodeIndex);

                var bvh = new BoundingVolumeHierarchy(nodes, nodeFilters);
                bvh.Refit(aabbs, 1, minBranchNodeIndex);

                if (nodeFilters.IsCreated)
                    bvh.BuildCombinedCollisionFilter(leafFilters, 1, minBranchNodeIndex);
            }
        }

        // Reads broadphase data from dynamic rigid bodies
        [BurstCompile]
        private struct PrepareDynamicBodyDataJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<RigidBody> rigidbodies;
            [ReadOnly]
            public NativeArray<MotionVelocity> motionVelocities;
            [ReadOnly]
            public float timeStep;
            [ReadOnly]
            public float3 gravity;
            [ReadOnly]
            public float aabbMargin;

            public NativeArray<PointAndIndex> points;
            public NativeArray<Aabb> aabbs;
            public NativeArray<CollisionFilter> filtersOut;

            //public NativeArray<bool> respondsToCollisionOut;

            public unsafe void Execute(int index)
            {
                var rigidbody = rigidbodies[index];

                Aabb aabb;
                if (rigidbody.Collider.IsCreated)
                {
                    var mv = motionVelocities[index];

                    // Apply gravity only on a copy to get proper expansion for the AABB,
                    // actual applying of gravity will be done later in the physics step
                    mv.LinearVelocity += gravity * timeStep * mv.GravityFactor;

                    var expansion = mv.CalculateExpansion(timeStep);
                    aabb = expansion.ExpandAabb(rigidbody.Collider.Value.CalculateAabb(rigidbody.WorldFromBody));
                    aabb.Expand(aabbMargin);

                    filtersOut[index] = rigidbody.Collider.Value.Filter;

                    //respondsToCollisionOut[index] = rigidbody.Collider.Value.RespondsToCollision;
                }
                else
                {
                    aabb.Min = rigidbody.WorldFromBody.pos;
                    aabb.Max = rigidbody.WorldFromBody.pos;

                    filtersOut[index] = CollisionFilter.Zero;

                    //respondsToCollisionOut[index] = false;
                }

                aabbs[index] = aabb;
                points[index] = new PointAndIndex
                {
                    Position = aabb.Center,
                    Index = index
                };
            }
        }

        // Prepares the NumStaticBodies value for PrepareStaticBodyDataJob
        [BurstCompile]
        private struct PrepareNumStaticBodiesJob : IJob
        {
            public int numStaticBodies;
            public NativeArray<int> buildStaticTree;
            public NativeArray<int> staticBodyCount;

            public void Execute()
            {
                if (buildStaticTree[0] == 1)
                    staticBodyCount[0] = numStaticBodies;
                else
                    staticBodyCount[0] = 0;
            }
        }

        // Reads broadphase data from static rigid bodies
        [BurstCompile]
        private struct PrepareStaticBodyDataJob : IJobParallelForDefer
        {
            [ReadOnly]
            public NativeArray<RigidBody> rigidbodies;
            [ReadOnly]
            public float aabbMargin;

            public NativeArray<Aabb> aabbs;
            public NativeArray<PointAndIndex> points;
            public NativeArray<CollisionFilter> filtersOut;

            //public NativeArray<bool> respondsToCollisionOut;

            public unsafe void Execute(int index)
            {
                RigidBody body = rigidbodies[index];

                Aabb aabb;
                if (body.Collider.IsCreated)
                {
                    aabb = body.Collider.Value.CalculateAabb(body.WorldFromBody);
                    aabb.Expand(aabbMargin);

                    filtersOut[index] = body.Collider.Value.Filter;
                    //respondsToCollisionOut[index] = body.Collider.Value.RespondsToCollision;
                }
                else
                {
                    aabb.Min = body.WorldFromBody.pos;
                    aabb.Max = body.WorldFromBody.pos;

                    filtersOut[index] = CollisionFilter.Zero;
                    //respondsToCollisionOut[index] = false;
                }

                aabbs[index] = aabb;

                PointAndIndex pointAndIndex;
                pointAndIndex.Position = aabb.Center;
                pointAndIndex.Index = index;
                points[index] = pointAndIndex;
            }
        }

        [BurstCompile]
        private struct CopyRespondsToCollision : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<RigidBody> rigidbodies;

            public NativeArray<bool> respondsToCollisionOut;

            public void Execute(int index)
            {
                var collider = rigidbodies[index].Collider;

                respondsToCollisionOut[index] = collider.IsCreated ? collider.Value.RespondsToCollision : false;
            }
        }

        public struct Container
        {
            public readonly AllocatorManager.AllocatorHandle Allocator;

            [NoAlias]
            private NativeArray<Node> __nodes;
            [NoAlias]
            private NativeArray<CollisionFilter> __nodeFilters;
            [NoAlias]
            private NativeArray<CollisionFilter> __bodyFilters;
            [NoAlias]
            private NativeArray<Builder.Range> __ranges;
            [NoAlias]
            private NativeArray<int> __branchCount;

            public int bodyCount => __bodyFilters.Length;

            public NativeArray<Node>.ReadOnly nodes => __nodes.AsReadOnly();

            public NativeArray<CollisionFilter>.ReadOnly nodeFilters => __nodeFilters.AsReadOnly();

            public NativeArray<CollisionFilter>.ReadOnly bodyFilters => __bodyFilters.AsReadOnly();

            public NativeArray<Builder.Range>.ReadOnly ranges => __ranges.AsReadOnly();

            public Container(ref BroadphaseTree origin)
            {
                Allocator = origin.__nodes.allocator;

                __nodes = origin.__nodes;
                __nodeFilters = origin.__nodeFilters;
                __bodyFilters = origin.__bodyFilters;
                __ranges = origin.__ranges;
                __branchCount = origin.__branchCount;
            }

            internal Broadphase.Tree As()
            {
                Broadphase.Tree tree;
                tree.Allocator = Allocator.ToAllocator;
                tree.Nodes = __nodes;
                tree.NodeFilters = __nodeFilters;
                tree.BodyFilters = __bodyFilters;
                tree.Ranges = __ranges;
                tree.BranchCount = __branchCount;
                tree.RespondsToCollision = default;// __respondsToCollision;

                return tree;
            }
        }

        [NoAlias] 
        private NativeArrayLite<Node> __nodes; // The nodes of the bounding volume
        [NoAlias] 
        private NativeArrayLite<CollisionFilter> __nodeFilters; // The collision filter for each node (a union of all its children)
        [NoAlias] 
        private NativeArrayLite<CollisionFilter> __bodyFilters; // A copy of the collision filter of each body
        [NoAlias] 
        private NativeArrayLite<Builder.Range> __ranges; // Used during building
        [NoAlias] 
        private NativeArrayLite<int> __branchCount; // Used during building

        //[NoAlias]
        //private NativeArrayLite<bool> __respondsToCollision; // A copy of the RespondsToCollision flag of each body

        public Container container => new Container(ref this);

        public BoundingVolumeHierarchy boundingVolumeHierarchy => new BoundingVolumeHierarchy(__nodes, __nodeFilters);

        public int bodyCount => __bodyFilters.Length;

        public BroadphaseTree(int numBodies, in AllocatorManager.AllocatorHandle allocator)
        {
            BurstUtility.InitializeJob<BuildFirstNLevelsJob>();
            //BurstUtility.InitializeJobParalledForDefer<BuildBranchesJob>();
            BurstUtility.InitializeJob<FinalizeTreeJob>();
            BurstUtility.InitializeJobParallelFor<PrepareDynamicBodyDataJob>();
            BurstUtility.InitializeJob<PrepareNumStaticBodiesJob>();
            //BurstUtility.InitializeJobParalledForDefer<PrepareStaticBodyDataJob>();
            BurstUtility.InitializeJob<DisposeNativeArray<int>>();

            this = default;

            __SetCapacity(numBodies, allocator);

            __ranges = new NativeArrayLite<Builder.Range>(
                Constants.MaxNumTreeBranches, 
                allocator, 
                NativeArrayOptions.UninitializedMemory);

            __branchCount = new NativeArrayLite<int>(1, allocator, NativeArrayOptions.ClearMemory);
        }

        public void Reset(int numBodies)
        {
            if (numBodies != __bodyFilters.Length)
                __SetCapacity(numBodies, __bodyFilters.allocator);
        }

        public void Dispose()
        {
            if (__nodes.isCreated)
                __nodes.Dispose();

            if (__nodeFilters.isCreated)
                __nodeFilters.Dispose();

            if (__bodyFilters.isCreated)
                __bodyFilters.Dispose();

            if (__ranges.isCreated)
                __ranges.Dispose();

            if (__branchCount.isCreated)
                __branchCount.Dispose();

            /*if (__respondsToCollision.isCreated)
                __respondsToCollision.Dispose();*/
        }

        /// <summary>
        /// Schedule a set of jobs to build the static tree of the broadphase based on the given world.
        /// </summary>
        public JobHandle ScheduleStaticTreeBuildJobs(
            int innerloopBatchCount,
            int threadsHintCount,
            float aabbMargin, 
            in NativeArray<RigidBody> rigidbodies,
            in NativeArray<int> shouldDoWork,
            in JobHandle inputDeps)
        {
            int numRigidbodies = rigidbodies.Length;
            Assert.AreEqual(numRigidbodies, bodyCount);
            if (numRigidbodies == 0)
                return inputDeps;

            var aabbs = new NativeArray<Aabb>(numRigidbodies, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var points = new NativeArray<PointAndIndex>(numRigidbodies, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var staticBodiesCount = new NativeArray<int>(1, Allocator.TempJob);
            PrepareNumStaticBodiesJob prepareNumStaticBodiesJob;
            prepareNumStaticBodiesJob.numStaticBodies = numRigidbodies;
            prepareNumStaticBodiesJob.buildStaticTree = shouldDoWork;
            prepareNumStaticBodiesJob.staticBodyCount = staticBodiesCount;
            JobHandle handle = prepareNumStaticBodiesJob.Schedule(inputDeps);

            PrepareStaticBodyDataJob prepareStaticBodyDataJob;
            prepareStaticBodyDataJob.rigidbodies = rigidbodies;
            prepareStaticBodyDataJob.aabbs = aabbs;
            prepareStaticBodyDataJob.points = points;
            prepareStaticBodyDataJob.filtersOut = __bodyFilters;
            prepareStaticBodyDataJob.aabbMargin = aabbMargin;
            handle = prepareStaticBodyDataJob.ScheduleUnsafeIndex0(staticBodiesCount, innerloopBatchCount, handle);

            var buildHandle = __ScheduleBuildJobs(
                innerloopBatchCount,
                threadsHintCount,
                __nodes, 
                __nodeFilters, 
                points,
                aabbs,
                __bodyFilters,
                __ranges,
                __branchCount, 
                shouldDoWork,
                handle);

            return JobHandle.CombineDependencies(buildHandle, staticBodiesCount.DisposeOnJobCompletion(handle));
        }

        /// <summary>
        /// Schedule a set of jobs to build the dynamic tree of the broadphase based on the given world.
        /// </summary>
        public JobHandle ScheduleDynamicTreeBuildJobs(
            int innerloopBatchCount,
            int threadsHintCount,
            float aabbMargin,
            float timeStep,
            in float3 gravity,
            in NativeArray<MotionVelocity> motionVelocities, 
            in NativeArray<RigidBody> rigidbodies,
            in JobHandle inputDeps)
        {
            int numRigidbodies = rigidbodies.Length;
            Assert.AreEqual(numRigidbodies, bodyCount);
            if (numRigidbodies == 0)
                return inputDeps;

            var aabbs = new NativeArray<Aabb>(numRigidbodies, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var points = new NativeArray<PointAndIndex>(numRigidbodies, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            PrepareDynamicBodyDataJob prepareDynamicBodyDataJob;
            prepareDynamicBodyDataJob.rigidbodies = rigidbodies;
            prepareDynamicBodyDataJob.motionVelocities = motionVelocities;
            prepareDynamicBodyDataJob.aabbs = aabbs;
            prepareDynamicBodyDataJob.points = points;
            prepareDynamicBodyDataJob.filtersOut = __bodyFilters;
            prepareDynamicBodyDataJob.aabbMargin = aabbMargin;
            prepareDynamicBodyDataJob.timeStep = timeStep;
            prepareDynamicBodyDataJob.gravity = gravity;
            var handle = prepareDynamicBodyDataJob.Schedule(numRigidbodies, innerloopBatchCount, inputDeps);

            var shouldDoWork = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            shouldDoWork[0] = 1;

            handle = __ScheduleBuildJobs(
                innerloopBatchCount, 
                threadsHintCount,
                __nodes, 
                __nodeFilters, 
                points, 
                aabbs, 
                __bodyFilters, 
                __ranges, 
                __branchCount, 
                shouldDoWork, 
                handle);

            return shouldDoWork.DisposeOnJobCompletion(handle);
        }

        internal JobHandle _CopyTo(ref Broadphase.Tree tree, in NativeArray<RigidBody> rigidbodies, in JobHandle inputDeps)
        {
            JobHandle jobHandle;
            var jobHandles = new NativeArray<JobHandle>(6, Allocator.Temp);
            {
                jobHandles[0] = tree.Nodes.CopyFrom(__nodes, inputDeps);
                jobHandles[1] = tree.NodeFilters.CopyFrom(__nodeFilters, inputDeps);
                jobHandles[2] = tree.BodyFilters.CopyFrom(__bodyFilters, inputDeps);
                jobHandles[3] = tree.Ranges.CopyFrom(__ranges, inputDeps);
                jobHandles[4] = tree.BranchCount.CopyFrom(__branchCount, inputDeps);

                CopyRespondsToCollision copyRespondsToCollision;
                copyRespondsToCollision.rigidbodies = rigidbodies;
                copyRespondsToCollision.respondsToCollisionOut = tree.RespondsToCollision;
                jobHandles[5] = copyRespondsToCollision.Schedule(rigidbodies.Length, 1, inputDeps);

                jobHandle = JobHandle.CombineDependencies(jobHandles);
            }
            jobHandles.Dispose();

            return jobHandle;
        }

        private static JobHandle __ScheduleBuildJobs(
            int innerloopBatchCount,
            int threadsHintCount,
            NativeArray<Node> nodes,
            NativeArray<CollisionFilter> nodeFilters,
            NativeArray<PointAndIndex> points,
            NativeArray<Aabb> aabbs,
            NativeArray<CollisionFilter> bodyFilters,
            NativeArray<Builder.Range> ranges,
            NativeArray<int> branchCount,
            in NativeArray<int> shouldDoWork, 
            in JobHandle inputDeps)
        {
            JobHandle handle = inputDeps;

            var branchNodeOffsets = new NativeArray<int>(Constants.MaxNumTreeBranches, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var oldBranchCount = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            // Build initial branches
            BuildFirstNLevelsJob buildFirstNLevelsJob;
            buildFirstNLevelsJob.points = points;
            buildFirstNLevelsJob.nodes = nodes;
            buildFirstNLevelsJob.nodeFilters = nodeFilters;
            buildFirstNLevelsJob.ranges = ranges;
            buildFirstNLevelsJob.branchNodeOffsets = branchNodeOffsets;
            buildFirstNLevelsJob.branchCount = branchCount;
            buildFirstNLevelsJob.oldBranchCount = oldBranchCount;
            buildFirstNLevelsJob.threadCount = threadsHintCount;
            buildFirstNLevelsJob.shouldDoWork = shouldDoWork;

            handle = buildFirstNLevelsJob.Schedule(handle);

            // Build branches
            BuildBranchesJob buildBranchesJob;
            buildBranchesJob.points = points;
            buildBranchesJob.aabbs = aabbs;
            buildBranchesJob.bodyFilters = bodyFilters;
            buildBranchesJob.nodes = nodes;
            buildBranchesJob.nodeFilters = nodeFilters;
            buildBranchesJob.ranges = ranges;
            buildBranchesJob.branchNodeOffsets = branchNodeOffsets;
            handle = buildBranchesJob.ScheduleUnsafeIndex0(branchCount, innerloopBatchCount, handle);

            // Note: This job also deallocates the aabbs and lookup arrays on completion
            FinalizeTreeJob finalizeTreeJob;
            finalizeTreeJob.aabbs = aabbs;
            finalizeTreeJob.nodes = nodes;
            finalizeTreeJob.nodeFilters = nodeFilters;
            finalizeTreeJob.leafFilters = bodyFilters;
            finalizeTreeJob.branchNodeOffsets = branchNodeOffsets;
            finalizeTreeJob.branchCount = branchCount;
            finalizeTreeJob.oldBranchCount = oldBranchCount;
            finalizeTreeJob.shouldDoWork = shouldDoWork;
            handle = finalizeTreeJob.Schedule(handle);

            return handle;
        }

        private void __SetCapacity(int numBodies, in AllocatorManager.AllocatorHandle allocator)
        {
            int numNodes = numBodies + Constants.MaxNumTreeBranches;

            if (__nodes.isCreated)
                __nodes.Dispose();

            __nodes = new NativeArrayLite<Node>(numNodes, allocator, NativeArrayOptions.UninitializedMemory)
            {
                // Always initialize first 2 nodes as empty, to gracefully return from queries on an empty tree
                [0] = Node.Empty,
                [1] = Node.Empty
            };

            if (__nodeFilters.isCreated)
                __nodeFilters.Dispose();

            __nodeFilters = new NativeArrayLite<CollisionFilter>(numNodes, allocator, NativeArrayOptions.UninitializedMemory)
            {
                // All queries should descend past these special root nodes
                [0] = CollisionFilter.Default,
                [1] = CollisionFilter.Default
            };

            if (__bodyFilters.isCreated)
                __bodyFilters.Dispose();

            __bodyFilters = new NativeArrayLite<CollisionFilter>(numBodies, allocator, NativeArrayOptions.UninitializedMemory);
        }
    }
}