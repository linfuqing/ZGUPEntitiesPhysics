using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Physics;
using UnityEngine;
using ZG.Mathematics;

namespace ZG
{
    public class PhysicsColliders : SharedNativeArray<CompoundCollider.ColliderBlobInstance>, IDisposable
    {
        public enum Flag
        {
            Created = 0x01,
            AutoReleased = 0x02, 
            ValueCreated = 0x04, 
        }

        private Flag __flag;
        private BlobAssetReference<Unity.Physics.Collider> __value = BlobAssetReference<Unity.Physics.Collider>.Null;

        public bool isCreated => (__flag & Flag.Created) == Flag.Created;

        public BlobAssetReference<Unity.Physics.Collider> value
        {
            get
            {
                if (!__value.IsCreated)
                {
                    BlobAssetReference<Unity.Physics.Collider> result = BlobAssetReference<Unity.Physics.Collider>.Null;
                    var values = base.values;
                    int length = values.Length;
                    if (length > 0)
                    {
                        if(length == 1)
                        { 
                            var value = values[0];
                            if (value.CompoundFromChild.Approximately(RigidTransform.identity))
                                result = value.Collider;
                        }

                        if (result == BlobAssetReference<Unity.Physics.Collider>.Null)
                        {
                            Debug.LogWarning($"Create Colliders {name}", this);

                            result = CompoundCollider.Create(values);

                            __flag |= Flag.ValueCreated;
                        }

                        __value = result;
                    }
                }

                return __value;
            }
        }

        public static PhysicsColliders Create(NativeArray<CompoundCollider.ColliderBlobInstance> values, bool isAutoRelease)
        {
            PhysicsColliders result = Create<PhysicsColliders>(values);
            result.__flag = Flag.Created;

            if (isAutoRelease)
                result.__flag |= Flag.AutoReleased;

            return result;
        }

        ~PhysicsColliders()
        {
            Dispose();
        }
        
        public unsafe new void Dispose()
        {
            if ((__flag & Flag.ValueCreated) == Flag.ValueCreated)
            {
                //Debug.Log($"Dispose Collider {__value.GetHashCode()}");

                __value.Dispose();

                __value = BlobAssetReference<Unity.Physics.Collider>.Null;
            }

            if ((__flag & Flag.AutoReleased) == Flag.AutoReleased)
            {
                foreach (var value in this)
                {
                    if (value.Collider.IsCreated)
                    {
                        //Debug.Log($"Dispose Collider {value.Collider.GetHashCode()}");

                        value.Collider.Dispose();
                    }
                }
            }

            __flag = 0;

            base.Dispose();
        }
    }

    public class PhysicsColliderDatabase : ScriptableObject, IDisposable
    {
        public enum SerializatedType
        {
            Normal,
            Identity
        }

        [HideInInspector, SerializeField]
        internal SerializatedType _serializatedType;
        [HideInInspector, SerializeField, UnityEngine.Serialization.FormerlySerializedAs("__bytes")]
        internal byte[] _bytes;
        
        public static PhysicsColliderDatabase Create(IEnumerable<CompoundCollider.ColliderBlobInstance> colliderBlobInstances)
        {
            PhysicsColliderDatabase result = CreateInstance<PhysicsColliderDatabase>();

            result._serializatedType = SerializatedType.Normal;

            using (MemoryStream memoryStream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(memoryStream);
                writer.SerializeColliderBlobInstances(colliderBlobInstances);

                result._bytes = memoryStream.ToArray();
            }

            return result;
        }

        public static PhysicsColliderDatabase Create(ICollection<BlobAssetReference<Unity.Physics.Collider>> colliders)
        {
            PhysicsColliderDatabase result = CreateInstance<PhysicsColliderDatabase>();

            result._serializatedType = SerializatedType.Identity;

            using (MemoryStream memoryStream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(memoryStream);
                writer.SerializeColliders(colliders);

                result._bytes = memoryStream.ToArray();
            }

            return result;
        }

        public static PhysicsColliderDatabase Create(params BlobAssetReference<Unity.Physics.Collider>[] colliders)
        {
            return Create((ICollection<BlobAssetReference<Unity.Physics.Collider>>)colliders);
        }

        public void Build(NativeList<CompoundCollider.ColliderBlobInstance> colliderBlobInstances)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(_bytes)))
            {
                switch (_serializatedType)
                {
                    case SerializatedType.Normal:
                        reader.Deserialize(ref colliderBlobInstances);
                        break;
                    case SerializatedType.Identity:
                        reader.Deserialize((BlobAssetReference<Unity.Physics.Collider> collider) =>
                        {
                            CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                            colliderBlobInstance.Collider = collider;
                            colliderBlobInstance.CompoundFromChild = RigidTransform.identity;
                            colliderBlobInstances.Add(colliderBlobInstance);
                        });
                        break;
                }
            }
        }

        public void Build(Action<CompoundCollider.ColliderBlobInstance> colliderBlobInstances)
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(_bytes)))
            {
                switch(_serializatedType)
                {
                    case SerializatedType.Normal:
                        reader.Deserialize(colliderBlobInstances);
                        break;
                    case SerializatedType.Identity:
                        reader.Deserialize((BlobAssetReference<Unity.Physics.Collider> collider) =>
                        {
                            CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                            colliderBlobInstance.Collider = collider;
                            colliderBlobInstance.CompoundFromChild = RigidTransform.identity;
                            colliderBlobInstances.Invoke(colliderBlobInstance);
                        });
                        break;
                }
            }
        }

        public void Dispose()
        {
            _bytes = null;
        }
    }
}