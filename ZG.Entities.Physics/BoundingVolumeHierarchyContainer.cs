using Unity.Collections;
using Unity.Entities;
using Unity.Physics;

namespace ZG
{
    public struct BoundingVolumeHierarchyLite
    {
        internal NativeArrayLite<BoundingVolumeHierarchy.Node> _nodes;
        internal NativeArrayLite<CollisionFilter> _nodeFilters;

        public static implicit operator BoundingVolumeHierarchy(BoundingVolumeHierarchyLite lite)
        {
            return new BoundingVolumeHierarchy(lite._nodes, lite._nodeFilters);
        }

        public void Dispose()
        {
            _nodes.Dispose();
            _nodeFilters.Dispose();
        }
    }

    public struct BoundingVolumeHierarchyLiteEx
    {
        internal NativeArrayLite<RigidBody> _rigidbodies;
        internal BoundingVolumeHierarchyLite _value;

        public NativeArray<RigidBody> rigidbodies => _rigidbodies;

        public BoundingVolumeHierarchy value => _value;

        public void Dispose()
        {
            _rigidbodies.Dispose();
            _value.Dispose();
        }

        public void CreateRigidbodyIndices(NativeParallelHashMap<Entity, int> results)
        {
            RigidBody rigidbody;
            int numRigidbodies = _rigidbodies.Length;
            for(int i = 0; i < numRigidbodies; ++i)
            {
                rigidbody = _rigidbodies[i];
                if (rigidbody.Entity == Entity.Null)
                    continue;

                results.Add(rigidbody.Entity, i);
            }
        }

        public bool CalculateDistance<T>(in ColliderDistanceInput input, ref T collector) where T : struct, ICollector<DistanceHit>
        {
            BvhLeafProcessor bvhLeafProcessor;
            bvhLeafProcessor.rigidbodies = _rigidbodies;

            return value.Distance(input, ref bvhLeafProcessor, ref collector);
        }

        public bool CalculateDistance(in ColliderDistanceInput input)
        {
            var collector = new AnyHitCollector<DistanceHit>(input.MaxDistance);

            return CalculateDistance(input, ref collector);
        }
    }

    public static class BoundingVolumeHierarchyUtility
    {
        public static BoundingVolumeHierarchyLite CreateBoundingVolumeHierarchy(
            this in BroadphaseTree.Container container, 
            in AllocatorManager.AllocatorHandle allocator)
        {
            BoundingVolumeHierarchyLite result;
            var nodes = container.nodes;
            result._nodes = new NativeArrayLite<BoundingVolumeHierarchy.Node>(nodes.Length, allocator, NativeArrayOptions.UninitializedMemory);
            NativeArray<BoundingVolumeHierarchy.Node>.Copy(nodes, result._nodes);

            var nodeFilters = container.nodeFilters;
            result._nodeFilters = new NativeArrayLite<CollisionFilter>(nodeFilters.Length, allocator, NativeArrayOptions.UninitializedMemory);
            NativeArray<CollisionFilter>.Copy(nodeFilters, result._nodeFilters);

            return result;
        }

        public static BoundingVolumeHierarchyLiteEx CreateBoundingVolumeHierarchyDynamic(
            this in CollisionWorldContainer container, 
            in AllocatorManager.AllocatorHandle allocator)
        {
            BoundingVolumeHierarchyLiteEx result;
            var dynamicBodies = container.dynamicBodies;
            result._rigidbodies = new NativeArrayLite<RigidBody>(dynamicBodies.Length, allocator, NativeArrayOptions.UninitializedMemory);
            NativeArray<RigidBody>.Copy(dynamicBodies, result._rigidbodies);

            result._value = CreateBoundingVolumeHierarchy(container.broadphase.dynamicTree, allocator);

            return result;
        }

        public static BoundingVolumeHierarchyLiteEx CreateBoundingVolumeHierarchyStatic(this in CollisionWorldContainer container, Allocator allocator)
        {
            BoundingVolumeHierarchyLiteEx result;
            var staticBodies = container.staticBodies;
            result._rigidbodies = new NativeArrayLite<RigidBody>(staticBodies.Length, allocator, NativeArrayOptions.UninitializedMemory);
            NativeArray<RigidBody>.Copy(staticBodies, result._rigidbodies);

            result._value = CreateBoundingVolumeHierarchy(container.broadphase.staticTree, allocator);

            return result;
        }
    }
}