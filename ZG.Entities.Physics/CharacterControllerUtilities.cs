using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Extensions;
using UnityEngine.Assertions;

namespace ZG
{
    // Stores the impulse to be applied by the character controller body
    public struct DeferredCharacterControllerImpulse
    {
        public Entity Entity;
        public float3 Impulse;
        public float3 Point;
    }

    public static class CharacterControllerUtilities
    {
        const float k_SimplexSolverEpsilon = 0.0001f;
        const float k_SimplexSolverEpsilonSq = k_SimplexSolverEpsilon * k_SimplexSolverEpsilon;

        const int k_DefaultQueryHitsCapacity = 8;
        const int k_DefaultConstraintsCapacity = 2 * k_DefaultQueryHitsCapacity;

        public enum CharacterSupportState : byte
        {
            Unsupported = 0,
            Sliding,
            Supported
        }

        public struct CharacterControllerStepInput
        {
            public NativeSlice<DistanceHit> distanceHits;
            public PhysicsWorldContainer physicsWorld;
            public float3 up;
            public float deltaTime;
            public float maxMovementSpeed;
            public float skinWidth;
            public float contactTolerance;
            public float maxSlope;
            public int rigidbodyIndex;
        }

        public struct DistanceHitComparer : System.Collections.Generic.IComparer<DistanceHit>
        {
            public static uint GetHash(in DistanceHit distanceHit)
            {
                return math.hash(distanceHit.Position) ^ math.hash(distanceHit.SurfaceNormal);
            }

            public int Compare(DistanceHit x, DistanceHit y)
            {
                int result = x.Distance.CompareTo(y.Distance);
                if (result != 0)
                    return result;

                result = x.ColliderKey.Value.CompareTo(y.ColliderKey.Value);
                if (result != 0)
                    return result;

                return GetHash(x).CompareTo(GetHash(y));
            }
        }

        public struct ColliderCastHitComparer : System.Collections.Generic.IComparer<ColliderCastHit>
        {
            public static uint GetHash(in ColliderCastHit distanceHit)
            {
                return math.hash(distanceHit.Position) ^ math.hash(distanceHit.SurfaceNormal);
            }

            public int Compare(ColliderCastHit x, ColliderCastHit y)
            {
                int result = x.Fraction.CompareTo(y.Fraction);
                if (result != 0)
                    return result;

                result = x.ColliderKey.Value.CompareTo(y.ColliderKey.Value);
                if (result != 0)
                    return result;

                return GetHash(x).CompareTo(GetHash(y));
            }
        }

        /*public struct CharacterControllerAllHitsCollector<T> : ICollector<T> where T : unmanaged, IQueryResult
        {
            private int m_selfRBIndex;

            public bool EarlyOutOnFirstHit => false;
            public float MaxFraction { get; }
            public int NumHits => AllHits.Length;

            public NativeList<T> AllHits;
            
            public CharacterControllerAllHitsCollector(int rbIndex, float maxFraction, ref NativeList<T> allHits)
            {
                MaxFraction = maxFraction;
                AllHits = allHits;
                m_selfRBIndex = rbIndex;
            }

            #region ICollector

            public bool AddHit(T hit)
            {
                Assert.IsTrue(hit.Fraction < MaxFraction);

                if (hit.RigidBodyIndex == m_selfRBIndex)
                    return false;

                AllHits.Add(hit);

                return true;
            }

            #endregion

        }*/

        // A collector which stores only the closest hit different from itself, the triggers, and predefined list of values it hit.
        public struct CharacterControllerClosestHitCollector<T> : ICollector<T> where T : struct, IQueryResult
        {
            //private uint __belongsTo;

            private int __numDynamicBodies;

            private int __selfRBIndex;

            private NativeArray<RigidBody> __rigidbodies;

            private NativeList<SurfaceConstraintInfo> __predefinedConstraints;

            public bool EarlyOutOnFirstHit => false;
            public float MaxFraction { get; private set; }
            public int NumHits { get; private set; }
            
