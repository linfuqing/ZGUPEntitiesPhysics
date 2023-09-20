using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;

namespace ZG
{
    public interface IQueryResultWrapper<in T> where T : struct, IQueryResult
    {
        float3 GetSurfaceNormal(T instance);

        float3 GetPosition(T instance);
    }

    public struct ColliderCastHitWrapper : IQueryResultWrapper<ColliderCastHit>
    {
        public float3 GetSurfaceNormal(ColliderCastHit instance)
        {
            return instance.SurfaceNormal;
        }

        public float3 GetPosition(ColliderCastHit instance)
        {
            return instance.Position;
        }
    }

    public struct DistanceHitWrapper : IQueryResultWrapper<DistanceHit>
    {
        public float3 GetSurfaceNormal(DistanceHit instance)
        {
            return instance.SurfaceNormal;
        }

        public float3 GetPosition(DistanceHit instance)
        {
            return instance.Position;
        }
    }

    public struct StaticBodyCollector<T> : ICollector<T> where T : struct, IQueryResult
    {
        private int __numDynamicBodies;

        public bool EarlyOutOnFirstHit => false;

        public int NumHits { get; private set; }

        public float MaxFraction { get; private set; }

        public T closestHit { get; private set; }

        public StaticBodyCollector(int numDynamicBodies, float maxFraction)
        {
            __numDynamicBodies = numDynamicBodies;

            NumHits = 0;
            MaxFraction = maxFraction;

            closestHit = default;
        }

        #region ICollector
        public bool AddHit(T hit)
        {
            if (hit.RigidBodyIndex < __numDynamicBodies)
                return false;

            if (!PhysicsUtility.IsCloserHit(closestHit, hit, NumHits))
                return false;

            closestHit = hit;
            MaxFraction = hit.Fraction + math.FLT_MIN_NORMAL;
            NumHits = 1;

            return true;
        }

        #endregion
    }

    public struct ClosestHitCollectorExclude<T> : ICollector<T> where T : struct, IQueryResult
    {
        private int __rigidBodyIndex;

        public bool EarlyOutOnFirstHit => false;

        public int NumHits { get; private set; }

        public float MaxFraction { get; private set; }

        public T closestHit { get; private set; }
        
        public ClosestHitCollectorExclude(int rigidBodyIndex, float maxFraction)
        {
            __rigidBodyIndex = rigidBodyIndex;

            NumHits = 0;
            MaxFraction = maxFraction;

            closestHit = default;
        }

        #region ICollector

        public bool AddHit(T hit)
        {
            if (hit.RigidBodyIndex == __rigidBodyIndex)
                return false;

            if (!PhysicsUtility.IsCloserHit(closestHit, hit, NumHits))
                return false;

            closestHit = hit;
            MaxFraction = hit.Fraction + math.FLT_MIN_NORMAL;
            NumHits = 1;

            return true;
        }

        #endregion
    }

    public struct ListCollectorExclude<THit, TList, TWrapper> : ICollector<THit> 
        where THit : unmanaged, IQueryResult
        where TWrapper : unmanaged, IWriteOnlyListWrapper<THit, TList>
    {
        private int __maskRigidbodyIndex;
        //private NativeArray<RigidBody> __rigidbodies;

        public readonly bool EarlyOutOnFirstHit => false;

        public readonly int NumHits => wrapper.GetCount(hits);

        public readonly float MaxFraction { get; }

        public TList hits;

        public TWrapper wrapper;

        public ListCollectorExclude(
            int maskRigidbodyIndex,
            float maxFraction,
            //in NativeArray<RigidBody> rigidbodies,
            ref TList hits, 
            ref TWrapper wrapper)
        {
            MaxFraction = maxFraction;
            __maskRigidbodyIndex = maskRigidbodyIndex;
            //__rigidbodies = rigidbodies;
            this.hits = hits;
            this.wrapper = wrapper;
        }

        #region IQueryResult implementation

        public unsafe bool AddHit(THit hit)
        {
            if (hit.RigidBodyIndex == __maskRigidbodyIndex)
                return false;

            /*if (!__rigidbodies[hit.RigidBodyIndex].Collider.Value.GetLeaf(hit.ColliderKey, out var leaf) || PhysicsUtility.IsTrigger(ref *leaf.Collider))
                return false;*/

            int index = wrapper.GetCount(hits);
            wrapper.SetCount(ref hits, index + 1);
            wrapper.Set(ref hits, hit, index);
            
            return true;
        }
        
        #endregion
    }
    
