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

        public
#if UNITY_EDITOR
            new
#endif
            Camera camera
        {
            get => _camera;

            set
            {
                _camera = value;

                __UpdateCollider();
            }
        }

        public static float CalaculeHorizontalFieldOfView(float vFOVRad, float aspect)
        {
            return Mathf.Atan(Mathf.Tan(vFOVRad * 0.5f) * aspect) * 2.0f;
        }

        private static float __CalaculeRadius(float near, float fov, float aspect)
        {
            fov *= Mathf.Deg2Rad;

            if (aspect > 1.0f)
                fov = CalaculeHorizontalFieldOfView(fov, aspect);

            float radius = near / Mathf.Cos(fov * 0.5f);

            return radius;
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

        private SphereGeometry __GetGeometry()
        {
            if (_camera == null)
                _camera = GetComponent<Camera>();

            SphereGeometry geometry = default;
            geometry.Center = Vector3.zero;
            geometry.Radius = __CalaculeRadius(_camera.nearClipPlane, _camera.fieldOfView, _camera.aspect);

            return geometry;
        }

        private unsafe BlobAssetReference<Unity.Physics.Collider> __CreateOrGetCollider()
        {
            if (!__collider.IsCreated)
            {
                CollisionFilter filter = default;
                filter.BelongsTo = ~0u;
                filter.CollidesWith = (uint)(int)_layerMask;
                __collider = SphereCollider.Create(__GetGeometry(), filter);
            }

            return __collider;
        }

        private unsafe void __UpdateCollider()
        {
            if (!__collider.IsCreated)
                return;

            ((SphereCollider*)__collider.GetUnsafePtr())->Geometry = __GetGeometry();
        }

        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            Translation translation;
            translation.Value = transform.position;
            assigner.SetComponentData(entity, translation);

            PhysicsCameraCollider collider;
            collider.value = __CreateOrGetCollider();
            assigner.SetComponentData(entity, collider);
        }
    }
}