            public T closestHit { get; private set; }

            public CharacterControllerClosestHitCollector(
                //uint belongsTo,
                int numDynamicBodies,
                int rbIndex,
                float maxFraction,
                in NativeArray<RigidBody> rigidbodies, 
                in NativeList<SurfaceConstraintInfo> predefinedConstraints)
            {
                //__belongsTo = belongsTo;
                __numDynamicBodies = numDynamicBodies;
                __selfRBIndex = rbIndex;
                __rigidbodies = rigidbodies;
                __predefinedConstraints = predefinedConstraints;

                NumHits = 0;
                MaxFraction = maxFraction;
                closestHit = default;
            }

            #region ICollector

            public unsafe bool AddHit(T hit)
            {
                Assert.IsTrue(hit.Fraction <= MaxFraction);

                // Check self hits and trigger hits
                if (hit.RigidBodyIndex == __selfRBIndex)
                    return false;

                if (hit.RigidBodyIndex < __numDynamicBodies)
                    return false;

                /*if ((__rigidbodies[hit.RigidBodyIndex].Collider.Value.GetLeafFilter(hit.ColliderKey).BelongsTo & __belongsTo) == 0)
                    return false;*/

                // Check predefined hits
                int length = __predefinedConstraints.Length;
                for (int i = 0; i < length; i++)
                {
                    SurfaceConstraintInfo constraint = __predefinedConstraints[i];
                    if (constraint.RigidBodyIndex == hit.RigidBodyIndex &&
                        constraint.ColliderKey.Equals(hit.ColliderKey))
                    {
                        // Hit was already defined, skip it
                        return false;
                    }
                }

                // Finally, accept the hit

                if (!PhysicsUtility.IsCloserHit(closestHit, hit, NumHits))
                    return false;

                closestHit = hit;
                MaxFraction = hit.Fraction + math.FLT_MIN_NORMAL;
                NumHits = 1;

                return true;
            }

            #endregion

        }

