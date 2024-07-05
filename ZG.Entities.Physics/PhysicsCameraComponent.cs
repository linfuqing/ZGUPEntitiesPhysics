using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using UnityEngine;
using SphereCollider = Unity.Physics.SphereCollider;

namespace ZG
{
    [EntityComponent(typeof(Translation))]
    [EntityComponent(typeof(PhysicsCameraCollider))]
    [EntityComponent(typeof(PhysicsCameraTarget))]
    [EntityComponent(typeof(PhysicsCameraDisplacement))]
    public class PhysicsCameraComponent : EntityProxyComponent, IEntityComponent
    {
        [SerializeField]
        internal LayerMask _layerMask;

        [SerializeField]
        internal Camera _camera;
        
        private BlobAssetReference<Unity.Physics.Collider> __collider;

        public float3 position => this.GetComponentData<Translation>().Value;

        public float3 displacement => this.GetComponentData<PhysicsCameraDisplacement>().value;

        public Entity target
        {
            set
            {
                PhysicsCameraTarget target;
                target.entity = value;
                this.SetComponentData(target);
            }
        }

        public static float CalculateHorizontalFieldOfView(float vFOVRad, float aspect)
        {
            return Mathf.Atan(Mathf.Tan(vFOVRad * 0.5f) * aspect) * 2.0f;
        }

        private static float __CalculateRadius(float near, float fov, float aspect)
        {
            fov *= Mathf.Deg2Rad;

            if (aspect > 1.0f)
                fov = CalculateHorizontalFieldOfView(fov, aspect);

            float radius = near / Mathf.Cos(fov * 0.5f);

            return radius;
        }

        public unsafe void CreateOrUpdateCollider(float nearClipPlane, float fieldOfView, float aspect)
        {
            SphereGeometry geometry = default;
            geometry.Center = Vector3.zero;
            geometry.Radius = __CalculateRadius(nearClipPlane, fieldOfView, aspect);

            if (!__collider.IsCreated)
            {
                CollisionFilter filter = default;
                filter.BelongsTo = ~0u;
                filter.CollidesWith = (uint)(int)_layerMask;
                __collider = SphereCollider.Create(geometry, filter);

                PhysicsCameraCollider collider;
                collider.value = __collider;
                this.SetComponentData(collider);
            }
            else
                ((SphereCollider*)__collider.GetUnsafePtr())->Geometry = geometry;
        }

        protected void OnDestroy()
        {
            if (__collider.IsCreated)
            {
                if (gameObjectEntity.isCreated)
                {
                    PhysicsShapeDestroiedCollider collider;
                    collider.hash = 0;
                    collider.value = __collider;

                    this.AppendBuffer(collider);
                }
                else
                    __collider.Dispose();

                __collider = default;
            }
        }

        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            Translation translation;
            translation.Value = transform.position;
            assigner.SetComponentData(entity, translation);

            PhysicsCameraCollider collider;
            collider.value = BlobAssetReference<Unity.Physics.Collider>.Null;//  __CreateOrGetCollider();
            assigner.SetComponentData(entity, collider);
        }
    }
}