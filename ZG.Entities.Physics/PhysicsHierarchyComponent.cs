using Unity.Entities;
using Unity.Transforms;
using Unity.Physics;
using BitField = ZG.BitField<Unity.Collections.FixedBytes126>;

namespace ZG
{
    [EntityComponent(typeof(Translation))]
    [EntityComponent(typeof(Rotation))]
    [EntityComponent(typeof(PhysicsCollider))]
    [EntityComponent(typeof(PhysicsShapeCompoundCollider))]
    [EntityComponent(typeof(PhysicsShapeChild))]
    [EntityComponent(typeof(PhysicsShapeDestroiedCollider))]
    [EntityComponent(typeof(PhysicsHierarchyData))]
    [EntityComponent(typeof(PhysicsHierarchyTriggersBitField))]
    [EntityComponent(typeof(PhysicsHierarchyInactiveTriggers))]
    [EntityComponent(typeof(PhysicsHierarchyCollidersBitField))]
    [EntityComponent(typeof(PhysicsHierarchyInactiveColliders))]
    public class PhysicsHierarchyComponent : EntityProxyComponent, IEntityComponent, IPhysicsComponent
    {
        [UnityEngine.SerializeField]
        internal PhysicsHierarchyDatabase _database;

        /*public void Refresh()
        {
            if (!gameObjectEntity.isAssigned)
                return;

            if (isActiveAndEnabled)
                this.RemoveComponent<PhysicsExclude>();
            else
                this.AddComponent<PhysicsExclude>();
        }

        protected void OnEnable()
        {
            if(gameObjectEntity.isAssigned)
                this.RemoveComponent<PhysicsExclude>();
        }

        protected void OnDisable()
        {
            if (gameObjectEntity.isAssigned)
                this.AddComponent<PhysicsExclude>();
        }*/

        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            var transform = gameObjectEntity.transform;

            Translation translation;
            translation.Value = transform.position;
            assigner.SetComponentData(entity, translation);

            //±ØÐë
            Rotation rotation;
            rotation.Value = transform.rotation;
            assigner.SetComponentData(entity, rotation);

            PhysicsHierarchyData instance;
            instance.definition = _database.definition;
            assigner.SetComponentData(entity, instance);

            PhysicsHierarchyTriggersBitField triggersBitField;
            triggersBitField.value = BitField.Max;
            assigner.SetComponentData(entity, triggersBitField);

            PhysicsHierarchyCollidersBitField collidersBitField;
            collidersBitField.colliderType = PhysicsHierarchyCollidersBitField.ColliderType.Convex;
            collidersBitField.hash = 0;
            collidersBitField.value = BitField.Max;
            assigner.SetComponentData(entity, collidersBitField);
        }
    }
}