        public static unsafe CharacterSupportState CheckSupport(
            in CharacterControllerStepInput stepInput, 
            in float3 position, 
            //in RigidTransform transform, 
            //ref DynamicBuffer<SurfaceConstraintInfo> constraints, 
            //ref NativeSlice<DistanceHit> distanceHits, 
            in float3 velocity, 
            out float3 surfaceNormal, 
            out float3 surfaceVelocity)
        {
            surfaceNormal = float3.zero;
            surfaceVelocity = float3.zero;

            // Up direction must be normalized
            //Assert.IsTrue(Math.IsNormalized(stepInput.Up));

            // Query the world
            /*NativeList<ColliderCastHit> castHits = new NativeList<ColliderCastHit>(k_DefaultQueryHitsCapacity, Allocator.Temp);
            SelfFilteringAllHitsCollector<ColliderCastHit> castHitsCollector = new SelfFilteringAllHitsCollector<ColliderCastHit>(
                stepInput.RigidBodyIndex, 1.0f, ref castHits);
            var maxDisplacement = -stepInput.ContactTolerance * stepInput.Up;
            {
                ColliderCastInput input = new ColliderCastInput()
                {
                    Collider = stepInput.Collider,
                    Orientation = transform.rot,
                    Start = transform.pos,
                    End = transform.pos + maxDisplacement
                };
                stepInput.World.CastCollider(input, ref castHitsCollector);
            }

            // If no hits, proclaim unsupported state
            if (castHitsCollector.NumHits == 0)
                return CharacterSupportState.Unsupported;
            
            // Iterate over distance hits and create constraints from them
            float maxDisplacementLength = math.length(maxDisplacement);*/
            int numDistanceHits = stepInput.distanceHits.Length;
            if (numDistanceHits < 1)
                return CharacterSupportState.Unsupported;

            var constraints = new NativeList<SurfaceConstraintInfo>(k_DefaultConstraintsCapacity, Allocator.Temp);
            for (int i = 0; i < numDistanceHits; i++)
            {
                var hit = stepInput.distanceHits[i];
                CreateConstraint(stepInput.physicsWorld, stepInput.up,
                    hit.RigidBodyIndex, hit.ColliderKey, hit.Position, hit.SurfaceNormal, hit.Distance,
                    stepInput.skinWidth, stepInput.maxSlope, ref constraints);
            }

            // Velocity for support checking
            float3 initialVelocity = velocity; //*maxDisplacement*/-stepInput.ContactTolerance / stepInput.DeltaTime * stepInput.Up;

            // Solve downwards (don't use min delta time, try to solve full step)
            float3 outVelocity = initialVelocity;
            float3 outPosition = position;//transform.pos;
            SimplexSolver.Solve(
                stepInput.deltaTime, 
                stepInput.deltaTime, 
                stepInput.maxMovementSpeed, 
                stepInput.up,
                ref constraints, 
                ref outPosition, 
                ref outVelocity, 
                out float integratedTime, 
                false);

            // Get info on surface
            int numSupportingPlanes = 0;
            {
                for (int j = 0; j < constraints.Length; j++)
                {
                    var constraint = constraints[j];
                    if (constraint.Touched && !constraint.IsTooSteep)
                    {
                        numSupportingPlanes++;
                        surfaceNormal += constraint.Plane.Normal;
                        surfaceVelocity += constraint.Velocity;
                    }
                }

                if (numSupportingPlanes > 0)
                {
                    float invNumSupportingPlanes = 1.0f / numSupportingPlanes;
                    surfaceNormal *= invNumSupportingPlanes;
                    surfaceVelocity *= invNumSupportingPlanes;

                    surfaceNormal = math.normalize(surfaceNormal);
                }
            }
            constraints.Dispose();

            // Check support state
            {
                if (math.lengthsq(initialVelocity - outVelocity) < k_SimplexSolverEpsilonSq)
                {
                    // If velocity hasn't changed significantly, declare unsupported state
                    return CharacterSupportState.Unsupported;
                }
                else if (numSupportingPlanes > 0 && math.lengthsq(outVelocity) < k_SimplexSolverEpsilonSq)
                {
                    // If velocity is very small, declare supported state
                    return CharacterSupportState.Supported;
                }
                else
                {
                    // Check if sliding
                    outVelocity = math.normalize(outVelocity);
                    float slopeAngleSin = math.max(0.0f, math.dot(outVelocity, -stepInput.up));
                    float slopeAngleCosSq = 1 - slopeAngleSin * slopeAngleSin;
                    if (slopeAngleCosSq <= stepInput.maxSlope * stepInput.maxSlope)
                    {
                        return CharacterSupportState.Sliding;
                    }
                    else if (numSupportingPlanes > 0)
                    {
                        return CharacterSupportState.Supported;
                    }
                    else
                    {
                        // If numSupportingPlanes is 0, surface normal is invalid, so state is unsupported
                        return CharacterSupportState.Unsupported;
                    }
                }
            }
        }

