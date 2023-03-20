using System;
using Unity.Jobs;
using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using Unity.Physics;
using Unity.Physics.Systems;

namespace ZG
{
    [Serializable, InternalBufferCapacity(1)]
    public struct PhysicsTriggerEvent : IBufferElementData
    {
        public Entity entity;

        public int bodyIndexA;
        public int bodyIndexB;

        public ColliderKey colliderKeyB;
        public ColliderKey colliderKeyA;
    }

    [AlwaysUpdateSystem, UpdateInGroup(typeof(FixedStepSimulationSystemGroup)), UpdateBefore(typeof(EndFramePhysicsSystem)), UpdateAfter(typeof(StepPhysicsWorld))]
    public partial class PhysicsTriggerEventSystem : SystemBase
    {
        [BurstCompile]
        private struct BuildMap : ITriggerEventsJob
        {
            public BufferLookup<PhysicsTriggerEvent> triggerEvents;

            public void Execute(TriggerEvent triggerEvent)
            {
                if (triggerEvents.HasBuffer(triggerEvent.EntityA))
                {
                    PhysicsTriggerEvent physicsTriggerEvent;
                    physicsTriggerEvent.entity = triggerEvent.EntityB;
                    physicsTriggerEvent.bodyIndexA = triggerEvent.BodyIndexA;
                    physicsTriggerEvent.bodyIndexB = triggerEvent.BodyIndexB;
                    physicsTriggerEvent.colliderKeyA = triggerEvent.ColliderKeyA;
                    physicsTriggerEvent.colliderKeyB = triggerEvent.ColliderKeyB;
                    triggerEvents[triggerEvent.EntityA].Add(physicsTriggerEvent);
                }

                if (triggerEvents.HasBuffer(triggerEvent.EntityB))
                {
                    PhysicsTriggerEvent physicsTriggerEvent;
                    physicsTriggerEvent.entity = triggerEvent.EntityA;
                    physicsTriggerEvent.bodyIndexA = triggerEvent.BodyIndexB;
                    physicsTriggerEvent.bodyIndexB = triggerEvent.BodyIndexA;
                    physicsTriggerEvent.colliderKeyA = triggerEvent.ColliderKeyB;
                    physicsTriggerEvent.colliderKeyB = triggerEvent.ColliderKeyA;
                    triggerEvents[triggerEvent.EntityB].Add(physicsTriggerEvent);
                }
            }
        }

        private BuildPhysicsWorld __buildPhysicsWorld;
        private StepPhysicsWorld __stepPhysicsWorld;
        private EndFramePhysicsSystem __endFramePhysicsSystem;

        protected override void OnCreate()
        {
            base.OnCreate();

            World world = World;

            __buildPhysicsWorld = world.GetOrCreateSystemManaged<BuildPhysicsWorld>();
            __stepPhysicsWorld = world.GetOrCreateSystemManaged<StepPhysicsWorld>();
            __endFramePhysicsSystem = world.GetOrCreateSystemManaged<EndFramePhysicsSystem>();
        }

        protected override void OnUpdate()
        {
            Entities.ForEach((ref DynamicBuffer<PhysicsTriggerEvent> triggerEvents) => triggerEvents.Clear()).ScheduleParallel();

            if (__stepPhysicsWorld.ShouldRunSystem())
            {
                BuildMap buildMap;
                buildMap.triggerEvents = GetBufferLookup<PhysicsTriggerEvent>();

                JobHandle jobHandle = buildMap.Schedule(
                    __stepPhysicsWorld.Simulation,
                    ref __buildPhysicsWorld.PhysicsWorld,
                    JobHandle.CombineDependencies(
                        Dependency,
                        __stepPhysicsWorld.FinalSimulationJobHandle));

                __endFramePhysicsSystem.AddInputDependency(jobHandle);

                Dependency = jobHandle;
            }
        }
    }
}