using System;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Authoring;
using Math = Unity.Physics.Math;
using Unity.Entities;

namespace ZG
{
    public static class PhysicsGeometryUtility
    {
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateSphere : IJob
        {
            public bool isBake;

            public quaternion orientation;

            public CollisionFilter filter;

            public Material material;

            public SphereGeometry input;

            public NativeList<CompoundCollider.ColliderBlobInstance> output;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public void Execute()
            {
                var localToWorld = hierarchy.CalculateLocalToWorld(out var localToRoot);

                var geometry = input;
                if (isBake)
                {
                    var center = input.Center;
                    var radius = input.Radius;

                    var orientation = this.orientation;
                    var basisToWorld = GetBasisToWorldMatrix(localToWorld, center, orientation, 1f);
                    var basisPriority = basisToWorld.HasShear() ? GetBasisAxisPriority(basisToWorld) : k_DefaultAxisPriority;
                    var bakeToShape = GetPrimitiveBakeToShapeMatrix(localToWorld, ref center, ref orientation, 1f, basisPriority);

                    radius *= math.cmax(bakeToShape.DecomposeScale());

                    geometry.Radius = radius;
                    geometry.Center = center;
                }

                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.CompoundFromChild = Math.DecomposeRigidBodyTransform(localToRoot);
                colliderBlobInstance.Collider = SphereCollider.Create(geometry, filter, material);
                output.Add(colliderBlobInstance);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateBox : IJob
        {
            public bool isBake;

            public CollisionFilter filter;

            public Material material;

            public BoxGeometry input;

            public NativeList<CompoundCollider.ColliderBlobInstance> output;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public void Execute()
            {
                var localToWorld = hierarchy.CalculateLocalToWorld(out var localToRoot);

                var geometry = input;
                if (isBake)
                {
                    var center = input.Center;
                    var size = input.Size;
                    var orientation = input.Orientation;

                    var bakeToShape = GetBakeToShape(localToWorld, ref center, ref orientation);
                    bakeToShape = math.mul(bakeToShape, float4x4.Scale(size));

                    var scale = bakeToShape.DecomposeScale();

                    size = scale;

                    geometry.Center = center;
                    geometry.Size = size;
                    geometry.Orientation = orientation;
                }

                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.CompoundFromChild = Math.DecomposeRigidBodyTransform(localToRoot);
                colliderBlobInstance.Collider = BoxCollider.Create(geometry, filter, material);
                output.Add(colliderBlobInstance);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateCapsule : IJob
        {
            public bool isBake;

            public CollisionFilter filter;

            public Material material;

            public CapsuleGeometryAuthoring input;

            public NativeList<CompoundCollider.ColliderBlobInstance> output;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public void Execute()
            {
                var localToWorld = hierarchy.CalculateLocalToWorld(out var localToRoot);

                CapsuleGeometryAuthoring authoring = input;
                if (isBake)
                {
                    var height = input.Height;
                    var radius = input.Radius;
                    var center = input.Center;
                    var orientation = input.Orientation;

                    var bakeToShape = GetBakeToShape(localToWorld, ref center, ref orientation);
                    var scale = bakeToShape.DecomposeScale();

                    radius *= math.cmax(scale.xy);
                    height = math.max(0, height * scale.z);

                    authoring.Height = height;
                    authoring.Radius = radius;
                    authoring.Center = center;
                    authoring.Orientation = orientation;
                }

                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.CompoundFromChild = Math.DecomposeRigidBodyTransform(localToRoot);
                colliderBlobInstance.Collider = CapsuleCollider.Create(authoring.ToRuntime(), filter, material);
                output.Add(colliderBlobInstance);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateCapsuleLegacy : IJob
        {
            public bool isBake;

            public int direction;
            public float height;
            public float radius;
            public float3 center;

            public CollisionFilter filter;

            public Material material;

            public NativeList<CompoundCollider.ColliderBlobInstance> output;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public void Execute()
            {
                CapsuleGeometryAuthoring authoring = default;
                authoring.Height = height;
                authoring.Radius = radius;
                authoring.Center = center;
                authoring.Orientation = Math.FromToRotation(math.float3(0.0f, 0.0f, 1.0f), new float3 { [direction] = 1.0f });

                CreateCapsule createCapsule;
                createCapsule.isBake = isBake;
                createCapsule.filter = filter;
                createCapsule.material = material;
                createCapsule.input = authoring;
                createCapsule.output = output;
                createCapsule.hierarchy = hierarchy;
                createCapsule.Execute();
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        struct CreateCylinder : IJob
        {
            public bool isBake;

            public CollisionFilter filter;

            public Material material;

            public CylinderGeometry input;

            public NativeList<CompoundCollider.ColliderBlobInstance> output;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public void Execute()
            {
                var localToWorld = hierarchy.CalculateLocalToWorld(out var localToRoot);

                var geometry = input;
                if (isBake)
                {
                    var height = input.Height;
                    var radius = input.Radius;
                    var bevelRadius = input.BevelRadius;
                    var center = input.Center;
                    var orientation = input.Orientation;

                    var bakeToShape = GetBakeToShape(localToWorld, ref center, ref orientation);
                    var scale = bakeToShape.DecomposeScale();

                    height *= scale.z;
                    radius *= math.cmax(scale.xy);

                    geometry.Height = height;
                    geometry.Radius = radius;
                    geometry.BevelRadius = math.min(bevelRadius, math.min(height * 0.5f, radius));
                    geometry.Center = center;
                    geometry.Orientation = orientation;
                }

                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.CompoundFromChild = Math.DecomposeRigidBodyTransform(localToRoot);
                colliderBlobInstance.Collider = CylinderCollider.Create(geometry, filter, material);
                output.Add(colliderBlobInstance);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        struct CreatePlane : IJob
        {
            public bool isBake;

            public float2 size;
            public float3 center;

            public quaternion orientation;

            public CollisionFilter filter;
            public Material material;

            public NativeList<CompoundCollider.ColliderBlobInstance> result;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public void Execute()
            {
                var localToWorld = hierarchy.CalculateLocalToWorld(out var localToRoot);

                GetPlanePoints(center, size, orientation, out var c0, out var c1, out var c2, out var c3);
                if (isBake)
                {
                    var localToShape = math.mul(math.inverse(new float4x4(Math.DecomposeRigidBodyTransform(localToWorld))), localToWorld);
                    c0 = math.mul(localToShape, new float4(c0, 1f)).xyz;
                    c1 = math.mul(localToShape, new float4(c1, 1f)).xyz;
                    c2 = math.mul(localToShape, new float4(c2, 1f)).xyz;
                    c3 = math.mul(localToShape, new float4(c3, 1f)).xyz;
                }

                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.CompoundFromChild = Math.DecomposeRigidBodyTransform(localToRoot);
                colliderBlobInstance.Collider = PolygonCollider.CreateQuad(c0, c1, c2, c3, filter, material);
                result.Add(colliderBlobInstance);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateConvexHull : IJob
        {
            public float4x4 localToRoot;

            public CollisionFilter filter;

            public Material material;

            public ConvexHullGenerationParameters generationParameters;

            [ReadOnly]
            public NativeArray<float3> pointCloud;

            public NativeList<CompoundCollider.ColliderBlobInstance> result;

            public void Execute()
            {
                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.CompoundFromChild = Math.DecomposeRigidBodyTransform(localToRoot);
                colliderBlobInstance.Collider = ConvexCollider.Create(
                                pointCloud,
                                generationParameters.ToRunTime(),
                                filter,
                                material);

                result.Add(colliderBlobInstance);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CreateMesh : IJob
        {
            public float4x4 localToRoot;

            public CollisionFilter filter;

            public Material material;

            [ReadOnly]
            public NativeArray<float3> vertices;
            [ReadOnly]
            public NativeArray<int3> triangles;

            public NativeList<CompoundCollider.ColliderBlobInstance> result;

            public void Execute()
            {
                CompoundCollider.ColliderBlobInstance colliderBlobInstance;
                colliderBlobInstance.CompoundFromChild = Math.DecomposeRigidBodyTransform(localToRoot);
                colliderBlobInstance.Collider = MeshCollider.Create(
                                vertices,
                                triangles,
                                filter,
                                material);

                result.Add(colliderBlobInstance);
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CalculateLocalToShape : IJob
        {
            [ReadOnly] 
            public NativeArray<float3> points;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public NativeArray<float4x4> matrices;

            public void Execute()
            {
                var localToWorld = hierarchy.CalculateLocalToWorld(out var localToRoot);

                var localToShapeQuantized = math.mul(math.inverse(GetShapeToWorldMatrix(localToWorld)), localToWorld);
                var aabb = new Aabb { Min = float.MaxValue, Max = float.MinValue };

                int length = points.Length;
                for (var i = 0; i < length; ++i)
                    aabb.Include(points[i]);

                GetQuantizedTransformations(localToShapeQuantized, aabb, out localToShapeQuantized);

                matrices[0] = localToShapeQuantized;
                matrices[1] = localToRoot;
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct CalculateLocalToRootJob : IJob
        {
            public NativeArray<float4x4> localToRoot;

            [ReadOnly]
            public NativeArray<EntityTransform> hierarchy;

            public void Execute()
            {
                hierarchy.CalculateLocalToWorld(out var localToRoot);

                this.localToRoot[0] = localToRoot;
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Deterministic)]
        private struct BakePointsJob : IJobParallelFor
        {
            public float4x4 localToShape;
            public NativeArray<float3> points;

            public void Execute(int index) => points[index] = math.mul(localToShape, new float4(points[index], 1f)).xyz;
        }

        private static readonly int[] k_NextAxis = { 1, 2, 0 };
        private static readonly int[] k_PrevAxis = { 2, 0, 1 };

        // used for de-skewing basis vectors; default priority assumes primary axis is z, secondary axis is y
        private static readonly int3 k_DefaultAxisPriority = new int3(2, 1, 0);

        // solver generally not expected to be precise under 0.01 units
        private const float k_DefaultLinearPrecision = 0.001f;

        public static float3 RoundToNearest(float3 value, float3 intervalPerAxis) =>
            math.round(value / intervalPerAxis) * intervalPerAxis;

        // prevents hashing negative zero
        public static float3 Sanitize(float3 value)
        {
            for (var i = 0; i < 3; ++i)
            {
                if (value[i] == 0f)
                    value[i] = 0f;
            }
            return value;
        }

        public static float3x3 Sanitize(float3x3 value)
        {
            for (var i = 0; i < 3; ++i)
                value[i] = Sanitize(value[i]);

            return value;
        }

        public static bool HasShear(this float4x4 m)
        {
            // scale each axis by abs of its max component in order to work with very large/small scales
            var rs0 = m.c0.xyz / math.max(math.cmax(math.abs(m.c0.xyz)), float.Epsilon);
            var rs1 = m.c1.xyz / math.max(math.cmax(math.abs(m.c1.xyz)), float.Epsilon);
            var rs2 = m.c2.xyz / math.max(math.cmax(math.abs(m.c2.xyz)), float.Epsilon);
            // verify all axes are orthogonal
            const float k_Zero = 1e-6f;
            return
                math.abs(math.dot(rs0, rs1)) > k_Zero ||
                math.abs(math.dot(rs0, rs2)) > k_Zero ||
                math.abs(math.dot(rs1, rs2)) > k_Zero;
        }

        public static bool HasNonUniformScale(this float4x4 m)
        {
            var s = new float3(math.lengthsq(m.c0.xyz), math.lengthsq(m.c1.xyz), math.lengthsq(m.c2.xyz));
            return math.cmin(s) != math.cmax(s);
        }

        public static float3 DecomposeScale(this float4x4 matrix) =>
            new float3(math.length(matrix.c0.xyz), math.length(matrix.c1.xyz), math.length(matrix.c2.xyz));

        public static float4 DeskewSecondaryAxis(float4 primaryAxis, float4 secondaryAxis)
        {
            var n0 = math.normalizesafe(primaryAxis);
            var dot = math.dot(secondaryAxis, n0);
            return secondaryAxis - n0 * dot;
        }

        public static void MakeZAxisPrimaryBasis(ref int3 basisPriority)
        {
            if (basisPriority[1] == 2)
                basisPriority = basisPriority.yxz;
            else if (basisPriority[2] == 2)
                basisPriority = basisPriority.zxy;
        }

        //public static float4x4 GetLocalToRootMatrix(this float4x4 localToWorld, float4x4 rootToWorld) => math.mul(math.inverse(rootToWorld), localToWorld);

        public static float4x4 GetShapeToWorldMatrix(this float4x4 localToWorld) =>
             new float4x4(Math.DecomposeRigidBodyTransform(localToWorld));

        // matrix to transform point from shape's local basis into world space
        public static float4x4 GetBasisToWorldMatrix(
            float4x4 localToWorld, 
            float3 center, 
            quaternion orientation, 
            float3 size) =>
            math.mul(localToWorld, float4x4.TRS(center, orientation, size));

        // matrix to transform point on a primitive from bake space into space of the shape
        public static float4x4 GetPrimitiveBakeToShapeMatrix(
            float4x4 localToWorld, 
            ref float3 center, 
            ref quaternion orientation, 
            float3 scale, 
            int3 basisPriority)
        {
            if (
                basisPriority.x == basisPriority.y
                || basisPriority.x == basisPriority.z
                || basisPriority.y == basisPriority.z
            )
                __ThrowArgumentException();

            var localToBasis = float4x4.TRS(center, orientation, scale);
            // correct for imprecision in cases of no scale to prevent e.g., convex radius from being altered
            if (scale.Equals(new float3(1f)))
            {
                localToBasis.c0 = math.normalizesafe(localToBasis.c0);
                localToBasis.c1 = math.normalizesafe(localToBasis.c1);
                localToBasis.c2 = math.normalizesafe(localToBasis.c2);
            }
            var localToBake = math.mul(localToWorld, localToBasis);

            if (localToBake.HasNonUniformScale() || localToBake.HasShear())
            {
                // deskew second longest axis with respect to longest axis
                localToBake[basisPriority[1]] =
                    DeskewSecondaryAxis(localToBake[basisPriority[0]], localToBake[basisPriority[1]]);

                // recompute third axes from first two
                var n2 = math.normalizesafe(
                    new float4(math.cross(localToBake[basisPriority[0]].xyz, localToBake[basisPriority[1]].xyz), 0f)
                );
                localToBake[basisPriority[2]] = n2 * math.dot(localToBake[basisPriority[2]], n2);
            }

            var bakeToShape = math.mul(math.inverse(GetShapeToWorldMatrix(localToWorld)), localToBake);
            // transform baked center/orientation (i.e. primitive basis) into shape space
            orientation = quaternion.LookRotationSafe(bakeToShape[basisPriority[0]].xyz, bakeToShape[basisPriority[1]].xyz);
            center = bakeToShape.c3.xyz;

            return bakeToShape;
        }

        // priority is determined by length of each size dimension in the shape's basis after applying localToWorld transformation
        public static int3 GetBasisAxisPriority(float4x4 basisToWorld)
       {
            var basisAxisLengths = basisToWorld.DecomposeScale();
            var max = math.cmax(basisAxisLengths);
            var min = math.cmin(basisAxisLengths);
            if (max == min)
                return k_DefaultAxisPriority;

            var imax = max == basisAxisLengths.x ? 0 : max == basisAxisLengths.y ? 1 : 2;

            basisToWorld[k_NextAxis[imax]] = DeskewSecondaryAxis(basisToWorld[imax], basisToWorld[k_NextAxis[imax]]);
            basisToWorld[k_PrevAxis[imax]] = DeskewSecondaryAxis(basisToWorld[imax], basisToWorld[k_PrevAxis[imax]]);

            basisAxisLengths = basisToWorld.DecomposeScale();
            min = math.cmin(basisAxisLengths);
            var imin = min == basisAxisLengths.x ? 0 : min == basisAxisLengths.y ? 1 : 2;
            if (imin == imax)
                imin = k_NextAxis[imax];
            var imid = k_NextAxis[imax] == imin ? k_PrevAxis[imax] : k_NextAxis[imax];

            return new int3(imax, imid, imin);
        }

        public static float4x4 GetBakeToShape(float4x4 localToWorld, ref float3 center, ref quaternion orientation)
        {
            var basisPriority = k_DefaultAxisPriority;
            var sheared = localToWorld.HasShear();
            if (localToWorld.HasNonUniformScale() || sheared)
            {
                if (sheared)
                {
                    var transformScale = localToWorld.DecomposeScale();
                    var basisToWorld = GetBasisToWorldMatrix(localToWorld, center, orientation, transformScale);
                    basisPriority = GetBasisAxisPriority(basisToWorld);
                }
                MakeZAxisPrimaryBasis(ref basisPriority);
            }
            return GetPrimitiveBakeToShapeMatrix(localToWorld, ref center, ref orientation, 1f, basisPriority);
        }

        public static void GetQuantizedTransformations(
            float4x4 leafToBody,
            Aabb bounds,
            out float3 translation,
            out quaternion orientationQ,
            out float3 scale,
            out float3x3 shear,
            float linearPrecision = k_DefaultLinearPrecision)
        {
            translation = RoundToNearest(leafToBody.c3.xyz, linearPrecision);

            var farthestPoint =
                math.abs(math.lengthsq(bounds.Max) > math.lengthsq(bounds.Min) ? bounds.Max : bounds.Min);

            // round scale using precision inversely proportional to mesh size along largest axis
            // (i.e. amount to scale farthest point one unit of linear precision)
            var scalePrecision = linearPrecision / math.max(math.cmax(farthestPoint), math.FLT_MIN_NORMAL);
            scale = RoundToNearest(leafToBody.DecomposeScale(), scalePrecision);
            if (math.determinant(leafToBody) < 0f)
                scale.x *= -1f;

            shear = new float3x3(
                leafToBody.c0.xyz / math.max(math.abs(scale.x), math.FLT_MIN_NORMAL) * math.sign(scale.x),
                leafToBody.c1.xyz / math.max(math.abs(scale.y), math.FLT_MIN_NORMAL) * math.sign(scale.y),
                leafToBody.c2.xyz / math.max(math.abs(scale.z), math.FLT_MIN_NORMAL) * math.sign(scale.z)
            );
            var orientation = float3x3.LookRotationSafe(shear.c2, shear.c1);

            // if shear is very nearly identity, hash it as identity
            // TODO: quantize shear
            shear = math.mul(shear, math.inverse(orientation));
            if (!HasShear(new float4x4(shear, 0f)))
                shear = float3x3.identity;

            // round orientation using precision inversely proportional to scaled mesh size
            // (i.e. radians to rotate farthest scaled point one unit of linear precision)
            var angularPrecision = math.min(linearPrecision / math.length(farthestPoint * scale), math.PI);
            var axisPrecision = math.min(math.cos(angularPrecision), math.sin(angularPrecision));
            orientation.c0 = math.normalize(RoundToNearest(orientation.c0, axisPrecision));
            orientation.c1 = math.normalize(RoundToNearest(orientation.c1, axisPrecision));
            orientation.c2 = math.normalize(RoundToNearest(orientation.c2, axisPrecision));

            translation = Sanitize(translation);
            orientationQ = new quaternion(Sanitize(orientation));
            scale = Sanitize(scale);
            shear = Sanitize(shear);
        }

        public static void GetQuantizedTransformations(
            float4x4 leafToBody,
            Aabb bounds,
            out float4x4 transformations,
            float linearPrecision = k_DefaultLinearPrecision)
        {
            GetQuantizedTransformations(
                leafToBody,
                bounds,
                out var t,
                out var r,
                out var s,
                out var sh,
                linearPrecision);
            transformations = math.mul(new float4x4(sh, 0f), float4x4.TRS(t, r, s));
        }

        public static void GetPlanePoints(
            float3 center, 
            float2 size, 
            quaternion orientation,
            out float3 vertex0, 
            out float3 vertex1, 
            out float3 vertex2, 
            out float3 vertex3
        )
        {
            var sizeYUp = math.float3(size.x, 0, size.y);

            vertex0 = center + math.mul(orientation, sizeYUp * math.float3(-0.5f, 0, 0.5f));
            vertex1 = center + math.mul(orientation, sizeYUp * math.float3(0.5f, 0, 0.5f));
            vertex2 = center + math.mul(orientation, sizeYUp * math.float3(0.5f, 0, -0.5f));
            vertex3 = center + math.mul(orientation, sizeYUp * math.float3(-0.5f, 0, -0.5f));
        }

        //Sphere
        public static void CreateCollider(
            this in SphereGeometry geometry,
            bool isBake,
            quaternion orientation, 
            CollisionFilter filter,
            Material material,
            NativeArray<EntityTransform> hierarchy, 
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreateSphere job;
            job.isBake = isBake; 
            job.orientation = orientation;
            job.filter = filter;
            job.material = material;
            job.input = geometry;
            job.output = colliders;
            job.hierarchy = hierarchy;
            job.Run();
        }

        //Box
        public static void CreateCollider(
            this in BoxGeometry geometry,
            bool isBake,
            CollisionFilter filter,
            Material material,
            NativeArray<EntityTransform> hierarchy,
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreateBox job;
            job.isBake = isBake;
            job.filter = filter;
            job.material = material;
            job.input = geometry;
            job.output = colliders;
            job.hierarchy = hierarchy;
            job.Run();
        }

        //Capsule
        public static void CreateCollider(
            this in CapsuleGeometryAuthoring authoring,
            bool isBake,
            CollisionFilter filter,
            Material material,
            NativeArray<EntityTransform> hierarchy,
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreateCapsule job;
            job.isBake = isBake;
            job.filter = filter;
            job.material = material;
            job.input = authoring;
            job.hierarchy = hierarchy;
            job.output = colliders;
            job.Run();
        }

        //Capsule Legacy
        public static void CreateCollider(
            bool isBake,
            int direction, 
            float height, 
            float radius, 
            float3 center, 
            CollisionFilter filter,
            Material material,
            NativeArray<EntityTransform> hierarchy,
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreateCapsuleLegacy job;
            job.isBake = isBake;
            job.direction = direction;
            job.height = height;
            job.radius = radius;
            job.center = center;
            job.filter = filter;
            job.material = material;
            job.hierarchy = hierarchy;
            job.output = colliders;
            job.Run();
        }

        //Cylinder
        public static void CreateCollider(
            this in CylinderGeometry geometry,
            bool isBake,
            CollisionFilter filter,
            Material material,
            NativeArray<EntityTransform> hierarchy,
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreateCylinder job;
            job.isBake = isBake;
            job.filter = filter;
            job.material = material;
            job.input = geometry;
            job.hierarchy = hierarchy;
            job.output = colliders;
            job.Run();
        }

        //Plane
        public static void CreateCollider(
            bool isBake,
            float2 size,
            float3 center,
            quaternion orientation, 
            CollisionFilter filter,
            Material material,
            NativeArray<EntityTransform> hierarchy,
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreatePlane job;
            job.isBake = isBake;
            job.size = size;
            job.center = center;
            job.orientation = orientation;
            job.filter = filter;
            job.material = material;
            job.hierarchy = hierarchy;
            job.result = colliders;
            job.Run();
        }

        //ConvexHull
        public static void CreateCollider(
            float4x4 localToRoot,
            NativeArray<float3> pointCloud,
            ConvexHullGenerationParameters generationParameters, 
            CollisionFilter filter,
            Material material,
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreateConvexHull job;
            job.localToRoot = localToRoot;
            job.filter = filter;
            job.material = material;
            job.generationParameters = generationParameters;
            job.pointCloud = pointCloud;
            job.result = colliders;
            job.Run();
        }

        //Mesh
        public static void CreateCollider(
            float4x4 localToRoot, 
            NativeArray<float3> vertices,
            NativeArray<int3> triangles, 
            CollisionFilter filter,
            Material material,
            NativeList<CompoundCollider.ColliderBlobInstance> colliders)
        {
            CreateMesh job;
            job.localToRoot = localToRoot;
            job.filter = filter;
            job.material = material;
            job.vertices = vertices;
            job.triangles = triangles;
            job.result = colliders;
            job.Run();
        }

        public static float4x4 CalculateLocalToRoot(this NativeArray<EntityTransform> hierarchy)
        {
            using (var localToRoot = new NativeArray<float4x4>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
            {
                CalculateLocalToRootJob job;
                job.localToRoot = localToRoot;
                job.hierarchy = hierarchy;
                job.Run();

                return localToRoot[0];
            }
        }

        public static float4x4 Bake(
            this NativeArray<float3> points,
            NativeArray<EntityTransform> hierarchy)
        {
            using (var matrices = new NativeArray<float4x4>(2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
            {
                CalculateLocalToShape calculateLocalToShape;
                calculateLocalToShape.points = points;
                calculateLocalToShape.hierarchy = hierarchy;
                calculateLocalToShape.matrices = matrices;
                calculateLocalToShape.Run();

                BakePointsJob job;
                job.points = points;
                job.localToShape = matrices[0];
                job.Run(points.Length);

                return matrices[1];
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void __ThrowArgumentException()
        {
            throw new ArgumentException();
        }
    }
}