        public static unsafe void CollideAndIntegrate(
            bool affectBodies,
            bool isUnstopping,
            //uint belongsTo, 
            int maxIterations, 
            Collider* Collider, 
            in CharacterControllerStepInput stepInput,
            in quaternion orientation,
            ref float3 position,
            ref float3 linearVelocity,
            ref NativeQueue<DeferredCharacterControllerImpulse>.ParallelWriter deferredImpulseWriter,
            float3 gravity = default, 
            float characterMass = 1.0f,
            float tau = 0.4f,
            float damping = 0.9f)
        {
            var collisionWorld = stepInput.physicsWorld.collisionWorld;
            int numDynamicBodies = collisionWorld.NumDynamicBodies;
            float remainingTime = stepInput.deltaTime;

            float3 newPosition = position;
            float3 newVelocity = linearVelocity;

            var rigidbodies = collisionWorld.Bodies;

            var constraints = new NativeList<SurfaceConstraintInfo>(k_DefaultConstraintsCapacity, Allocator.Temp);
            var distanceHits = new NativeList<DistanceHit>(math.max(k_DefaultQueryHitsCapacity, stepInput.distanceHits.Length), Allocator.Temp);
            var castHits = new NativeList<ColliderCastHit>(k_DefaultQueryHitsCapacity, Allocator.Temp);

            NativeListWriteOnlyWrapper<ColliderCastHit> colliderCastWrapper;
            NativeListWriteOnlyWrapper<DistanceHit> distanceWrapper;

            const float timeEpsilon = 0.000001f;
            for (int i = 0; i < maxIterations && remainingTime > timeEpsilon; i++)
            {
                constraints.Clear();

                // Do a collider cast
                {
                    float3 displacement = newVelocity * remainingTime;

                    castHits.Clear();

                    var collector = new ListCollectorExclude<ColliderCastHit, NativeList<ColliderCastHit>, NativeListWriteOnlyWrapper<ColliderCastHit>>(
                        stepInput.rigidbodyIndex, 
                        1.0f, 
                        //rigidbodies, 
                        ref castHits, 
                        ref colliderCastWrapper);
                    var input = new ColliderCastInput()
                    {
                        Collider = Collider,
                        Orientation = orientation,
                        Start = newPosition,
                        End = newPosition + displacement
                    };
                    collisionWorld.CastCollider(input, ref collector);

                    castHits.Sort(new ColliderCastHitComparer());

                    //float displacementLength = math.length(displacement);
                    // Iterate over hits and create constraints from them
                    for (int hitIndex = 0; hitIndex < collector.NumHits; hitIndex++)
                    {
                        var hit = collector.hits[hitIndex];
                        /*if ((rigidbodies[hit.RigidBodyIndex].Collider.Value.GetLeafFilter(hit.ColliderKey).BelongsTo & belongsTo) == 0)
                            continue;*/

                        if (isUnstopping && hit.RigidBodyIndex < numDynamicBodies)
                            continue;

                        CreateConstraint(
                            stepInput.physicsWorld, 
                            stepInput.up,
                            hit.RigidBodyIndex, 
                            hit.ColliderKey, 
                            hit.Position, 
                            hit.SurfaceNormal,
                            -hit.Fraction * math.dot(hit.SurfaceNormal, displacement),
                            stepInput.skinWidth, 
                            stepInput.maxSlope, 
                            ref constraints);
                    }
                }

                // Then do a collider distance for penetration recovery,
                // but only fix up penetrating hits
                {
                    // Collider distance query
                    if (i > 0)
                    {
                        distanceHits.Clear();

                        var distanceHitsCollector = new ListCollectorExclude<DistanceHit, NativeList<DistanceHit>, NativeListWriteOnlyWrapper<DistanceHit>>(
                            stepInput.rigidbodyIndex, 
                            stepInput.contactTolerance, 
                            ref distanceHits, 
                            ref distanceWrapper);
                        {
                            ColliderDistanceInput input = new ColliderDistanceInput()
                            {
                                MaxDistance = stepInput.contactTolerance,
                                Transform = math.RigidTransform(orientation, position),
                                Collider = Collider
                            };
                            collisionWorld.CalculateDistance(input, ref distanceHitsCollector);
                        }

                        distanceHits.Sort(new DistanceHitComparer());
                    }
                    else
                    {
                        distanceHits.ResizeUninitialized(stepInput.distanceHits.Length);
                        distanceHits.AsArray().Slice().CopyFrom(stepInput.distanceHits);
                    }

                    // Iterate over penetrating hits and fix up distance and normal
                    int numConstraints = constraints.Length;
                    for (int hitIndex = 0; hitIndex < distanceHits.Length; hitIndex++)
                    {
                        DistanceHit hit = distanceHits[hitIndex];
                        /*if ((rigidbodies[hit.RigidBodyIndex].Collider.Value.GetLeafFilter(hit.ColliderKey).BelongsTo & belongsTo) == 0)
                            continue;*/

                        if (isUnstopping && hit.RigidBodyIndex < numDynamicBodies)
                            continue;

                        if (hit.Distance < stepInput.skinWidth)
                        {
                            bool found = false;

                            // Iterate backwards to locate the original constraint before the max slope constraint
                            for (int constraintIndex = numConstraints - 1; constraintIndex >= 0; constraintIndex--)
                            {
                                SurfaceConstraintInfo constraint = constraints[constraintIndex];
                                if (constraint.RigidBodyIndex == hit.RigidBodyIndex &&
                                    constraint.ColliderKey.Equals(hit.ColliderKey))
                                {
                                    // Fix up the constraint (normal, distance)
                                    {
                                        // Create new constraint
                                        SurfaceConstraintInfo newConstraint = CreateConstraintFromHit(
                                            stepInput.physicsWorld, 
                                            hit.ColliderKey,
                                            hit.Position, 
                                            hit.SurfaceNormal, 
                                            hit.Distance,
                                            math.dot(hit.SurfaceNormal, stepInput.up), 
                                            stepInput.skinWidth,
                                            hit.RigidBodyIndex);

                                        // Resolve its penetration
                                        ResolveConstraintPenetration(ref newConstraint);

                                        // Write back
                                        constraints[constraintIndex] = newConstraint;
                                    }

                                    found = true;
                                    break;
                                }
                            }

                            // Add penetrating hit not caught by collider cast
                            if (!found)
                            {
                                CreateConstraint(stepInput.physicsWorld, stepInput.up,
                                    hit.RigidBodyIndex, hit.ColliderKey, hit.Position, hit.SurfaceNormal, hit.Distance,
                                    stepInput.skinWidth, stepInput.maxSlope, ref constraints);
                            }
                        }
                    }
                }

                // Min delta time for solver to break
                float minDeltaTime = 0.0f;
                if (math.lengthsq(newVelocity) > k_SimplexSolverEpsilonSq)
                {
                    // Min delta time to travel at least 1cm
                    minDeltaTime = 0.01f / math.length(newVelocity);
                }

                // Solve
                float3 prevVelocity = newVelocity;
                float3 prevPosition = newPosition;
                SimplexSolver.Solve(remainingTime, minDeltaTime, stepInput.maxMovementSpeed, stepInput.up, ref constraints, ref newPosition, ref newVelocity, out float integratedTime);

                // Apply impulses to hit bodies
                if (affectBodies)
                {
                    CalculateAndStoreDeferredImpulses(tau, damping, characterMass, stepInput.deltaTime, prevVelocity, gravity, stepInput.physicsWorld, constraints.AsArray(), ref deferredImpulseWriter);
                }

                // Calculate new displacement
                float3 newDisplacement = newPosition - prevPosition;

                // If simplex solver moved the character we need to re-cast to make sure it can move to new position
                if (math.lengthsq(newDisplacement) > k_SimplexSolverEpsilon)
                {
                    // Check if we can walk to the position simplex solver has suggested
                    
                    var newCollector = new CharacterControllerClosestHitCollector<ColliderCastHit>(
                        //belongsTo, 
                        isUnstopping ? numDynamicBodies : 0, 
                        stepInput.rigidbodyIndex, 
                        1.0f, 
                        rigidbodies, 
                        constraints);

                    ColliderCastInput input = new ColliderCastInput()
                    {
                        Collider = Collider,
                        Orientation = orientation,
                        Start = prevPosition,
                        End = prevPosition + newDisplacement
                    };

                    collisionWorld.CastCollider(input, ref newCollector);

                    if (newCollector.NumHits > 0)
                    {
                        ColliderCastHit hit = newCollector.closestHit;

                        // Move character along the newDisplacement direction until it reaches this new contact
                        {
                            Assert.IsTrue(hit.Fraction >= 0.0f && hit.Fraction <= 1.0f);

                            integratedTime *= hit.Fraction;
                            newPosition = prevPosition + newDisplacement * hit.Fraction;
                        }
                    }
                }

                // Reduce remaining time
                remainingTime -= integratedTime;

                // Write back position so that the distance query will update results
                position = newPosition;
            }

            constraints.Dispose();
            distanceHits.Dispose();
            castHits.Dispose();

            // Write back final velocity
            linearVelocity = newVelocity;
        }

