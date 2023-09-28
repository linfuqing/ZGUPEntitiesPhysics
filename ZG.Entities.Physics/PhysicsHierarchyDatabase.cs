using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
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

        public const int VERSION = 0;

        [SerializeField, UnityEngine.Serialization.FormerlySerializedAs("__inactiveShapeIndices")]
        internal int[] _inactiveShapeIndices;

        [SerializeField, HideInInspector]
        private byte[] __bytes;

        [SerializeField, HideInInspector]
        private int __colliderCount;

        private bool __isInit;

        private BlobAssetReference<PhysicsHierarchyDefinition> __definition;
        private BlobAssetReference<Unity.Physics.Collider>[] __colliders;
        private Dictionary<int, BlobAssetReference<Unity.Physics.Collider>> __shapeColliders;

        public IReadOnlyList<BlobAssetReference<Unity.Physics.Collider>> colliders => __colliders;

        public IReadOnlyList<int> inactiveShapeIndices => _inactiveShapeIndices;

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

            if(__shapeColliders != null)
            {
                foreach (var shapeCollider in __shapeColliders)
                    shapeCollider.Value.Dispose();

                __shapeColliders = null;
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

        public BlobAssetReference<Unity.Physics.Collider> GetOrCreateCollider(int shapeIndex)
        {
            ref var shape = ref __definition.Value.shapes[shapeIndex];

            int numColliders = shape.colliders.Length;
            if (numColliders < 1)
                return BlobAssetReference<Unity.Physics.Collider>.Null;

            if (numColliders == 1)
            {
                ref var collider = ref shape.colliders[0];
                if (collider.transform.Equals(RigidTransform.identity))
                    return __colliders[collider.index];
            }

            if (__shapeColliders == null)
                __shapeColliders = new Dictionary<int, BlobAssetReference<Unity.Physics.Collider>>();

            if (__shapeColliders.TryGetValue(shapeIndex, out var result))
                return result;

            var colliderBlobInstances = new NativeArray<CompoundCollider.ColliderBlobInstance>(numColliders, Allocator.TempJob);

            CompoundCollider.ColliderBlobInstance colliderBlobInstance;
            for (int i = 0; i < numColliders; ++i)
            {
                ref var collider = ref shape.colliders[i];
                colliderBlobInstance.Collider = __colliders[collider.index];
                colliderBlobInstance.CompoundFromChild = collider.transform;

                colliderBlobInstances[i] = colliderBlobInstance;
            }

            result = CompoundColliderUtility.Create(colliderBlobInstances);

            colliderBlobInstances.Dispose();

            __shapeColliders[shapeIndex] = result;

            return result;
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
        public Data data;

        [HideInInspector]
        public Transform root;

        public void Create(
            BlobAssetReference<Unity.Physics.Collider>[] colliderResults,
            int[] inactiveShapeIndices)
        {
            _inactiveShapeIndices = inactiveShapeIndices;

            __colliders =  colliderResults;

            __colliderCount = __colliders == null ? 0 : __colliders.Length;
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