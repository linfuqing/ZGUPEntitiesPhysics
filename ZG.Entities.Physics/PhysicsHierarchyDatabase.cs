using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Authoring;
using UnityEngine;

namespace ZG
{
    public interface IPhysicsHierarchyShape
    {
        string name { get; }

        CollisionFilter collisionFilter { get; }

        Unity.Physics.Material material { get; }

        float contactTolerance { get; }

        //PhysicsHierarchyTriggerFlag triggerFlag { get; }
    }

    [CreateAssetMenu(menuName = "ZG/Physics Hierarchy Database", fileName = "PhysicsHierarchyDatabase")]
    public class PhysicsHierarchyDatabase : ScriptableObject, ISerializationCallbackReceiver
    {
        public const int VERSION = 0;

        [SerializeField, HideInInspector]
        private byte[] __bytes;

        [SerializeField, HideInInspector]
        private int __colliderCount;

        private bool __isInit;

        private BlobAssetReference<Unity.Physics.Collider>[] __colliders;
        private BlobAssetReference<PhysicsHierarchyDefinition> __definition;

        public IReadOnlyList<BlobAssetReference<Unity.Physics.Collider>> colliders => __colliders;

        public BlobAssetReference<PhysicsHierarchyDefinition> definition
        {
            get
            {
                Init();

                return __definition;
            }
        }

        ~PhysicsHierarchyDatabase()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (__definition.IsCreated)
            {
                __definition.Dispose();

                __definition = BlobAssetReference<PhysicsHierarchyDefinition>.Null;
            }

            if (__colliders != null)
            {
                foreach (var collider in __colliders)
                {
                    if (collider.IsCreated)
                        collider.Dispose();
                }

                __colliders = null;
            }
        }

        public bool Init()
        {
            if (__isInit)
                return false;

            __isInit = true;

            var instance = SingletonAssetContainer<BlobAssetReference<Unity.Physics.Collider>>.instance;

            SingletonAssetContainerHandle handle;
            handle.instanceID = __definition.Value.instanceID;

            int numColliders = __colliders.Length;
            for (int i = 0; i < numColliders; ++i)
            {
                handle.index = i;

                instance[handle] = __colliders[i];
            }

            return true;
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (__bytes != null && __bytes.Length > 0)
            {
                if (__definition.IsCreated)
                    __definition.Dispose();

                if (__colliders != null)
                {
                    foreach (var collider in __colliders)
                    {
                        if (collider.IsCreated)
                            collider.Dispose();
                    }
                }

                unsafe
                {
                    fixed (byte* ptr = __bytes)
                    {
                        using (var reader = new MemoryBinaryReader(ptr, __bytes.LongLength))
                        {
                            int version = reader.ReadInt();

                            UnityEngine.Assertions.Assert.AreEqual(VERSION, version);

                            __definition = reader.Read<PhysicsHierarchyDefinition>();

                            __colliders = new BlobAssetReference<Unity.Physics.Collider>[__colliderCount];
                            for (int i = 0; i < __colliderCount; ++i)
                                __colliders[i] = reader.Read<Unity.Physics.Collider>();
                        }
                    }
                }

                __bytes = null;
            }

            __isInit = false;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            if (__definition.IsCreated)
            {
                using (var writer = new MemoryBinaryWriter())
                {
                    writer.Write(VERSION);
                    writer.Write(__definition);

                    __colliderCount = __colliders == null ? 0 : __colliders.Length;
                    for (int i = 0; i < __colliderCount; ++i)
                        writer.Write(__colliders[i]);

                    __bytes = writer.GetContentAsNativeArray().ToArray();
                }
            }
        }

        void OnDestroy()
        {
            Dispose();
        }

#if UNITY_EDITOR
        [Serializable]
        public struct Data
        {
            [Serializable]
            public struct Collider
            {
                public int index;
                public Vector3 position;
                public Quaternion rotation;
            }

            [Serializable]
            public struct Trigger
            {
                public string name;
                public int index;
                //public PhysicsHierarchyTriggerFlag flag;
                public float contactTolerance;
                public string tag;
            }

