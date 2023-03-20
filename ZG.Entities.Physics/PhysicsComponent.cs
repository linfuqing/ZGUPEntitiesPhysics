using Unity.Mathematics;
using Unity.Transforms;
using Unity.Entities;
using Unity.Physics;

namespace ZG
{
    public interface IPhysicsComponent : IGameObjectEntity
    {
        bool enabled { get; }

        GameObjectEntity gameObjectEntity { get; }

        //void Refresh();
    }

    public static class PhysicsComponentUtility
    {
        public static void SetRotation(this IPhysicsComponent instance, in quaternion value, EntityCommander commander)
        {
            Rotation rotation;
            rotation.Value = value;
            commander.SetComponentData(instance.entity, rotation);
        }

        public static void SetRotation(this IPhysicsComponent instance, in quaternion value)
        {
            Rotation rotation;
            rotation.Value = value;
            instance.SetComponentData(rotation);
        }

        public static void SetPosition(this IPhysicsComponent instance, in float3 value)
        {
            Translation translation;
            translation.Value = value;
            instance.SetComponentData(translation);
        }

        public static void SetPosition(this IPhysicsComponent instance, in float3 value, EntityCommander commander)
        {
            Translation translation;
            translation.Value = value;
            commander.SetComponentData(instance.entity, translation);
        }

        public static float3 GetPosition(this IPhysicsComponent instance)
        {
            return instance.GetComponentData<Translation>().Value;
        }

        public static quaternion GetRotation(this IPhysicsComponent instance)
        {
            return instance.GetComponentData<Rotation>().Value;
        }

        public static RigidTransform GetTransform(this IPhysicsComponent instance)
        {
            return math.RigidTransform(GetRotation(instance), GetPosition(instance));
        }

        public static BlobAssetReference<Collider> GetCollider(this IPhysicsComponent instance)
        {
#if UNITY_EDITOR
            if (!UnityEngine.Application.isPlaying)
                return BlobAssetReference<Collider>.Null;
#endif

            return instance.GetComponentData<PhysicsShapeCompoundCollider>().value;
        }

        public static BlobAssetReference<Collider> GetPhysicsCollider(this IPhysicsComponent instance)
        {
            return instance.GetComponentData<PhysicsCollider>().Value;
        }

        public static unsafe bool DistanceTo(this IPhysicsComponent instance, IPhysicsComponent target, CollisionFilter filter, float maxDistance)
        {
            Collider* source = (Collider*)GetPhysicsCollider(instance).GetUnsafePtr(),//__collider.GetUnsafePtr(),
                destination = (Collider*)GetPhysicsCollider(target)/*__collider*/.GetUnsafePtr();
            if (destination == null && source == null)
                return math.distance(GetPosition(instance), GetPosition(target)) <= maxDistance;

            RigidTransform transform = GetTransform(instance);
            if (source == null)
            {
                transform = math.mul(math.inverse(GetTransform(target)), transform);

                PointDistanceInput pointDistanceInput = default;
                pointDistanceInput.Position = transform.pos;
                pointDistanceInput.MaxDistance = maxDistance;
                pointDistanceInput.Filter = filter;
                return destination->CalculateDistance(pointDistanceInput);
            }
            else
            {
                transform = math.inverse(transform);
                transform = math.mul(transform, GetTransform(target));
                if (destination == null)
                {
                    PointDistanceInput pointDistanceInput = default;
                    pointDistanceInput.Position = transform.pos;
                    pointDistanceInput.MaxDistance = maxDistance;
                    pointDistanceInput.Filter = filter;
                    return source->CalculateDistance(pointDistanceInput);
                }
                else
                {
                    ColliderDistanceInput colliderDistanceInput = default;
                    colliderDistanceInput.Collider = destination;
                    colliderDistanceInput.Transform = transform;
                    colliderDistanceInput.MaxDistance = maxDistance;
                    return source->CalculateDistance(colliderDistanceInput);
                }
            }
        }

        public static unsafe bool DistanceTo(this IPhysicsComponent instance, IPhysicsComponent target, CollisionFilter filter, ref float distance)
        {
            Collider* source = (Collider*)GetPhysicsCollider(instance).GetUnsafePtr(),//.GetUnsafePtr(),
                destination = (Collider*)GetPhysicsCollider(target)/*__collider*/.GetUnsafePtr();
            if (destination == null && source == null)
                return math.distance(GetPosition(instance), GetPosition(target)) <= distance;

            RigidTransform transform = GetTransform(instance);
            if (source == null)
            {
                transform = math.mul(math.inverse(GetTransform(target)), transform);

                uint belongsTo = filter.BelongsTo;
                filter.BelongsTo = filter.CollidesWith;
                filter.CollidesWith = belongsTo;

                PointDistanceInput pointDistanceInput = default;
                pointDistanceInput.Position = transform.pos;
                pointDistanceInput.MaxDistance = distance;
                pointDistanceInput.Filter = filter;
                bool result = destination->CalculateDistance(pointDistanceInput, out var closestHit);

                distance = closestHit.Distance;

                return result;
            }
            else
            {
                transform = math.inverse(transform);
                transform = math.mul(transform, GetTransform(target));
                if (destination == null)
                {
                    PointDistanceInput pointDistanceInput = default;
                    pointDistanceInput.Position = transform.pos;
                    pointDistanceInput.MaxDistance = distance;
                    pointDistanceInput.Filter = filter;
                    bool result = source->CalculateDistance(pointDistanceInput, out var closestHit);

                    distance = closestHit.Distance;

                    return result;
                }
                else
                {
                    ColliderDistanceInput colliderDistanceInput = default;
                    colliderDistanceInput.Collider = destination;
                    colliderDistanceInput.Transform = transform;
                    colliderDistanceInput.MaxDistance = distance;
                    bool result = source->CalculateDistance(colliderDistanceInput, out var closestHit);

                    distance = closestHit.Distance;

                    return result;
                }
            }
        }

        public static unsafe bool DistanceTo(this IPhysicsComponent instance, float3 position, CollisionFilter filter, float maxDistance)
        {
            Collider* collider = (Collider*)GetPhysicsCollider(instance).GetUnsafePtr();
            if (collider == null)
                return math.distance(GetPosition(instance), position) <= maxDistance;

            RigidTransform transform = GetTransform(instance);

            transform = math.inverse(transform);
            PointDistanceInput pointDistanceInput = default;
            pointDistanceInput.Position = math.transform(transform, position);
            pointDistanceInput.MaxDistance = maxDistance;
            pointDistanceInput.Filter = filter;

            return collider->CalculateDistance(pointDistanceInput);
        }

    }
}