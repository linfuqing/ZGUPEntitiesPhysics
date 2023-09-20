using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;

namespace ZG
{
    public struct PhysicsShapeCompoundCollider : IComponentData
    {
        public BlobAssetReference<Unity.Physics.Collider> value;
    }

    public struct PhysicsShapeParent : IComponentData
    {
        public int index;
        public Entity entity;
    }

    public struct PhysicsShapeCollider : IComponentData
    {
        public float contactTolerance;
        public BlobAssetReference<Unity.Physics.Collider> value;
    }

    public struct PhysicsShapeColliderBlobInstance : IBufferElementData
    {
        public CompoundCollider.ColliderBlobInstance value;
    }

    [InternalBufferCapacity(1)]
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

    [InternalBufferCapacity(1)]
    public struct PhysicsShapeChildHit : IBufferElementData
    {
        public RigidBody rigidbody;
        public DistanceHit value;
    }

    /*public struct PhysicsShapeTriggerEventRevicer : IBufferElementData
    {
        public int eventIndex;

        public Entity entity;
    }*/

    public struct PhysicsShapeChildEntity : ICleanupBufferElementData
    {
        public Entity value;
    }

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
}