            [Serializable]
            public struct Shape
            {
                public string name;
                public Collider[] colliders;
                public Trigger[] triggers;
            }

            public Shape[] shapes;

            public static void Create(
                IPhysicsHierarchyShape result,
                Transform transform,
                Transform root,
                ref List<UnityEngine.Collider> colliders,
                ref List<PhysicsShapeAuthoring> shapes,
                ref List<Shape> shapeResults,
                ref List<BlobAssetReference<Unity.Physics.Collider>> colliderResults)
            {
                if (colliders == null)
                    colliders = new List<UnityEngine.Collider>();

                var collidersTemp = transform.GetComponents<UnityEngine.Collider>();
                if (collidersTemp != null && collidersTemp.Length > 0)
                    colliders.AddRange(collidersTemp);

                if (shapes == null)
                    shapes = new List<PhysicsShapeAuthoring>();

                var shapesTemp = transform.GetComponents<PhysicsShapeAuthoring>();
                if (shapesTemp != null && shapesTemp.Length > 0)
                    shapes.AddRange(shapesTemp);

                IPhysicsHierarchyShape childShape;
                List<UnityEngine.Collider> childColliders;
                List<PhysicsShapeAuthoring> childShapes;
                foreach (Transform child in transform)
                {
                    childShape = child.GetComponent<IPhysicsHierarchyShape>();
                    if (childShape == null)
                    {
                        childColliders = colliders;
                        childShapes = shapes;
                    }
                    else
                    {
                        if (!child.gameObject.activeSelf)
                            continue;

                        childColliders = null;
                        childShapes = null;
                    }

                    Create(
                        childShape,
                        child,
                        root,
                        ref childColliders,
                        ref childShapes,
                        ref shapeResults,
                        ref colliderResults);
                }

                if (result != null)
                {
                    using (var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.TempJob))
                    {
                        List<Trigger> triggers = null;
                        Trigger trigger;
                        trigger.index = 0;
                        //trigger.flag = result.triggerFlag;
                        trigger.contactTolerance = result.contactTolerance;

                        string tag;

                        var collisionFilter = result.collisionFilter;
                        if (colliders != null)
                        {
                            colliders.Convert(
                                colliderBlobInstances,
                                root,
                                result.material,
                                collisionFilter,
                                0.0f,
                                0,
                                0,
                                false,
                                false);

                            foreach (var collider in colliders)
                            {
                                if (collider.isTrigger)
                                {
                                    trigger.name = collider.name;

                                    tag = collider.tag;

                                    trigger.tag = tag == "Untagged" ? string.Empty : tag;

                                    if (triggers == null)
                                        triggers = new List<Trigger>();

                                    triggers.Add(trigger);
                                }

                                //Destroy(collider);

                                ++trigger.index;
                            }
                        }

                        if (shapes != null)
                        {
                            shapes.Convert(
                               colliderBlobInstances,
                               root,
                               collisionFilter.GroupIndex,
                               0,
                               0,
                               false,
                               false);

                            foreach (var shape in shapes)
                            {
                                if (shape.CollisionResponse == CollisionResponsePolicy.RaiseTriggerEvents)
                                {
                                    trigger.name = shape.name;

                                    tag = shape.tag;

                                    trigger.tag = tag == "Untagged" ? string.Empty : tag;

                                    if (triggers == null)
                                        triggers = new List<Trigger>();

                                    triggers.Add(trigger);
                                }

                                //Destroy(shape);

                                ++trigger.index;
                            }
                        }

                        int numColliderBlobInstances = colliderBlobInstances.Length;

                        Shape shapeResult;
                        shapeResult.name = result.name;
                        shapeResult.colliders = new Collider[numColliderBlobInstances];

                        for (int i = 0; i < numColliderBlobInstances; ++i)
                        {
                            ref var collider = ref shapeResult.colliders[i];
                            ref readonly var colliderBlobInstance = ref colliderBlobInstances.ElementAt(i);

                            if (colliderResults == null)
                                colliderResults = new List<BlobAssetReference<Unity.Physics.Collider>>();

                            collider.index = colliderResults.Count;
                            collider.position = colliderBlobInstance.CompoundFromChild.pos;
                            collider.rotation = colliderBlobInstance.CompoundFromChild.rot;

                            colliderResults.Add(colliderBlobInstance.Collider);
                        }

                        shapeResult.triggers = triggers == null ? null : triggers.ToArray();

                        if (shapeResults == null)
                            shapeResults = new List<Shape>();

                        shapeResults.Add(shapeResult);
                    }
                }
            }