        private static SurfaceConstraintInfo CreateConstraintFromHit(
            in PhysicsWorldContainer world, 
            in ColliderKey colliderKey,
            in float3 hitPosition, 
            in float3 normal, 
            float distance,
            float verticalComponent,
            float skinWidth,
            int rigidBodyIndex)
        {
            bool bodyIsDynamic = 0 <= rigidBodyIndex && rigidBodyIndex < world.dynamicBodyCount;
            return new SurfaceConstraintInfo()
            {
                ColliderKey = colliderKey,
                Plane = new Plane
                {
                    Normal = normal,
                    Distance = distance - skinWidth,
                },
                HitPosition = hitPosition,
                Velocity = bodyIsDynamic ?
                    world.GetLinearVelocity(rigidBodyIndex, hitPosition) :
                    float3.zero,
                VerticalComponent = verticalComponent, 
                RigidBodyIndex = rigidBodyIndex, 
                Priority = bodyIsDynamic ? 1 : 0
            };
        }

        private static void CreateMaxSlopeConstraint(in float3 up, ref SurfaceConstraintInfo constraint, out SurfaceConstraintInfo maxSlopeConstraint)
        {
            //float verticalComponent = math.dot(constraint.Plane.Normal, up);

            SurfaceConstraintInfo newConstraint = constraint;
            newConstraint.Plane.Normal = math.normalize(newConstraint.Plane.Normal - constraint.VerticalComponent * up);
            newConstraint.VerticalComponent = 0.0f;// math.dot(newConstraint.Plane.Normal, up);
            float distance = newConstraint.Plane.Distance;

            // Calculate distance to the original plane along the new normal.
            // Clamp the new distance to 2x the old distance to avoid penetration recovery explosions.
            newConstraint.Plane.Distance = distance / math.max(math.dot(newConstraint.Plane.Normal, constraint.Plane.Normal), 0.5f);

            if (newConstraint.Plane.Distance < 0.0f)
            {
                // Disable penetration recovery for the original plane
                constraint.Plane.Distance = 0.0f;

                // Prepare velocity to resolve penetration
                ResolveConstraintPenetration(ref newConstraint);
            }

            // Output max slope constraint
            maxSlopeConstraint = newConstraint;
        }

