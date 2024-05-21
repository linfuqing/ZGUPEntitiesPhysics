using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Transforms;
using Unity.Physics;
using ZG.Unsafe;
using Math = ZG.Mathematics.Math;

namespace ZG
{
    public enum CompoundColliderDeserializeType
    {
        Collider = 0,
        ColliderBlobInstance
    }

    public static partial class CompoundColliderUtility
    {
        private struct Comparer : IComparer<KeyValuePair<ColliderKey, IEntityDataStreamSerializer>>
        {
            public int Compare(KeyValuePair<ColliderKey, IEntityDataStreamSerializer> x, KeyValuePair<ColliderKey, IEntityDataStreamSerializer> y)
            {
                return x.Key.Value.CompareTo(y.Key.Value);
            }
        }
        
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateRightNowJob : IJob
        {
            [ReadOnly]
            public NativeArray<CompoundCollider.ColliderBlobInstance> children;

            public NativeArray<BlobAssetReference<Collider>> result;

            public void Execute()
            {
                result[0] = CompoundCollider.Create(children);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateJob : IJob
        {
            public uint numColliderKeyBits;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<CompoundCollider.ColliderBlobInstance> children;

            public ComponentLookup<Translation> translations;
            public ComponentLookup<Rotation> rotations;
            public ComponentLookup<PhysicsCollider> physicsColliders;
            public ComponentLookup<PhysicsCustomTags> physicsCustomTags;

            public void Execute()
            {
                PhysicsCustomTags physicsCustomTags;
                physicsCustomTags.Value = byte.MaxValue;
                
                int entityIndex = 0, childIndex = 0, numChildren = children.Length, numEntities = entityArray.Length;
                Entity entity;
                Translation translation;
                Rotation rotation;
                PhysicsCollider physicsCollider;
                CompoundCollider.ColliderBlobInstance child;
                NativeArray<CompoundCollider.ColliderBlobInstance> results = numChildren > numEntities ?
                    new NativeArray<CompoundCollider.ColliderBlobInstance>(numChildren - numEntities + 1, Allocator.Temp) : default;
                for (int i = 0; i < numChildren; i++)
                {
                    child = children[i];
                    if (child.Collider.Value.TotalNumColliderKeyBits + numColliderKeyBits > 32 || !results.IsCreated)
                    {
                        entity = entityArray[entityIndex++];

                        translation.Value = child.CompoundFromChild.pos;
                        translations[entity] = translation;

                        rotation.Value = child.CompoundFromChild.rot;
                        rotations[entity] = rotation;

                        physicsCollider.Value = child.Collider;
                        physicsColliders[entity] = physicsCollider;
                        
                        this.physicsCustomTags[entity] = physicsCustomTags;
                    }
                    else
                        results[childIndex++] = child;
                }

                if (results.IsCreated)
                {
                    entity = entityArray[entityIndex++];

                    translation.Value = float3.zero;
                    translations[entity] = translation;

                    rotation.Value = quaternion.identity;
                    rotations[entity] = rotation;

                    physicsCollider.Value = CompoundCollider.Create(results);
                    physicsColliders[entity] = physicsCollider;

                    this.physicsCustomTags[entity] = physicsCustomTags;
                    
                    results.Dispose();
                }
            }
        }

        [DisableAutoCreation]
        private partial class System : SystemBase
        {
            private EntityArchetype __entityArchetype;

            public NativeArray<Entity> Create(
                Allocator allocator, 
                in NativeArray<CompoundCollider.ColliderBlobInstance> children)
            {
                int i, numEntities = 0, numChildren = children.Length;
                uint numColliderKeyBits = (uint)Math.GetHighestBit(numChildren);
                for (i = 0; i < numChildren; i++)
                {
                    if (children[i].Collider.Value.TotalNumColliderKeyBits + numColliderKeyBits > 32)
                        ++numEntities;
                }

                if (numEntities < numChildren)
                    ++numEntities;

                var entityArray = EntityManager.CreateEntity(__entityArchetype, numEntities, allocator);

                CreateJob job;
                job.numColliderKeyBits = numColliderKeyBits;
                job.entityArray = entityArray;
                job.children = children;
                job.translations = GetComponentLookup<Translation>();
                job.rotations = GetComponentLookup<Rotation>();
                job.physicsColliders = GetComponentLookup<PhysicsCollider>();
                job.physicsCustomTags = GetComponentLookup<PhysicsCustomTags>();
                job.RunByRef();

                return entityArray;
            }

            public NativeArray<Entity> Deserialize(ref NativeBuffer.Reader reader, Allocator allocator, CompoundColliderDeserializeType type)
            {
                int i;

                var children = new NativeList<CompoundCollider.ColliderBlobInstance>(Allocator.TempJob);
                switch (type)
                {
                    case CompoundColliderDeserializeType.Collider:

                        var colliders = new NativeList<BlobAssetReference<Collider>>(Allocator.Temp);

                        reader.Deserialize(ref colliders);

                        int numColliders = colliders.Length;
                        children.ResizeUninitialized(numColliders);

                        CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                        colliderBlobInstance.CompoundFromChild = RigidTransform.identity;

                        for (i = 0; i < numColliders; ++i)
                        {
                            colliderBlobInstance.Collider = colliders[i];

                            children[i] = colliderBlobInstance;
                        }

                        colliders.Dispose();

                        break;

                    default:
                        reader.Deserialize(ref children);
                        break;
                }

                int numChildren = children.Length;
                uint numColliderKeyBits = (uint)Math.GetHighestBit(numChildren), colliderIndex;
                var sizes = DeserializeDeserializers(ref reader, out var colliderKeys);
                int numSizes = sizes.Length;
                var colliderCounts = new NativeArray<int>(numChildren, Allocator.Temp, NativeArrayOptions.ClearMemory);
                if (colliderKeys.IsCreated)
                {
                    foreach (var colliderKey in colliderKeys)
                    {
                        if (colliderKey.PopSubKey(numColliderKeyBits, out colliderIndex))
                            ++colliderCounts[(int)colliderIndex];
                    }
                }
                else
                {
                    for (i = 0; i < numSizes; ++i)
                        colliderCounts[i] = 1;
                }
                
                int numSerializerEntites = 0, numColliderEntities = 0;
                for (i = 0; i < numChildren; i++)
                {
                    if (colliderCounts[i] > 0)
                        ++numSerializerEntites;
                    else if(children[i].Collider.Value.TotalNumColliderKeyBits + numColliderKeyBits > 32)
                        ++numColliderEntities;
                }

                int numEntities = numSerializerEntites + numColliderEntities;
                if (numEntities < numChildren)
                    ++numEntities;

                var entityManager = EntityManager;
                var entityArray = entityManager.CreateEntity(__entityArchetype, numEntities, allocator);

                if (numSizes > 0)
                {
                    PhysicsCustomTags physicsCustomTags;
                    physicsCustomTags.Value = byte.MaxValue;

                    int entityCount = numEntities, previousIndex = -1, colliderCount;
                    Entity entity;
                    ColliderKey colliderKey;
                    Translation translation;
                    Rotation rotation;
                    PhysicsCollider physicsCollider;
                    CompoundCollider.ColliderBlobInstance child;
                    UnsafeBlock.Reader blockReader;
                    var colliderIndices =
                        new NativeArray<int>(numChildren, Allocator.Temp, NativeArrayOptions.ClearMemory);
                    var assigner = new EntityComponentAssigner(Allocator.TempJob);
                    for (i = 0; i < entityCount; ++i)
                    {
                        child = children[i];
                        
                        colliderCount = colliderCounts[i];
                        if (colliderCount < 1)
                        {
                            children[i] = children[--entityCount];
                            children[entityCount] = child;

                            colliderIndices[entityCount] = previousIndex < i ? i : colliderIndices[i];
                            colliderIndices[i] = entityCount;

                            previousIndex = i--;
                            
                            continue;
                        }
                        
                        colliderIndices[i] = i;

                        entity = entityArray[i];

                        translation.Value = child.CompoundFromChild.pos;
                        assigner.SetComponentData(entity, translation);

                        rotation.Value = child.CompoundFromChild.rot;
                        assigner.SetComponentData(entity, rotation);

                        physicsCollider.Value = child.Collider;
                        assigner.SetComponentData(entity, physicsCollider);

                        assigner.SetComponentData(entity, physicsCustomTags);
                        
                        previousIndex = i;
                    }
                    
                    UnityEngine.Assertions.Assert.AreEqual(numSerializerEntites, entityCount);
                    
                    for (i = 0; i < numSizes; ++i)
                    {
                        if (colliderKeys.IsCreated)
                        {
                            colliderKey = colliderKeys[i];
                            if (!colliderKey.PopSubKey(numColliderKeyBits, out colliderIndex))
                                continue;
                        }
                        else
                        {
                            colliderIndex = (uint)i;
                            
                            colliderKey = ColliderKey.Empty;
                        }

                        blockReader = reader.ReadBlock(sizes[i]).reader;
                        blockReader.DeserializeStream(
                            ref entityManager, 
                            ref assigner, entityArray[colliderIndices[(int)colliderIndex]], 
                            colliderKey.Equals(ColliderKey.Empty) ? 
                                default : colliderKey.ToNativeArray().Reinterpret<byte>(UnsafeUtility.SizeOf<ColliderKey>()));
                    }

                    colliderIndices.Dispose();
                    
                    assigner.Playback(ref this.GetState());

                    CompleteDependency();

                    assigner.Dispose();
                }

                colliderCounts.Dispose();

                CreateJob job;
                job.numColliderKeyBits = numColliderKeyBits;
                job.entityArray = entityArray.GetSubArray(numSerializerEntites, numEntities - numSerializerEntites);
                job.children = children.AsArray().GetSubArray(numSerializerEntites, numChildren - numSerializerEntites);
                job.translations = GetComponentLookup<Translation>();
                job.rotations = GetComponentLookup<Rotation>();
                job.physicsColliders = GetComponentLookup<PhysicsCollider>();
                job.physicsCustomTags = GetComponentLookup<PhysicsCustomTags>();
                job.RunByRef();

                children.Dispose();

                return entityArray;
            }

            public NativeArray<Entity> Deserialize(
                ref NativeBuffer.Reader reader,
                ref NativeList<BlobAssetReference<Collider>> colliders, 
                Allocator allocator)
            {
                reader.Deserialize(ref colliders);

                int numColliders = colliders.Length;
                var entityManager = EntityManager;
                var entityArray = entityManager.CreateEntity(__entityArchetype, numColliders, allocator);
           
                PhysicsCustomTags physicsCustomTags;
                physicsCustomTags.Value = byte.MaxValue;
                
                Entity entity;
                ColliderKey colliderKey;
                Translation translation;
                Rotation rotation;
                PhysicsCollider physicsCollider;
                UnsafeBlock.Reader blockReader;
                var assigner = new EntityComponentAssigner(Allocator.TempJob);
                for (int i = 0; i < numColliders; ++i)
                {
                    entity = entityArray[i];

#if UNITY_EDITOR
                    entityManager.SetName(entity, $"CompoundCollider {i} : {entity.Index}");
#endif

                    translation.Value = float3.zero;
                    assigner.SetComponentData(entity, translation);

                    rotation.Value = quaternion.identity;
                    assigner.SetComponentData(entity, rotation);

                    physicsCollider.Value = colliders[i];
                    assigner.SetComponentData(entity, physicsCollider);
                    
                    assigner.SetComponentData(entity, physicsCustomTags);
                }
     
                /*var colliderCounts =
                    new NativeArray<int>(numColliders, Allocator.Temp, NativeArrayOptions.ClearMemory);*/
                var sizes = DeserializeDeserializers(ref reader, out var colliderKeys);
                int numSizes = sizes.Length;
                uint numColliderKeyBits = (uint)Math.GetHighestBit(numColliders), colliderIndex;
                for (int i = 0; i < numSizes; ++i)
                {
                    if (colliderKeys.IsCreated)
                    {
                        colliderKey = colliderKeys[i];
                        if (!colliderKey.PopSubKey(numColliderKeyBits, out colliderIndex))
                            continue;
                    }
                    else
                    {
                        colliderIndex = (uint)i;
                        
                        colliderKey = ColliderKey.Empty;
                    }

                    blockReader = reader.ReadBlock(sizes[i]).reader;
                    blockReader.DeserializeStream(
                        ref entityManager, 
                        ref assigner, entityArray[(int)colliderIndex], 
                        colliderKey.Equals(ColliderKey.Empty) ? 
                            default : colliderKey.ToNativeArray().Reinterpret<byte>(UnsafeUtility.SizeOf<ColliderKey>()));
                }
                
                assigner.Playback(ref this.GetState());

                CompleteDependency();

                assigner.Dispose();

                //colliderCounts.Dispose();
                colliders.Dispose();
                
                return entityArray;
            }

            protected override void OnCreate()
            {
                base.OnCreate();

                __entityArchetype = EntityManager.CreateArchetype(
                    ComponentType.ReadOnly<Translation>(),
                    ComponentType.ReadOnly<Rotation>(),
                    ComponentType.ReadOnly<PhysicsCollider>(),
                    ComponentType.ReadOnly<PhysicsCustomTags>());
            }

            protected override void OnUpdate()
            {
                throw new global::System.NotImplementedException();
            }
        }

        public static BlobAssetReference<Collider> Create(this in NativeArray<CompoundCollider.ColliderBlobInstance> children)
        {
            using (var result = new NativeArray<BlobAssetReference<Collider>>(1, Allocator.TempJob))
            {
                CreateRightNowJob createRightNowJob;
                createRightNowJob.children = children;
                createRightNowJob.result = result;
                createRightNowJob.RunByRef();

                return result[0];
            }
        }

        public static NativeArray<Entity> Create(this in NativeArray<CompoundCollider.ColliderBlobInstance> children, Allocator allocator, World world)
        {
            return world.GetOrCreateSystemManaged<System>().Create(allocator, children);
        }

        public static NativeArray<Entity> DeserializeCompoundColliders(
            this ref NativeBuffer.Reader reader, 
            Allocator allocator, 
            CompoundColliderDeserializeType type, 
            World world)
        {
            return world.GetOrCreateSystemManaged<System>().Deserialize(ref reader, allocator, type);
        }

        public static NativeArray<Entity> DeserializeCompoundColliders(
            this ref NativeBuffer.Reader reader,
            ref NativeList<BlobAssetReference<Collider>> colliders, 
            Allocator allocator,
            World world)
        {
            return world.GetOrCreateSystemManaged<System>().Deserialize(ref reader, ref colliders, allocator);
        }

        public static NativeArray<int> DeserializeDeserializers<T>(
            this ref T reader, 
            //ref NativeArray<int> colliderCounts, 
            out NativeArray<ColliderKey> colliderKeys) where T : struct, INativeReader
        {
            int numSizes = reader.isVail ? reader.Read<int>() : 0;
            if (numSizes < 1)
            {
                colliderKeys = default;
                
                return default;
            }

            var sizes = reader.ReadArray<int>(numSizes);

            int offset = 0;
            foreach (var size in sizes)
                offset += size;

            offset += reader.position;
            if (offset < reader.length)
            {
                var position = reader.position;
                reader.position = offset;
                colliderKeys = reader.ReadArray<ColliderKey>(numSizes);
                reader.position = position;
            }
            else
                colliderKeys = default;

            /*if (colliderKeys.IsCreated && colliderCounts.IsCreated)
            {
                uint numSubKeyBits = (uint)Math.GetHighestBit(colliderCounts.Length), colliderIndex;
                for (int i = 0; i < numSizes; ++i)
                {
                    if (colliderKeys[i].PopSubKey(numSubKeyBits, out colliderIndex))
                        colliderCounts.Increment((int)colliderIndex);
                }
            }*/

            return sizes;
        }
        
        public static void SerializeSerializers(
            this ref NativeBuffer.Writer writer, 
            ICollection<IEntityDataStreamSerializer> serializers)
        {
            int numSerializers = serializers == null ? 0 : serializers.Count;
            if (numSerializers > 0)
            {
                writer.Write(numSerializers);

                var sizes = writer.WriteBlock(sizeof(int) * numSerializers, false).writer;
                int source = writer.position, destination;
                foreach (var serializer in serializers)
                {
                    serializer.Serialize(ref writer);

                    destination = writer.position;
                    sizes.Write(destination - source);
                    source = destination;
                }
            }
        }
        
        public static void SerializeSerializers(
            this ref NativeBuffer.Writer writer, 
            ICollection<KeyValuePair<ColliderKey, IEntityDataStreamSerializer>> serializers)
        {
            int numSerializers = serializers == null ? 0 : serializers.Count;
            if (numSerializers > 0)
            {
                var serializersArray = new KeyValuePair<ColliderKey, IEntityDataStreamSerializer>[numSerializers];
                serializers.CopyTo(serializersArray, 0);
                Array.Sort(serializersArray, new Comparer());
                
                writer.Write(numSerializers);

                //int[] counts = null;
                var sizes = writer.WriteBlock(sizeof(int) * numSerializers, false).writer;
                //ColliderKey key;
                //uint numSubKeyBits = (uint)Math.GetHighestBit(colliderCount), index;
                int source = writer.position, destination;
                foreach (var serializer in serializersArray)
                {
                    /*key = serializer.Key;
                    if (key.PopSubKey(numSubKeyBits, out index))
                    {
                        if(counts == null || counts.Length <= index)
                            Array.Resize(ref counts, (int)index + 1);

                        ++counts[index];
                    }*/
                    
                    serializer.Value.Serialize(ref writer);
                    //writer.Write(key);

                    destination = writer.position;
                    sizes.Write(destination - source);
                    source = destination;
                }
                
                foreach (var serializer in serializersArray)
                    writer.Write(serializer.Key);
                
                /*if(counts != null)
                    writer.Write(counts);*/
            }
        }

        public static void SerializeColliderBlobInstances(
            this ref NativeBuffer.Writer writer, 
            in NativeArray<CompoundCollider.ColliderBlobInstance> source, 
            IDictionary<int, IEntityDataStreamSerializer> serializers)
        {
            int numSerializers = serializers == null ? 0 : serializers.Count;
            if (numSerializers > 0)
            {
                int numChildren = source.Length, numColliders = 0, i, j;
                var destination = new NativeArray<CompoundCollider.ColliderBlobInstance>(numChildren, Allocator.Temp);
                var keys = serializers.Keys;
                for (i = 0; i < numChildren; ++i)
                {
                    j = 0;
                    foreach (int key in keys)
                    {
                        if (key == i)
                            break;

                        ++j;
                    }

                    if (j < numSerializers)
                        destination[j] = source[i];
                    else
                        destination[numSerializers + numColliders++] = source[i];
                }

                writer.SerializeColliderBlobInstances(destination);

                SerializeSerializers(ref writer, serializers.Values);

                destination.Dispose();
            }
            else
                writer.SerializeColliderBlobInstances(source);

        }

        public static void SerializeColliderBlobInstances(
            this ref NativeBuffer.Writer writer, 
            in NativeArray<CompoundCollider.ColliderBlobInstance> colliderBlobInstances, 
            ICollection<KeyValuePair<ColliderKey, IEntityDataStreamSerializer>> serializers)
        {
            int numSerializers = serializers == null ? 0 : serializers.Count;
            if (numSerializers > 0)
            {
                writer.SerializeColliderBlobInstances(colliderBlobInstances);

                SerializeSerializers(ref writer, serializers);
            }
            else
                writer.SerializeColliderBlobInstances(colliderBlobInstances);
        }

        public static void SerializeColliders(
            this ref NativeBuffer.Writer writer,
            in NativeArray<BlobAssetReference<Collider>> source,
            IDictionary<int, IEntityDataStreamSerializer> serializers)
        {
            int numSerializers = serializers == null ? 0 : serializers.Count;
            if (numSerializers > 0)
            {
                int numChildren = source.Length, numColliders = 0, i, j;
                var destination = new NativeArray<BlobAssetReference<Collider>>(numChildren, Allocator.Temp);
                var keys = serializers.Keys;
                for (i = 0; i < numChildren; ++i)
                {
                    j = 0;
                    foreach (int key in keys)
                    {
                        if (key == i)
                            break;

                        ++j;
                    }

                    if (j < numSerializers)
                        destination[j] = source[i];
                    else
                        destination[numSerializers + numColliders++] = source[i];
                }

                writer.SerializeColliders(destination);

                SerializeSerializers(ref writer, serializers.Values);

                destination.Dispose();
            }
            else
                writer.SerializeColliders(source);
        }
        
        public static void SerializeColliders(
            this ref NativeBuffer.Writer writer,
            in NativeArray<BlobAssetReference<Collider>> colliders,
            ICollection<KeyValuePair<ColliderKey, IEntityDataStreamSerializer>> serializers)
        {
            int numSerializers = serializers == null ? 0 : serializers.Count;
            if (numSerializers > 0)
            {
                writer.SerializeColliders(colliders);

                SerializeSerializers(ref writer, serializers);
            }
            else
                writer.SerializeColliders(colliders);
        }
    }
}