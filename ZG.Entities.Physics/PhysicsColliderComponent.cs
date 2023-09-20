using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using UnityEngine;
using System.Collections.Generic;

namespace ZG
{
    [EntityComponent(typeof(Transform))]
    [EntityComponent(typeof(Translation))]
    [EntityComponent(typeof(Rotation))]
    [EntityComponent(typeof(PhysicsCollider))]
    public class PhysicsColliderComponent : EntityProxyComponent, IEntityComponent, IPhysicsComponent
    {
        [SerializeField]
        internal PhysicsColliderDatabase _database;

        public PhysicsColliderDatabase database
        {
            get
            {
                return _database;
            }

            set
            {
                if (_database == value)
                    return;

                _database = value;
            }
        }

        public unsafe CompoundCollider.ColliderBlobInstance mainCollider
        {
            get
            {
                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.Collider = _database.collider;
                colliderBlobInstance.CompoundFromChild = RigidTransform.identity;
                return colliderBlobInstance;
            }
        }

        public float3 position
        {
            get
            {
                return this.GetComponent<Translation>().Value;
            }

            set
            {
                Translation translation;
                translation.Value = value;
                this.SetComponentData(translation);

                var gameObjectEntity = this.gameObjectEntity;
                Transform transform = gameObjectEntity == null ? null : gameObjectEntity.transform;
                if (transform != null)
                    transform.position = value;
            }
        }

        public quaternion orientation
        {
            get
            {
                return this.GetComponent<Rotation>().Value;
            }

            set
            {
                Rotation rotation;
                rotation.Value = value;
                this.SetComponentData(rotation);

                var gameObjectEntity = this.gameObjectEntity;
                Transform transform = gameObjectEntity == null ? null : gameObjectEntity.transform;
                if (transform != null)
                    transform.rotation = value;
            }
        }

        [EntityComponents]
        public System.Type[] componentTypes => _database.componentTypes;

        public void Refresh()
        {

        }
        
        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            var transform = base.transform;

            Translation translation;
            translation.Value = transform.position;
            UnityEngine.Assertions.Assert.AreEqual(float3.zero, translation.Value);
            assigner.SetComponentData(entity, translation);

            Rotation rotation;
            rotation.Value = transform.rotation;
            UnityEngine.Assertions.Assert.AreEqual(quaternion.identity, rotation.Value);
            assigner.SetComponentData(entity, rotation);

            PhysicsCollider physicsCollider;
            physicsCollider.Value = _database.collider;
            assigner.SetComponentData(entity, physicsCollider);

            _database.Init(entity, ref assigner);
        }

        /*protected void OnDisable()
        {
            if (__entities.IsCreated)
            {
                EntityManager entityManager = base.entityManager;
                if (entityManager != null && entityManager.IsCreated)
                    entityManager.DestroyEntity(__entities);

                __entities.Dispose();
            }

            if(__colliderBlobInstances.IsCreated)
            {
                foreach (CompoundCollider.ColliderBlobInstance colliderBlobInstance in (NativeArray<CompoundCollider.ColliderBlobInstance>)__colliderBlobInstances)
                    colliderBlobInstance.Collider.Release();

                __colliderBlobInstances.Dispose();
            }
        }*/
    }
}