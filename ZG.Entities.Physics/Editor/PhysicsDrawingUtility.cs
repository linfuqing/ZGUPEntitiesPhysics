using System;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using Collider = Unity.Physics.Collider;

namespace ZG
{
    class BeveledBoxBoundsHandle : BoxBoundsHandle
    {
        public float BevelRadius { get; set; }

        protected override void DrawWireframe()
        {
            if (BevelRadius <= 0f)
            {
                base.DrawWireframe();
                return;
            }

            var center = (float3)this.center;
            var size = (float3)this.size;
            DrawFace(center, size * new float3(1f, 1f, 1f), 0, 1, 2);
            DrawFace(center, size * new float3(-1f, 1f, 1f), 0, 1, 2);
            DrawFace(center, size * new float3(1f, 1f, 1f), 1, 0, 2);
            DrawFace(center, size * new float3(1f, -1f, 1f), 1, 0, 2);
            DrawFace(center, size * new float3(1f, 1f, 1f), 2, 0, 1);
            DrawFace(center, size * new float3(1f, 1f, -1f), 2, 0, 1);

            var corner = 0.5f * size - new float3(1f) * BevelRadius;
            var rgt = new float3(1f, 0f, 0f);
            var up = new float3(0f, 1f, 0f);
            var fwd = new float3(0f, 0f, 1f);
            DrawCorner(center + corner * new float3(1f, 1f, 1f), quaternion.LookRotation(fwd, up));
            DrawCorner(center + corner * new float3(-1f, 1f, 1f), quaternion.LookRotation(-rgt, up));
            DrawCorner(center + corner * new float3(1f, -1f, 1f), quaternion.LookRotation(rgt, -up));
            DrawCorner(center + corner * new float3(1f, 1f, -1f), quaternion.LookRotation(-fwd, rgt));
            DrawCorner(center + corner * new float3(-1f, -1f, 1f), quaternion.LookRotation(fwd, -up));
            DrawCorner(center + corner * new float3(-1f, 1f, -1f), quaternion.LookRotation(-fwd, up));
            DrawCorner(center + corner * new float3(1f, -1f, -1f), quaternion.LookRotation(-fwd, -up));
            DrawCorner(center + corner * new float3(-1f, -1f, -1f), quaternion.LookRotation(-rgt, -up));
        }

        static Vector3[] s_FacePoints = new Vector3[8];

        void DrawFace(float3 center, float3 size, int a, int b, int c)
        {
            size *= 0.5f;
            var ctr = center + new float3 { [a] = size[a] };
            var i = 0;
            size -= new float3(BevelRadius);
            s_FacePoints[i++] = ctr + new float3 { [b] = size[b], [c] = size[c] };
            s_FacePoints[i++] = ctr + new float3 { [b] = -size[b], [c] = size[c] };
            s_FacePoints[i++] = ctr + new float3 { [b] = -size[b], [c] = size[c] };
            s_FacePoints[i++] = ctr + new float3 { [b] = -size[b], [c] = -size[c] };
            s_FacePoints[i++] = ctr + new float3 { [b] = -size[b], [c] = -size[c] };
            s_FacePoints[i++] = ctr + new float3 { [b] = size[b], [c] = -size[c] };
            s_FacePoints[i++] = ctr + new float3 { [b] = size[b], [c] = -size[c] };
            s_FacePoints[i++] = ctr + new float3 { [b] = size[b], [c] = size[c] };
            Handles.DrawLines(s_FacePoints);
        }

        void DrawCorner(float3 point, quaternion orientation)
        {
            var rgt = math.mul(orientation, new float3(1f, 0f, 0f));
            var up = math.mul(orientation, new float3(0f, 1f, 0f));
            var fwd = math.mul(orientation, new float3(0f, 0f, 1f));
            Handles.DrawWireArc(point, fwd, rgt, 90f, BevelRadius);
            Handles.DrawWireArc(point, rgt, up, 90f, BevelRadius);
            Handles.DrawWireArc(point, up, fwd, 90f, BevelRadius);
        }
    }

