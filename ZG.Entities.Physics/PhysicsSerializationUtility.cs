using System;
using System.Collections.Generic;
using System.IO;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Collections;

namespace ZG
{
    public static class PhysicsSerializationUtility
    {
        /*public struct MemoryBinaryWriter : Unity.Entities.Serialization.BinaryWriter
        {
            public NativeBuffer.Writer __instance;

            public MemoryBinaryWriter(ref NativeBuffer.Writer instance)
            {
                __instance = instance;
            }

            public unsafe void WriteBytes(void* data, int bytes)
            {
                __instance.Write(data, bytes);
            }

            public void Dispose()
            {

            }
        }

        public struct MemoryBinaryReader : Unity.Entities.Serialization.BinaryReader
        {
            public NativeBuffer.Reader __instance;

            public MemoryBinaryReader(ref NativeBuffer.Reader instance)
            {
                __instance = instance;
            }

            public unsafe void ReadBytes(void* data, int bytes)
            {
                UnsafeUtility.MemCpy(data, __instance.Read(bytes), bytes);
            }

            public void Dispose()
            {

            }
        }*/

        public static void Serialize(this BinaryWriter writer, CollisionFilter filter)
        {
            writer.Write(filter.GroupIndex);
            writer.Write(filter.BelongsTo);
            writer.Write(filter.CollidesWith);
        }

        public static void Serialize(this BinaryWriter writer, Material material)
        {
            writer.Write((byte)material.CollisionResponse);
            writer.Write((byte)material.FrictionCombinePolicy);
            writer.Write((byte)material.RestitutionCombinePolicy);
            writer.Write(material.Friction);
            writer.Write(material.Restitution);
        }

        public static unsafe void SerializeColliders(
               this BinaryWriter binaryWriter,
               ICollection<BlobAssetReference<Collider>> colliders)
        {
            int count = colliders == null ? 0 : colliders.Count;
            binaryWriter.Write(count);
            if (count > 0)
            {
                int memorySize;
                byte[] bytes = null;
                foreach (var collider in colliders)
                {
                    memorySize = collider.Value.MemorySize;

                    binaryWriter.Write(memorySize);

                    if (bytes == null || bytes.Length < memorySize)
                        bytes = new byte[memorySize];

                    fixed (void* ptr = bytes)
                        UnsafeUtility.MemCpy(ptr, collider.GetUnsafePtr(), memorySize);

                    binaryWriter.Write(bytes, 0, memorySize);
                }
            }
        }