    public struct AnyHitCollector<T> : ICollector<T> where T : struct, IQueryResult
    {
        public bool EarlyOutOnFirstHit => true;

        public int NumHits { get; private set; }

        public float MaxFraction { get; }

        public T hit { get; private set; }

        public AnyHitCollector(float maxFraction)
        {
            NumHits = 0;
            MaxFraction = maxFraction;

            hit = default;
        }

        #region ICollector

        public bool AddHit(T hit)
        {
            this.hit = hit;

            NumHits = 1;

            return true;
        }

        #endregion
    }

    public interface IPhysicsColliderHandler
    {
        bool Execute(ref Collider collider, RigidTransform transform);
    }

    public static class PhysicsUtility
    {
        public static int CompareHit<T>(in T x, in T y) where T : struct, IQueryResult
        {
            if (x.Fraction < y.Fraction)
                return -1;
            else if (x.Fraction > y.Fraction)
                return 1;

            return x.ColliderKey.Value.CompareTo(y.ColliderKey.Value);
        }

        public static bool IsCloserHit<T>(in T source, in T destination, int hitCount) where T : struct, IQueryResult
        {
            return hitCount < 1 || CompareHit(destination, source) < 0;
        }

        public static bool IsTrigger(this ref Collider collider)
        {
            if (collider.CollisionType == CollisionType.Convex)
                return UnsafeUtility.As<Collider, ConvexColliderHeader>(ref collider).Material.CollisionResponse == CollisionResponsePolicy.RaiseTriggerEvents;

            throw new System.NotSupportedException();
        }

        public static unsafe CollisionFilter GetLeafFilter(this ref Collider collider, in ColliderKey colliderKey)
        {
            return collider.GetLeaf(colliderKey, out var leaf) ? leaf.Collider->Filter : collider.Filter;
        }

        public static unsafe void Scale(this ref Collider collider, float value)
        {
            switch (collider.Type)
            {
                case ColliderType.Sphere:
                    var sphereCollider = (SphereCollider*)UnsafeUtility.AddressOf(ref collider);
                    var sphereGeometry = sphereCollider->Geometry;
                    sphereGeometry.Radius *= value;

                    sphereCollider->Geometry = sphereGeometry;

                    break;
                case ColliderType.Capsule:
                    var capsuleCollider = (CapsuleCollider*)UnsafeUtility.AddressOf(ref collider);
                    var capsuleGeometry = capsuleCollider->Geometry;
                    capsuleGeometry.Vertex0 *= value;
                    capsuleGeometry.Vertex1 *= value;
                    capsuleGeometry.Radius *= value;
                    capsuleCollider->Geometry = capsuleGeometry;
                    
                    break;
                case ColliderType.Box:
                    var boxCollider = (BoxCollider*)UnsafeUtility.AddressOf(ref collider);
                    var boxGeometry = boxCollider->Geometry;
                    boxGeometry.Size *= value;
                    boxGeometry.BevelRadius *= value;
                    boxCollider->Geometry = boxGeometry;

                    break;
                case ColliderType.Cylinder:
                    var cylinderCollider = (CylinderCollider*)UnsafeUtility.AddressOf(ref collider);
                    var cylinderGeometry = cylinderCollider->Geometry;
                    cylinderGeometry.Height *= value;
                    cylinderGeometry.Radius *= value;
                    cylinderGeometry.BevelRadius *= value;
                    cylinderCollider->Geometry = cylinderGeometry;

                    break;
                default:
                    ThrowNotSupportedException();
                    break;
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void ThrowNotSupportedException()
        {
            throw new System.NotSupportedException();
        }
    }
}