    class BeveledCylinderBoundsHandle : PrimitiveBoundsHandle
    {
        public BeveledCylinderBoundsHandle() => midpointHandleDrawFunction = DoMidpointHandle;

        void DoMidpointHandle(int controlID, Vector3 position, Quaternion rotation, float size, EventType eventType)
        {
            var direction = (HandleDirection)(controlID - m_FirstControlID);
            switch (direction)
            {
                case HandleDirection.NegativeX:
                case HandleDirection.PositiveX:
                    break;
                case HandleDirection.NegativeY:
                case HandleDirection.PositiveY:
                    break;
                case HandleDirection.NegativeZ:
                case HandleDirection.PositiveZ:
                    break;
                default:
                    Debug.LogException(
                        new NotImplementedException(
                            $"Unknown handle direction {direction}. " +
                            $"Did you forget to call {nameof(DrawHandle)}() during {EventType.Layout} phase?"
                        )
                    );
                    break;
            }
            Handles.DotHandleCap(controlID, position, rotation, size, eventType);
        }

        public float BevelRadius
        {
            get => m_BevelRadius;
            set
            {
                m_BevelRadius = math.max(0f, value);
                Height = math.max(Height, BevelRadius * 2f);
                Radius = math.max(Radius, BevelRadius);
            }
        }

        float m_BevelRadius = ConvexHullGenerationParameters.Default.BevelRadius;

        public float Height
        {
            get => GetSize().z;
            set
            {
                var size = GetSize();
                size.z = math.max(math.max(0f, 2f * BevelRadius), value);
                SetSize(size);
            }
        }

        public float Radius
        {
            get => GetSize().x * 0.5f;
            set
            {
                var size = GetSize();
                size.x = size.y = math.max(0f, math.max(value, BevelRadius) * 2f);
                SetSize(size);
            }
        }

        public int SideCount
        {
            get => m_SideCount;
            set
            {
                if (value == m_SideCount)
                    return;

                m_SideCount = value;

                Array.Resize(ref m_Points, m_SideCount * 6);
                Array.Resize(ref m_PointsWithRadius, m_SideCount * 10);
            }
        }
        int m_SideCount;

        Vector3[] m_Points = Array.Empty<Vector3>();
        Vector3[] m_PointsWithRadius = Array.Empty<Vector3>();
        int m_FirstControlID;

        protected override void DrawWireframe()
        {
            m_FirstControlID = GUIUtility.GetControlID(GetHashCode(), FocusType.Passive) - 6;

            var halfHeight = new float3(0f, 0f, Height * 0.5f);
            var t = 2f * (m_SideCount - 1) / m_SideCount;
            var prevXY = new float3(math.cos(math.PI * t), math.sin(math.PI * t), 0f) * Radius;
            var prevXYCvx = math.normalizesafe(prevXY) * BevelRadius;
            int step;
            Vector3[] points;
            if (BevelRadius > 0f)
            {
                points = m_PointsWithRadius;
                step = 10;
            }
            else
            {
                points = m_Points;
                step = 6;
            }
            for (var i = 0; i < m_SideCount; ++i)
            {
                t = 2f * i / m_SideCount;
                var xy = new float3(math.cos(math.PI * t), math.sin(math.PI * t), 0f) * Radius;
                var xyCvx = math.normalizesafe(xy) * BevelRadius;
                var idx = i * step;
                var ctr = (float3)center;
                // height
                points[idx++] = ctr + xy + halfHeight - new float3 { z = BevelRadius };
                points[idx++] = ctr + xy - halfHeight + new float3 { z = BevelRadius };
                // top
                points[idx++] = prevXY + halfHeight - prevXYCvx;
                points[idx++] = xy + halfHeight - xyCvx;
                // bottom
                points[idx++] = prevXY - halfHeight - prevXYCvx;
                points[idx++] = xy - halfHeight - xyCvx;
                // convex
                if (BevelRadius > 0f)
                {
                    // top
                    points[idx++] = ctr + prevXY + halfHeight - new float3 { z = BevelRadius };
                    points[idx++] = ctr + xy + halfHeight - new float3 { z = BevelRadius };
                    // bottom
                    points[idx++] = ctr + prevXY - halfHeight + new float3 { z = BevelRadius };
                    points[idx++] = ctr + xy - halfHeight + new float3 { z = BevelRadius };
                    // corners
                    var normal = math.cross(new float3(0f, 0f, 1f), xy);
                    var p = new float3(xy.x, xy.y, halfHeight.z) - new float3(xyCvx.x, xyCvx.y, BevelRadius);
                    Handles.DrawWireArc(ctr + p, normal, xy, -90f, BevelRadius);
                    p *= new float3(1f, 1f, -1f);
                    Handles.DrawWireArc(ctr + p, normal, xy, 90f, BevelRadius);
                }
                prevXY = xy;
                prevXYCvx = xyCvx;
            }
            Handles.DrawLines(points);
        }