        private static void ResolveConstraintPenetration(ref SurfaceConstraintInfo constraint)
        {
            // Fix up the velocity to enable penetration recovery
            if (constraint.Plane.Distance < 0.0f)
            {
                float3 newVel = constraint.Velocity - constraint.Plane.Normal * constraint.Plane.Distance;
                constraint.Velocity = newVel;
                constraint.Plane.Distance = 0.0f;
            }
        }

        private static void CreateConstraint(
            in PhysicsWorldContainer world, 
            in float3 up,
            int hitRigidBodyIndex, 
            in ColliderKey hitColliderKey, 
            in float3 hitPosition, 
            in float3 hitSurfaceNormal, 
            float hitDistance,
            float skinWidth, 
            float maxSlopeCos, 
            ref NativeList<SurfaceConstraintInfo> constraints)
        {
            SurfaceConstraintInfo constraint = CreateConstraintFromHit(
                world, 
                hitColliderKey, 
                hitPosition,
                hitSurfaceNormal,
                hitDistance,
                math.dot(hitSurfaceNormal, up),
                skinWidth,
                hitRigidBodyIndex);

            // Check if max slope plane is required
            //float verticalComponent = math.dot(constraint.Plane.Normal, up);
            bool shouldAddPlane = constraint.VerticalComponent > k_SimplexSolverEpsilon && constraint.VerticalComponent < maxSlopeCos;
            if (shouldAddPlane)
            {
                constraint.IsTooSteep = true;
                CreateMaxSlopeConstraint(up, ref constraint, out SurfaceConstraintInfo maxSlopeConstraint);
                constraints.Add(maxSlopeConstraint);
            }

            // Prepare velocity to resolve penetration
            ResolveConstraintPenetration(ref constraint);

            // Add original constraint to the list
            constraints.Add(constraint);
        }

