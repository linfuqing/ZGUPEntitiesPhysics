using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using UnityEngine;

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
        [SerializeField]
        private PhysicsColliders __colliders;

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

                __colliders = null;

                _database = value;
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

        public void Refresh()
        {

        }
        
        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            if (__colliders == null && _database != null)
            {
                var colliderBlobInstances = new Unity.Collections.NativeList<CompoundCollider.ColliderBlobInstance>(Unity.Collections.Allocator.Temp);

                _database.Build(colliderBlobInstances);

                __colliders = PhysicsColliders.Create(colliderBlobInstances.AsArray(), true);

                colliderBlobInstances.Dispose();

                _database.Dispose();
                _database = null;
            }

            if (__colliders != null)
            {
                /*if (!__colliderBlobInstances.IsCreated)
                    __colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.Persistent);
                else
                    __colliderBlobInstances.Clear();

                database.Build(__colliderBlobInstances.Add);

                if (__colliderBlobInstances.IsCreated)
                {
                    EntityManager entityManager = base.entityManager;
                    if (entityManager != null && entityManager.IsCreated)
                    {
                        Entity entity;
                        Translation translation;
                        Rotation rotation;
                        PhysicsCollider physicsCollider;
                        foreach (CompoundCollider.ColliderBlobInstance colliderBlobInstance in (NativeArray<CompoundCollider.ColliderBlobInstance>)__colliderBlobInstances)
                        {
                            entity = entityManager.CreateEntity(
                                typeof(Translation),
                                typeof(Rotation),
                                typeof(PhysicsCollider));

                            translation.Value = colliderBlobInstance.CompoundFromChild.pos;
                            entityManager.SetComponentData(entity, translation);

                            rotation.Value = colliderBlobInstance.CompoundFromChild.rot;
                            entityManager.SetComponentData(entity, rotation);

                            physicsCollider.Value = colliderBlobInstance.Collider;
                            entityManager.SetComponentData(entity, physicsCollider);

                            entityManager.AddComponentObject(entity, transform);

                            if (!__entities.IsCreated)
                                __entities = new NativeList<Entity>(Allocator.Persistent);

                            __entities.Add(entity);
                        }
                    }
                }*/

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
                physicsCollider.Value = __colliders.value;
                assigner.SetComponentData(entity, physicsCollider);

                //Debug.Log($"Physics Init {name} In {world.GetExistingSystem<FrameSyncSystemGroup>().frameIndex}");
            }
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