        protected override Bounds OnHandleChanged(HandleDirection handle, Bounds boundsOnClick, Bounds newBounds)
        {
            const int k_DirectionX = 0;
            const int k_DirectionY = 1;
            const int k_DirectionZ = 2;

            var changedAxis = k_DirectionX;
            var otherRadiusAxis = k_DirectionY;
            switch (handle)
            {
                case HandleDirection.NegativeY:
                case HandleDirection.PositiveY:
                    changedAxis = k_DirectionY;
                    otherRadiusAxis = k_DirectionX;
                    break;
                case HandleDirection.NegativeZ:
                case HandleDirection.PositiveZ:
                    changedAxis = k_DirectionZ;
                    break;
            }

            var upperBound = newBounds.max;
            var lowerBound = newBounds.min;

            var convexDiameter = 2f * BevelRadius;

            // ensure changed dimension cannot be made less than convex diameter
            if (upperBound[changedAxis] - lowerBound[changedAxis] < convexDiameter)
            {
                switch (handle)
                {
                    case HandleDirection.PositiveX:
                    case HandleDirection.PositiveY:
                    case HandleDirection.PositiveZ:
                        upperBound[changedAxis] = lowerBound[changedAxis] + convexDiameter;
                        break;
                    default:
                        lowerBound[changedAxis] = upperBound[changedAxis] - convexDiameter;
                        break;
                }
            }

            // ensure radius changes uniformly
            if (changedAxis != k_DirectionZ)
            {
                var rad = 0.5f * (upperBound[changedAxis] - lowerBound[changedAxis]);

                lowerBound[otherRadiusAxis] = center[otherRadiusAxis] - rad;
                upperBound[otherRadiusAxis] = center[otherRadiusAxis] + rad;
            }

            return new Bounds((upperBound + lowerBound) * 0.5f, upperBound - lowerBound);
        }
    }

    public static class PhysicsDrawingUtility
    {
        // TODO: implement interactive tool modes
        static readonly BeveledBoxBoundsHandle Box =
            new BeveledBoxBoundsHandle { handleColor = Color.clear };
        static readonly CapsuleBoundsHandle Capsule =
            new CapsuleBoundsHandle { handleColor = Color.clear, heightAxis = CapsuleBoundsHandle.HeightAxis.Z };
        static readonly BeveledCylinderBoundsHandle Cylinder =
            new BeveledCylinderBoundsHandle { handleColor = Color.clear };
        static readonly SphereBoundsHandle Sphere =
            new SphereBoundsHandle { handleColor = Color.clear };
        static readonly BoxBoundsHandle Plane = new BoxBoundsHandle
        {
            handleColor = Color.clear,
            axes = PrimitiveBoundsHandle.Axes.X | PrimitiveBoundsHandle.Axes.Z
        };

