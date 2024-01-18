using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;
using Unity.Jobs;
using Unity.Transforms;
using Unity.Physics;
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
            public int numColliderKeyBits;

            [ReadOnly]
            public NativeArray<Entity> entityArray;

            [ReadOnly]
            public NativeArray<CompoundCollider.ColliderBlobInstance> children;

            public ComponentLookup<Translation> translations;
            public ComponentLookup<Rotation> rotations;
            public ComponentLookup<PhysicsCollider> physicsColliders;

            public void Execute()
            {
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
                int i, numEntities = 0, numChildren = children.Length, numColliderKeyBits = Math.GetHighestBit(numChildren);
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
                var colliderCounts =
                    new NativeArray<int>(numChildren, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                var sizes = DeserializeDeserializers(ref reader, ref colliderCounts);
                
                int numColliderKeyBits = Math.GetHighestBit(numChildren), 
                    numSerializerEntites = 0, numColliderEntities = 0;
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

                int numSizes = sizes.Length;
                if (numSizes > 0)
                {
                    int entityCount = numEntities, entityIndex = 0, sizeIndex = 0, colliderCount, j;
                    Entity entity;
                    Translation translation;
                    Rotation rotation;
                    PhysicsCollider physicsCollider;
                    CompoundCollider.ColliderBlobInstance child;
                    UnsafeBlock.Reader blockReader;
                    var assigner = new EntityComponentAssigner(Allocator.TempJob);
                    for (i = 0; i < numChildren; ++i)
                    {
                        child = children[i];
                        
                        colliderCount = colliderCounts[i];
                        if (colliderCount < 1)
                        {
                            children[i] = children[--entityCount];
                            children[entityCount] = child;
                            
                            --i;
                            
                            continue;
                        }

                        entity = entityArray[entityIndex++];

                        translation.Value = child.CompoundFromChild.pos;
                        assigner.SetComponentData(entity, translation);

                        rotation.Value = child.CompoundFromChild.rot;
                        assigner.SetComponentData(entity, rotation);

                        physicsCollider.Value = child.Collider;
                        assigner.SetComponentData(entity, physicsCollider);

                        for (j = 0; j < colliderCount; ++j)
                        {
                            if (sizeIndex >= numSizes)
                                break;
                            
                            blockReader = reader.ReadBlock(sizes[sizeIndex++]).reader;
                            blockReader.DeserializeStream(ref entityManager, ref assigner, entity);
                        }

                        if (entityIndex == numSerializerEntites)
                            break;
                    }
                    
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
                job.RunByRef();

                children.Dispose();

                return entityArray;
            }

            public NativeArray<Entity> Deserialize(
                ref NativeBuffer.Reader reader,
                Allocator allocator)
            {
                var colliders = new NativeList<BlobAssetReference<Collider>>(Allocator.Temp);

                reader.Deserialize(ref colliders);

                int numColliders = colliders.Length;
                var entityManager = EntityManager;
                var entityArray = entityManager.CreateEntity(__entityArchetype, numColliders, allocator);
                
                var colliderCounts =
                    new NativeArray<int>(numColliders, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                var sizes = DeserializeDeserializers(ref reader, ref colliderCounts);
                int numSizes = sizes.Length;
                if (numSizes > 0)
                {
                    int sizeIndex = 0, colliderCount, j;
                    Entity entity;
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
                        
                        colliderCount = colliderCounts[i];
                        for (j = 0; j < colliderCount; ++j)
                        {
                            if (sizeIndex >= numSizes)
                                break;

                            blockReader = reader.ReadBlock(sizes[sizeIndex++]).reader;
                            blockReader.DeserializeStream(ref entityManager, ref assigner, entity);
                        }
                    }

                    assigner.Playback(ref this.GetState());

                    CompleteDependency();

                    assigner.Dispose();
                }

                colliderCounts.Dispose();
                colliders.Dispose();
                
                return entityArray;
            }

            protected override void OnCreate()
            {
                base.OnCreate();

                __entityArchetype = EntityManager.CreateArchetype(
                    ComponentType.ReadOnly<Translation>(),
                    ComponentType.ReadOnly<Rotation>(),
                    ComponentType.ReadOnly<PhysicsCollider>());
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
            Allocator allocator,
            World world)
        {
            return world.GetOrCreateSystemManaged<System>().Deserialize(ref reader, allocator);
        }

        public static NativeArray<int> DeserializeDeserializers(this ref NativeBuffer.Reader reader, ref NativeArray<int> colliderCounts)
        {
            int numSizes = reader.isVail ? reader.Read<int>() : 0;
            if (numSizes < 1)
                return default;

            var sizes = reader.ReadArray<int>(numSizes);

            int offset = 0;
            foreach (var size in sizes)
                offset += size;

            offset += reader.position;
            if (offset < reader.length)
            {
                var position = reader.position;
                reader.position = offset;
                reader.ReadArray<int>(colliderCounts.Length).CopyTo(colliderCounts);
                reader.position = position;
            }
            else
            {
                int numColliderCounts = colliderCounts.Length;
                for (int i = 0; i < numColliderCounts; ++i)
                    colliderCounts[i] = i < numSizes ? 1 : 0;
            }

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
            int colliderCount, 
            ICollection<KeyValuePair<ColliderKey, IEntityDataStreamSerializer>> serializers)
        {
            int numSerializers = serializers == null ? 0 : serializers.Count;
            if (numSerializers > 0)
            {
                var serializersArray = new KeyValuePair<ColliderKey, IEntityDataStreamSerializer>[numSerializers];
                serializers.CopyTo(serializersArray, 0);
                Array.Sort(serializersArray, new Comparer());
                
                writer.Write(numSerializers);

                int[] counts = null;
                var sizes = writer.WriteBlock(sizeof(int) * numSerializers, false).writer;
                ColliderKey key;
                uint numSubKeyBits = (uint)Math.GetHighestBit(colliderCount), index;
                int source = writer.position, destination;
                foreach (var serializer in serializersArray)
                {
                    key = serializer.Key;
                    if (key.PopSubKey(numSubKeyBits, out index))
                    {
                        if(counts == null || counts.Length <= index)
                            Array.Resize(ref counts, (int)index + 1);

                        ++counts[index];
                    }
                    
                    serializer.Value.Serialize(ref writer);
                    writer.Write(key);

                    destination = writer.position;
                    sizes.Write(destination - source);
                    source = destination;
                }
                
                if(counts != null)
                    writer.Write(counts);
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

                SerializeSerializers(ref writer, colliderBlobInstances.Length, serializers);
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

                SerializeSerializers(ref writer, colliders.Length, serializers);
            }
            else
                writer.SerializeColliders(colliders);
        }
    }
}