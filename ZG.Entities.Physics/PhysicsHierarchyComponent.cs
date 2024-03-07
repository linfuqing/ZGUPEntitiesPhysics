using Unity.Entities;
using Unity.Transforms;
using Unity.Physics;
using BitField = ZG.BitField<Unity.Collections.FixedBytes126>;
using Unity.Mathematics;

namespace ZG
{
    [EntityComponent(typeof(Translation))]
    [EntityComponent(typeof(Rotation))]
    [EntityComponent(typeof(PhysicsCollider))]
    [EntityComponent(typeof(PhysicsCustomTags))]
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
        internal byte _customTags = 1;

        [UnityEngine.SerializeField]
        internal PhysicsHierarchyDatabase _database;

        public PhysicsHierarchyDatabase database
        {
            get => _database;

#if UNITY_EDITOR
            set => _database = value;
#endif
        }

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

        public CompoundCollider.ColliderBlobInstance mainCollider
        {
            get
            {
                CompoundCollider.ColliderBlobInstance result;

                result.Collider = BlobAssetReference<Collider>.Null;
                result.CompoundFromChild = RigidTransform.identity;

                var colliders = _database.colliders;
                ref var shapes = ref _database.definition.Value.shapes;

                bool isContains;
                int numShapes = shapes.Length;
                var inactiveShapeIndices = _database.inactiveShapeIndices;
                for (int i = 0; i < numShapes; ++i)
                {
                    isContains = false;
                    if (inactiveShapeIndices != null)
                    {
                        foreach (var inactiveShapeIndex in inactiveShapeIndices)
                        {
                            if (inactiveShapeIndex == i)
                            {
                                isContains = true;

                                break;
                            }
                        }
                    }

                    if (isContains)
                        continue;

                    ref var shape = ref shapes[i];
                    if (shape.colliders.Length > 0)
                    {
                        ref var collider = ref shape.colliders[0];

                        result.Collider = colliders[collider.index];
                        result.CompoundFromChild = collider.transform;
                    }

                    break;
                }

                return result;
            }
        }

        void IEntityComponent.Init(in Entity entity, EntityComponentAssigner assigner)
        {
            /*var transform = gameObjectEntity.transform;

            Translation translation;
            translation.Value = transform.position;
            assigner.SetComponentData(entity, translation);

            //����
            Rotation rotation;
            rotation.Value = transform.rotation;
            assigner.SetComponentData(entity, rotation);*/

            PhysicsCustomTags physicsCustomTags;
            physicsCustomTags.Value = _customTags;
            assigner.SetComponentData(entity, physicsCustomTags);

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

            var inactiveShapeIndices = _database.inactiveShapeIndices;
            int numInactiveShapeIndices = inactiveShapeIndices == null ? 0 : inactiveShapeIndices.Count;
            if (numInactiveShapeIndices > 0)
            {
                int inactiveShapeIndex;
                PhysicsHierarchyInactiveTriggers[] inactiveTriggers = new PhysicsHierarchyInactiveTriggers[numInactiveShapeIndices];
                PhysicsHierarchyInactiveColliders[] inactiveColliders = new PhysicsHierarchyInactiveColliders[numInactiveShapeIndices];
                for(int i = 0; i < numInactiveShapeIndices; ++i)
                {
                    inactiveShapeIndex = inactiveShapeIndices[i];

                    inactiveTriggers[i].shapeIndex = inactiveShapeIndex;

                    inactiveColliders[i].shapeIndex = inactiveShapeIndex;
                }

                assigner.SetBuffer(EntityComponentAssigner.BufferOption.Override, entity, inactiveTriggers);
                assigner.SetBuffer(EntityComponentAssigner.BufferOption.Override, entity, inactiveColliders);
            }
        }
    }
}