            public BlobAssetReference<PhysicsHierarchyDefinition> ToAsset(int instanceID)
            {
                using (var blobBuilder = new BlobBuilder(Allocator.Temp))
                {
                    ref var root = ref blobBuilder.ConstructRoot<PhysicsHierarchyDefinition>();

                    root.instanceID = instanceID;

                    int i, j, numColliders, numTriggers, numShapes = this.shapes.Length;
                    var random = new Unity.Mathematics.Random((uint)DateTime.UtcNow.Ticks);
                    var shapes = blobBuilder.Allocate(ref root.shapes, numShapes);
                    BlobBuilderArray<PhysicsHierarchyDefinition.Collider> colliders;
                    BlobBuilderArray<PhysicsHierarchyDefinition.Trigger> triggers;
                    for (i = 0; i < numShapes; ++i)
                    {
                        ref readonly var sourceShape = ref this.shapes[i];
                        ref var destinationShape = ref shapes[i];

                        numColliders = sourceShape.colliders.Length;
                        colliders = blobBuilder.Allocate(ref destinationShape.colliders, numColliders);
                        for (j = 0; j < numColliders; ++j)
                        {
                            ref readonly var sourceCollider = ref sourceShape.colliders[j];
                            ref var destinationCollider = ref colliders[j];

                            destinationCollider.index = sourceCollider.index;
                            destinationCollider.hash = random.NextUInt();
                            destinationCollider.transform = math.RigidTransform(sourceCollider.rotation, sourceCollider.position);
                        }

                        numTriggers = sourceShape.triggers == null ? 0 : sourceShape.triggers.Length;
                        triggers = blobBuilder.Allocate(ref destinationShape.triggers, numTriggers);
                        for (j = 0; j < numTriggers; ++j)
                        {
                            ref readonly var sourceTrigger = ref sourceShape.triggers[j];
                            ref var destinationTrigger = ref triggers[j];

                            destinationTrigger.index = sourceTrigger.index;
                            destinationTrigger.contactTolerance = sourceTrigger.contactTolerance;
                            destinationTrigger.tag = sourceTrigger.tag;
                        }
                    }

                    return blobBuilder.CreateBlobAssetReference<PhysicsHierarchyDefinition>(Allocator.Persistent);
                }
            }
        }

        public Data data;

        [HideInInspector]
        public Transform root;

        public bool Create()
        {
            var result = root == null ? null : root.GetComponent<IPhysicsHierarchyShape>();
            if (result == null)
                return false;

            List<UnityEngine.Collider> colliders = null;
            List<PhysicsShapeAuthoring> shapes = null;
            List<Data.Shape> shapeResults = null;
            List<BlobAssetReference<Unity.Physics.Collider>> colliderResults = null;
            Data.Create(
                result,
                root,
                root,
                ref colliders,
                ref shapes,
                ref shapeResults,
                ref colliderResults);

            data.shapes = shapeResults == null ? null : shapeResults.ToArray();

            __colliders = colliderResults == null ? null : colliderResults.ToArray();

            __colliderCount = __colliders == null ? 0 : __colliders.Length;

            return true;
        }

        public void Rebuild()
        {
            if (__definition.IsCreated)
                __definition.Dispose();

            __definition = data.ToAsset(GetInstanceID());

            __bytes = null;

            ((ISerializationCallbackReceiver)this).OnBeforeSerialize();
        }

        public void EditorMaskDirty()
        {
            Rebuild();

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}