        public static unsafe void SerializeColliderBlobInstances(
            this BinaryWriter writer,
            IEnumerable<CompoundCollider.ColliderBlobInstance> colliderBlobInstances)
        {
            int index;
            List<BlobAssetReference<Collider>> colliders = null;
            Dictionary<BlobAssetReference<Collider>, int> indices = null;
            foreach (CompoundCollider.ColliderBlobInstance colliderBlobInstance in colliderBlobInstances)
            {
                if (indices == null)
                    indices = new Dictionary<BlobAssetReference<Collider>, int>();

                if (!indices.TryGetValue(colliderBlobInstance.Collider, out index))
                {
                    if (colliders == null)
                        colliders = new List<BlobAssetReference<Collider>>();

                    index = colliders.Count;

                    colliders.Add(colliderBlobInstance.Collider);

                    indices[colliderBlobInstance.Collider] = index;
                }

                writer.Write(index);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.x);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.y);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.z);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.w);
                writer.Write(colliderBlobInstance.CompoundFromChild.pos.x);
                writer.Write(colliderBlobInstance.CompoundFromChild.pos.y);
                writer.Write(colliderBlobInstance.CompoundFromChild.pos.z);
            }

            writer.Write(-1);

            SerializeColliders(writer, colliders);
        }

        public static unsafe void SerializeColliders(
               this ref NativeBuffer.Writer writer,
               in NativeArray<BlobAssetReference<Collider>> colliders)
        {
            int count = colliders.IsCreated ? colliders.Length : 0;
            writer.Write(count);
            if (count > 0)
            {
                //using (var temp = new MemoryBinaryWriter(ref writer))
                {
                    int memorySize;
                    BlobAssetReference<Collider> collider;
                    for (int i = 0; i < count; ++i)
                    {
                        collider = colliders[i];

                        memorySize = collider.Value.MemorySize;

                        writer.Write(memorySize);

                        writer.Write(collider.GetUnsafePtr(), memorySize);

                        //temp.Write(collider);
                    }
                }
            }
        }

        public static unsafe void SerializeColliderBlobInstances(
            this ref NativeBuffer.Writer writer,
            in NativeArray<CompoundCollider.ColliderBlobInstance> colliderBlobInstances)
        {
            int index, count = colliderBlobInstances.Length;
            CompoundCollider.ColliderBlobInstance colliderBlobInstance;
            NativeList<BlobAssetReference<Collider>> colliders = default;
            NativeParallelHashMap<BlobAssetReference<Collider>, int> indices = default;
            for(int i = 0; i < count; ++i)
            {
                colliderBlobInstance = colliderBlobInstances[i];

                if (!indices.IsCreated)
                    indices = new NativeParallelHashMap<BlobAssetReference<Collider>, int>(1, Allocator.Temp);

                if (!indices.TryGetValue(colliderBlobInstance.Collider, out index))
                {
                    if (!colliders.IsCreated)
                        colliders = new NativeList<BlobAssetReference<Collider>>(Allocator.Temp);

                    index = colliders.Length;

                    colliders.Add(colliderBlobInstance.Collider);

                    indices[colliderBlobInstance.Collider] = index;
                }

                writer.Write(index);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.x);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.y);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.z);
                writer.Write(colliderBlobInstance.CompoundFromChild.rot.value.w);
                writer.Write(colliderBlobInstance.CompoundFromChild.pos.x);
                writer.Write(colliderBlobInstance.CompoundFromChild.pos.y);
                writer.Write(colliderBlobInstance.CompoundFromChild.pos.z);
            }

            if (indices.IsCreated)
                indices.Dispose();

            writer.Write(-1);

            SerializeColliders(ref writer, colliders.AsArray());

            if (colliders.IsCreated)
                colliders.Dispose();
        }

        public static CollisionFilter DeserializeCollisionFilter(this BinaryReader reader)
        {
            CollisionFilter filter;
            filter.GroupIndex = reader.ReadInt32();
            filter.BelongsTo = reader.ReadUInt32();
            filter.CollidesWith = reader.ReadUInt32();

            return filter;
        }

        public static Material DeserializeMaterial(this BinaryReader reader)
        {
            Material material = default;
            material.CollisionResponse = (CollisionResponsePolicy)reader.ReadByte();
            material.FrictionCombinePolicy = (Material.CombinePolicy)reader.ReadByte();
            material.RestitutionCombinePolicy = (Material.CombinePolicy)reader.ReadByte();
            material.Friction = reader.ReadSingle();
            material.Restitution = reader.ReadSingle();

            return material;
        }

        public static void Deserialize(
            this BinaryReader reader,
            Action<BlobAssetReference<Collider>> colliders)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; ++i)
                colliders(BlobAssetReference<Collider>.Create(reader.ReadBytes(reader.ReadInt32())));
        }

        public static void Deserialize(
            this BinaryReader reader,
            ref NativeList<BlobAssetReference<Collider>> colliders)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; ++i)
                colliders.Add(BlobAssetReference<Collider>.Create(reader.ReadBytes(reader.ReadInt32())));
        }

        public static void Deserialize(
            this BinaryReader reader,
            Action<CompoundCollider.ColliderBlobInstance> colliderBlobInstances)
        {
            int index = reader.ReadInt32();
            List<KeyValuePair<int, RigidTransform>> transforms = null;
            while (index != -1)
            {
                if (transforms == null)
                    transforms = new List<KeyValuePair<int, RigidTransform>>();

                transforms.Add(new KeyValuePair<int, RigidTransform>(
                    index,
                    new RigidTransform(
                        new quaternion(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle()),
                        new float3(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle()))
                    ));
                index = reader.ReadInt32();
            }
            
            List<BlobAssetReference<Collider>> colliders = new List<BlobAssetReference<Collider>>();
            Deserialize(reader, colliders.Add);
            
            CompoundCollider.ColliderBlobInstance colliderBlobInstance;
            foreach (var transform in transforms)
            {
                colliderBlobInstance.Collider = colliders[transform.Key];
                colliderBlobInstance.CompoundFromChild = transform.Value;

                colliderBlobInstances(colliderBlobInstance);
            }
        }

        public static void Deserialize(
            this BinaryReader reader,
            ref NativeList<CompoundCollider.ColliderBlobInstance> colliderBlobInstances)
        {
            int index = reader.ReadInt32();
            List<KeyValuePair<int, RigidTransform>> transforms = null;
            while (index != -1)
            {
                if (transforms == null)
                    transforms = new List<KeyValuePair<int, RigidTransform>>();

                transforms.Add(new KeyValuePair<int, RigidTransform>(
                    index,
                    new RigidTransform(
                        new quaternion(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle()),
                        new float3(
                            reader.ReadSingle(),
                            reader.ReadSingle(),
                            reader.ReadSingle()))
                    ));
                index = reader.ReadInt32();
            }

            List<BlobAssetReference<Collider>> colliders = new List<BlobAssetReference<Collider>>();
            Deserialize(reader, colliders.Add);

            CompoundCollider.ColliderBlobInstance colliderBlobInstance;
            foreach (var transform in transforms)
            {
                colliderBlobInstance.Collider = colliders[transform.Key];
                colliderBlobInstance.CompoundFromChild = transform.Value;

                colliderBlobInstances.Add(colliderBlobInstance);
            }
        }

        public unsafe static void Deserialize<T>(
            this ref T reader,
            ref NativeList<BlobAssetReference<Collider>> colliderBlobInstances) where T : struct, IUnsafeReader
        {
            int count = reader.Read<int>(), length;
            //using (var temp = new MemoryBinaryReader(ref reader))
            {
                for (int i = 0; i < count; ++i)
                {
                    length = reader.Read<int>();
                    colliderBlobInstances.Add(BlobAssetReference<Collider>.Create(reader.Read(length), length));

                    //colliderBlobInstances.Add(temp.Read<Collider>());
                }
            }
        }

        public static void Deserialize<T>(
            this ref T reader,
            ref NativeList<CompoundCollider.ColliderBlobInstance> colliderBlobInstances) where T : struct, IUnsafeReader
        {
            int index = reader.Read<int>();
            var transforms = new NativeParallelHashMap<int, RigidTransform>(1, Allocator.Temp);
            while (index != -1)
            {
                transforms.Add(
                    index,
                    new RigidTransform(
                        new quaternion(
                            reader.Read<float>(),
                            reader.Read<float>(),
                            reader.Read<float>(),
                            reader.Read<float>()),
                        new float3(
                            reader.Read<float>(),
                            reader.Read<float>(),
                            reader.Read<float>()))
                    );
                index = reader.Read<int>();
            }

            var colliders = new NativeList<BlobAssetReference<Collider>>(Allocator.Temp);
            Deserialize(ref reader, ref colliders);

            CompoundCollider.ColliderBlobInstance colliderBlobInstance;
            KeyValue<int, RigidTransform> keyValue;
            var enumerator = transforms.GetEnumerator();
            while(enumerator.MoveNext())
            {
                keyValue = enumerator.Current;

                colliderBlobInstance.Collider = colliders[keyValue.Key];
                colliderBlobInstance.CompoundFromChild = keyValue.Value;

                colliderBlobInstances.Add(colliderBlobInstance);
            }

            colliders.Dispose();
            transforms.Dispose();
        }
    }
}