        public static unsafe void Draw(Collider* collider, RigidTransform transform)
        {
            switch (collider->Type)
            {
                case ColliderType.Box:
                    var boxGeometry = ((Unity.Physics.BoxCollider*)collider)->Geometry;
                    Box.BevelRadius = boxGeometry.BevelRadius;
                    Box.center = float3.zero;
                    Box.size = boxGeometry.Size;

                    transform = math.mul(transform, math.RigidTransform(boxGeometry.Orientation, boxGeometry.Center));
                    using (new Handles.DrawingScope(math.mul(Handles.matrix, float4x4.TRS(transform.pos, transform.rot, 1f))))
                        Box.DrawHandle();
                    break;
                case ColliderType.Capsule:
                    Capsule.center = float3.zero;
                    Capsule.height = Capsule.radius = 0f;
                    var capsuleGeometry = ((Unity.Physics.CapsuleCollider*)collider)->Geometry;
                    var distance = capsuleGeometry.Vertex0 - capsuleGeometry.Vertex1;
                    var temp = math.RigidTransform(Unity.Physics.Math.FromToRotation(math.float3(0.0f, 0.0f, 1.0f), math.normalizesafe(distance, math.float3(0.0f, 0.0f, 1.0f))), (capsuleGeometry.Vertex0 + capsuleGeometry.Vertex1) * 0.5f);
                    transform = math.mul(transform, temp);
                    Capsule.height = math.mul(math.inverse(temp.rot), distance).z + capsuleGeometry.Radius * 2f;
                    Capsule.radius = capsuleGeometry.Radius;
                    using (new Handles.DrawingScope(math.mul(Handles.matrix, float4x4.TRS(transform.pos, transform.rot, 1f))))
                        Capsule.DrawHandle();
                    break;
                case ColliderType.Sphere:
                    var sphereGeometry = ((Unity.Physics.SphereCollider*)collider)->Geometry;
                    Sphere.center = float3.zero;
                    Sphere.radius = sphereGeometry.Radius;
                    using (new Handles.DrawingScope(math.mul(Handles.matrix, float4x4.TRS(transform.pos + sphereGeometry.Center, transform.rot, 1f))))
                        Sphere.DrawHandle();
                    break;
                case ColliderType.Cylinder:
                    var cylinderGeometry = ((CylinderCollider*)collider)->Geometry;
                    Cylinder.center = float3.zero;
                    Cylinder.Height = cylinderGeometry.Height;
                    Cylinder.Radius = cylinderGeometry.Radius;
                    Cylinder.SideCount = cylinderGeometry.SideCount;
                    Cylinder.BevelRadius = cylinderGeometry.BevelRadius;
                    transform = math.mul(transform, math.RigidTransform(cylinderGeometry.Orientation, cylinderGeometry.Center));
                    using (new Handles.DrawingScope(math.mul(Handles.matrix, float4x4.TRS(transform.pos, transform.rot, 1f))))
                        Cylinder.DrawHandle();
                    break;
                case ColliderType.Convex:
                case ColliderType.Mesh:
                    var points = DrawingUtility.GetEdges(((ConvexCollider*)collider), out _);
                    int numPoints = points.Length;
                    if (numPoints > 0)
                    {
                        for (int i = 0; i < numPoints; ++i)
                            points[i] = math.transform(transform, points[i]);

                        Handles.DrawLines(points);
                    }

                    break;
                case ColliderType.Compound:
                    var children = ((CompoundCollider*)collider)->Children;
                    int numChildren = children.Length;
                    for (int i = 0; i < numChildren; ++i)
                    {
                        ref var child = ref children[i];

                        Draw(child.Collider, math.mul(transform, child.CompoundFromChild));
                    }
                    break;
                default:
                    //throw new NotImplementedException();
                    break;
            }
        }

        public static unsafe void Draw(this Unity.Entities.BlobAssetReference<Collider> collider, in RigidTransform transform)
        {
            Draw((Collider*)collider.GetUnsafePtr(), transform);
        }
    }
}