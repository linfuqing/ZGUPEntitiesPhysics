using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Physics;
using UnityEngine;
using ZG.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.UIElements;
using FNSDK;

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

    public class PhysicsColliderDatabase : ScriptableObject, ISerializationCallbackReceiver
    {
        public enum SerializatedType
        {
            Normal,
            Identity, 
            Stream
        }

        [HideInInspector, SerializeField]
        internal SerializatedType _serializatedType;
        [HideInInspector, SerializeField, UnityEngine.Serialization.FormerlySerializedAs("__bytes")]
        internal byte[] _bytes;

        private BlobAssetReference<Unity.Physics.Collider> __collider;

        public BlobAssetReference<Unity.Physics.Collider> collider => __collider;

        public static PhysicsColliderDatabase Create(in NativeArray<CompoundCollider.ColliderBlobInstance> colliderBlobInstances, IDictionary<int, IEntityDataStreamSerializer> serializers)
        {
            PhysicsColliderDatabase result = CreateInstance<PhysicsColliderDatabase>();

            result._serializatedType = SerializatedType.Normal;

            using (var buffer = new NativeBuffer(Allocator.Temp, 1))
            {
                var writer = buffer.writer;
                writer.SerializeColliderBlobInstances(colliderBlobInstances, serializers);

                result._bytes = buffer.ToBytes();
            }

            return result;
        }

        public static PhysicsColliderDatabase Create(in NativeArray<BlobAssetReference<Unity.Physics.Collider>> colliders, IDictionary<int, IEntityDataStreamSerializer> serializers)
        {
            PhysicsColliderDatabase result = CreateInstance<PhysicsColliderDatabase>();

            result._serializatedType = SerializatedType.Identity;

            using (var buffer = new NativeBuffer(Allocator.Temp, 1))
            {
                var writer = buffer.writer;
                writer.SerializeColliders(colliders, serializers);

                result._bytes = buffer.ToBytes();
            }

            return result;
        }

        public static unsafe PhysicsColliderDatabase Create(IDictionary<int, IEntityDataStreamSerializer> serializers, params BlobAssetReference<Unity.Physics.Collider>[] colliders)
        {
            fixed (void* ptr = colliders)
            {
                return Create(CollectionHelper.ConvertExistingDataToNativeArray<BlobAssetReference<Unity.Physics.Collider>>(ptr, colliders.Length, Allocator.None, true), serializers);
            }
        }

        public unsafe Type[] componentTypes
        {
            get
            {
                if (_serializatedType != SerializatedType.Stream)
                    ((ISerializationCallbackReceiver)this).OnAfterDeserialize();

                if (_serializatedType != SerializatedType.Stream || _bytes == null)
                    return null;

                int length = _bytes.Length;
                fixed (void* ptr = _bytes)
                {
                    var appendBuffer = new UnsafeAppendBuffer(ptr, length); 
                    appendBuffer.Length = length;
                    var buffer = new UnsafeBlock((UnsafeAppendBuffer*)UnsafeUtility.AddressOf(ref appendBuffer), 0, length);
                    var reader = buffer.reader;

                    var componentTypes = new NativeList<ComponentType>(Allocator.Temp);
                    reader.DeserializeStream(ref componentTypes);

                    int numComponentTypes = componentTypes.Length;
                    if (numComponentTypes > 0)
                    {
                        Type[] types = new Type[numComponentTypes];

                        for (int i = 0; i < numComponentTypes; ++i)
                            types[i] = TypeManager.GetType(componentTypes[i].TypeIndex);

                        return types;
                    }
                }

                return null;
            }
        }

        public unsafe void Init(in Entity entity, ref EntityComponentAssigner assigner)
        {
            if (_serializatedType != SerializatedType.Stream)
                ((ISerializationCallbackReceiver)this).OnAfterDeserialize();

            if (_serializatedType != SerializatedType.Stream || _bytes == null)
                return;

            int length = _bytes.Length;
            fixed (void* ptr = _bytes)
            {
                var appendBuffer = new UnsafeAppendBuffer(ptr, length);
                var buffer = new UnsafeBlock((UnsafeAppendBuffer*)UnsafeUtility.AddressOf(ref appendBuffer), 0, length);
                var reader = buffer.reader;

                reader.DeserializeStream(ref assigner, entity);
            }
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            if (!__collider.IsCreated && _serializatedType != SerializatedType.Stream)
                return;

            using (var buffer = new NativeBuffer(Allocator.Temp, 1))
            {
                using (var colliders = new NativeList<BlobAssetReference<Unity.Physics.Collider>>(Allocator.Temp)
                {
                    __collider
                })
                {
                    var writer = buffer.writer;
                    writer.SerializeColliders(colliders.AsArray());

                    if (_bytes != null)
                        writer.Write(_bytes);
                }

                _bytes = buffer.ToBytes();
            }
            _serializatedType = SerializatedType.Identity;
        }

        unsafe void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (_bytes == null)
                return;

            if (_serializatedType != SerializatedType.Stream)
            {
                if (__collider.IsCreated)
                    __collider.Dispose();
            }

            int length = _bytes.Length;
            fixed (void* ptr = _bytes)
            {
                var appendBuffer = new UnsafeAppendBuffer(ptr, length);
                appendBuffer.Length = length;
                var buffer = new UnsafeBlock((UnsafeAppendBuffer*)UnsafeUtility.AddressOf(ref appendBuffer), 0, length);

                var reader = buffer.reader;
                var colliderBlobInstances = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.Temp);
                switch (_serializatedType)
                {
                    case SerializatedType.Normal:
                        reader.Deserialize(ref colliderBlobInstances);
                        break;
                    case SerializatedType.Identity:
                        var colliders = new NativeList<BlobAssetReference<Unity.Physics.Collider>>(Allocator.Temp);
                        reader.Deserialize(ref colliders);

                        CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                        foreach (var collider in colliders)
                        {
                            colliderBlobInstance.Collider = collider;
                            colliderBlobInstance.CompoundFromChild = RigidTransform.identity;
                            colliderBlobInstances.Add(colliderBlobInstance);
                        }

                        colliders.Dispose();
                        break;
                }

                if (!colliderBlobInstances.IsEmpty)
                {
                    __collider = CompoundCollider.Create(colliderBlobInstances.AsArray());

                    foreach (var colliderBlobInstance in colliderBlobInstances)
                        colliderBlobInstance.Collider.Dispose();
                }

                colliderBlobInstances.Dispose();

                _serializatedType = SerializatedType.Stream;

                length -= reader.position;
                if(length > 0)
                    UnsafeUtility.MemMove(ptr, reader.Read(length), length);
                //reader.DeserializeStream(__entity, ref entityManager);
            }

            if (length > 0)
                Array.Resize(ref _bytes, length);
            else
                _bytes = null;
        }
    }
}