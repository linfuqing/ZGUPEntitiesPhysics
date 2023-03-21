using Unity.Collections;
using Unity.Physics;

namespace ZG
{
    public struct BvhLeafProcessor :
               BoundingVolumeHierarchy.IColliderDistanceLeafProcessor
    {
        public NativeArray<RigidBody> rigidbodies;

        public bool DistanceLeaf<T>(ColliderDistanceInput input, int rigidBodyIndex, ref T collector) where T : struct, ICollector<DistanceHit>
        {
            input.QueryContext.IsInitialized = true;
            input.QueryContext.RigidBodyIndex = rigidBodyIndex;

            RigidBody rigidbody = rigidbodies[rigidBodyIndex];

            return rigidbody.CalculateDistance(input, ref collector);
        }
    }
}