        private static unsafe void CalculateAndStoreDeferredImpulses(
            float tau, 
            float damping, 
            float characterMass, 
            float deltaTime, 
            in float3 linearVelocity,
            in float3 gravity, 
            in PhysicsWorldContainer physicsWorld, 
            in NativeArray<SurfaceConstraintInfo> constraints, 
            ref NativeQueue<DeferredCharacterControllerImpulse>.ParallelWriter deferredImpulseWriter)
        {
            var collisionWorld = physicsWorld.collisionWorld;
            var motionVelocities = physicsWorld.motionVelocities;
            for (int i = 0; i < constraints.Length; i++)
            {
                SurfaceConstraintInfo constraint = constraints[i];

                int rigidBodyIndex = constraint.RigidBodyIndex;
                if (rigidBodyIndex < 0 || rigidBodyIndex >= collisionWorld.NumDynamicBodies)
                {
                    // Invalid and static bodies should be skipped
                    continue;
                }

                RigidBody body = collisionWorld.Bodies[rigidBodyIndex];

                float3 pointRelVel = physicsWorld.GetLinearVelocity(rigidBodyIndex, constraint.HitPosition);
                pointRelVel -= linearVelocity;

                float projectedVelocity = math.dot(pointRelVel, constraint.Plane.Normal);

                // Required velocity change
                float deltaVelocity = -projectedVelocity * damping;

                float distance = constraint.Plane.Distance;
                if (distance < 0.0f)
                {
                    deltaVelocity += (distance / deltaTime) * tau;
                }

                // Calculate impulse
                MotionVelocity mv = motionVelocities[rigidBodyIndex];
                float3 impulse = float3.zero;
                if (deltaVelocity < 0.0f)
                {
                    // Impulse magnitude
                    float impulseMagnitude = 0.0f;
                    {
                        float objectMassInv = GetInvMassAtPoint(constraint.HitPosition, constraint.Plane.Normal, body, mv);
                        impulseMagnitude = deltaVelocity / objectMassInv;
                    }

                    impulse = impulseMagnitude * constraint.Plane.Normal;
                }

                // Add gravity
                {
                    // Effect of gravity on character velocity in the normal direction
                    float3 charVelDown = gravity * deltaTime;
                    float relVelN = math.dot(charVelDown, constraint.Plane.Normal);

                    // Subtract separation velocity if separating contact
                    {
                        bool isSeparatingContact = projectedVelocity < 0.0f;
                        float newRelVelN = relVelN - projectedVelocity;
                        relVelN = math.select(relVelN, newRelVelN, isSeparatingContact);
                    }

                    // If resulting velocity is negative, an impulse is applied to stop the character
                    // from falling into the body
                    {
                        float3 newImpulse = impulse;
                        newImpulse += relVelN * characterMass * constraint.Plane.Normal;
                        impulse = math.select(impulse, newImpulse, relVelN < 0.0f);
                    }
                }

                // Store impulse
                deferredImpulseWriter.Enqueue(
                    new DeferredCharacterControllerImpulse()
                    {
                        Entity = body.Entity,
                        Impulse = impulse,
                        Point = constraint.HitPosition
                    });
            }
        }
        
        static float GetInvMassAtPoint(in float3 point, in float3 normal, in RigidBody body, in MotionVelocity mv)
        {
            var massCenter =
                math.transform(body.WorldFromBody, body.Collider.Value.MassProperties.MassDistribution.Transform.pos);
            float3 arm = point - massCenter;
            float3 jacAng = math.cross(arm, normal);
            float3 armC = jacAng * mv.InverseInertia;

            float objectMassInv = math.dot(armC, jacAng);
            objectMassInv += mv.InverseMass;

            return objectMassInv;
        }
    }
}