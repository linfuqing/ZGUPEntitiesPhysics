using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;
using Unity.Physics;

namespace ZG
{
    public interface IPhysicsCameraHandler
    {
        bool TryGetTargetPosition(
            in Entity entity, 
            in CollisionWorld collisionWorld, 
            out float3 targetPosition);
    }

    public struct PhysicsCameraCollider : IComponentData
    {
        public BlobAssetReference<Collider> value;
    }

    public struct PhysicsCameraTarget : IComponentData
    {
        public Entity entity;
    }

    public struct PhysicsCameraDisplacement : IComponentData
    {
        public float3 value;
    }

    [BurstCompile]
    public struct PhysicsCameraApply<T> : IJobChunk where T : struct, IPhysicsCameraHandler
    {
        private struct Comparer : System.Collections.Generic.IComparer<ColliderCastHit>
        {
            public int Compare(ColliderCastHit x, ColliderCastHit y)
            {
                return x.Fraction.CompareTo(y.Fraction);
            }
        }

        private struct Executor
        {
            [ReadOnly]
            public CollisionWorld collisionWorld;

            [ReadOnly]
            public NativeArray<Translation> translations;

            [ReadOnly]
            public NativeArray<PhysicsCameraTarget> targets;

            [ReadOnly]
            public NativeArray<PhysicsCameraCollider> colliders;

            [WriteOnly]
            public NativeArray<PhysicsCameraDisplacement> displacements;

            public T handler;

            public unsafe void Execute(int index)
            {
                var collider = colliders[index].value;
                if (!collider.IsCreated)
                    return;

                Entity targetEntity = targets[index].entity;
                if (!handler.TryGetTargetPosition(
                    targetEntity,
                    collisionWorld, 
                    out float3 targetPosition))
                    return;

                /*int targetRigidbodyIndex = collisionWorld.GetRigidBodyIndex(targetEntity);
                if (targetRigidbodyIndex == -1)
                    return;

                var targetRigidbody = collisionWorld.Bodies[targetRigidbodyIndex];
                if (!targetRigidbody.Collider.IsCreated)
                    return;

                float3 targetPosition = targetRigidbody.WorldFromBody.pos;*/

                //targetPosition = smoothTime > Mathf.Epsilon ? Vector3.SmoothDamp(targetPosition, targetPosition, ref __velocity, smoothTime) : targetPosition;

                /*var parentPosition = position;
                if (result.type == GameCameraType.Auto)
                {
                    Vector3 eulerAngles = transform.localRotation.eulerAngles;
                    eulerAngles.y = ZG.Mathematics.Math.GetEulerY(result.targetTransform.rot);
                    transform.localRotation = Quaternion.Euler(eulerAngles);
                }

                result.cameraTransform.pos += parentPosition - (float3)transform.position;*/

                //targetPosition.y += targetRigidbody.Collider.Value.CalculateAabb().Center.y;

                float3 cameraPosition = translations[index].Value;

                ColliderCastInput colliderCastInput = default;
                colliderCastInput.Collider = (Collider*)collider.GetUnsafePtr();
                colliderCastInput.Orientation = quaternion.LookRotationSafe(targetPosition - cameraPosition, math.up());// targetRigidbody.WorldFromBody.rot;
                colliderCastInput.Start = targetPosition;
                colliderCastInput.End = cameraPosition;

                PhysicsCameraDisplacement displacement;
                displacement.value = float3.zero;

                var hits = new NativeList<ColliderCastHit>(Allocator.Temp);
                if (collisionWorld.CastCollider(colliderCastInput, ref hits))
                {
                    int numHits = hits.Length;
                    if (numHits > 0)
                    {
                        hits.AsArray().Sort(new Comparer());

                        float fraction = 1.0f;
                        float3 distance = targetPosition - cameraPosition, forward = math.normalizesafe(distance);
                        ColliderCastHit hit;
                        var rigidbodies = collisionWorld.Bodies;
                        for (int i = 0; i < numHits; ++i)
                        {
                            hit = hits[i];
                            if (hit.Fraction < math.FLT_MIN_NORMAL || math.dot(hit.SurfaceNormal, forward) < math.FLT_MIN_NORMAL || rigidbodies[hit.RigidBodyIndex].Entity == targetEntity)
                                continue;

                            fraction = math.min(fraction, hit.Fraction);
                        }

                        if (fraction < 1.0f)
                            displacement.value += distance * (1.0f - fraction);

                        //parentPosition.y += target.collider.Value.CalculateAabb().Max.y * fraction;
                    }
                }
                hits.Dispose();

                displacements[index] = displacement;
            }
        }

        [ReadOnly]
        public CollisionWorldContainer collisionWorld;

        [ReadOnly]
        public ComponentTypeHandle<Translation> translationType;

        [ReadOnly]
        public ComponentTypeHandle<PhysicsCameraTarget> targetType;

        [ReadOnly]
        public ComponentTypeHandle<PhysicsCameraCollider> colliderType;

        public ComponentTypeHandle<PhysicsCameraDisplacement> displacementType;

        public T handler;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            Executor executor;
            executor.collisionWorld = collisionWorld;
            executor.translations = chunk.GetNativeArray(ref translationType);
            executor.targets = chunk.GetNativeArray(ref targetType);
            executor.colliders = chunk.GetNativeArray(ref colliderType);
            executor.displacements = chunk.GetNativeArray(ref displacementType);
            executor.handler = handler;

            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out int i))
                executor.Execute(i);
        }
    }

    public struct PhysicsCameraSystemCore
    {
        private EntityQuery __group;

        private ComponentTypeHandle<Translation> __translationType;

        private ComponentTypeHandle<PhysicsCameraTarget> __targetType;

        private ComponentTypeHandle<PhysicsCameraCollider> __colliderType;

        private ComponentTypeHandle<PhysicsCameraDisplacement> __displacementType;

        public PhysicsCameraSystemCore(ref SystemState state)
        {
            using (var builder = new EntityQueryBuilder(Allocator.Temp))
                __group = builder
                        .WithAll<Translation, PhysicsCameraTarget, PhysicsCameraCollider>()
                        .WithAllRW<PhysicsCameraDisplacement>()
                        .Build(ref state);

            __translationType = state.GetComponentTypeHandle<Translation>(true);
            __targetType = state.GetComponentTypeHandle<PhysicsCameraTarget>(true);
            __colliderType = state.GetComponentTypeHandle<PhysicsCameraCollider>(true);
            __displacementType = state.GetComponentTypeHandle<PhysicsCameraDisplacement>();
        }

        public void Update<T>(
            in T handler, 
            in CollisionWorldContainer collisionWorld, 
            ref SystemState state) where T : struct, IPhysicsCameraHandler
        {
            PhysicsCameraApply<T> apply;
            apply.collisionWorld = collisionWorld;
            apply.translationType = __translationType.UpdateAsRef(ref state);
            apply.targetType = __targetType.UpdateAsRef(ref state);
            apply.colliderType = __colliderType.UpdateAsRef(ref state);
            apply.displacementType = __displacementType.UpdateAsRef(ref state);
            apply.handler = handler;

            state.Dependency = apply.ScheduleParallelByRef(__group, state.Dependency);
        }
    }
}