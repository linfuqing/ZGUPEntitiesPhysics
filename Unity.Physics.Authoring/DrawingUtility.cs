using System.Collections;
using System.Collections.Generic;
using Unity.Physics;
using UnityEngine;

namespace ZG
{
    public static class DrawingUtility
    {
        private static List<Vector3> __reusableEdges = new List<Vector3>();

        public unsafe static Vector3[] GetEdges(ConvexCollider* collider, out Aabb bounds)
        {
            switch (collider->Type)
            {
                case ColliderType.Convex:
                    var convex = collider;
                    Unity.Physics.Authoring.DrawingUtility.GetConvexHullEdges(
                        ref convex->ConvexHull, __reusableEdges
                    );

                    bounds = convex->CalculateAabb();
                    break;
                case ColliderType.Mesh:

                    var mesh = (Unity.Physics.MeshCollider*)collider;
                    Unity.Physics.Authoring.DrawingUtility.GetMeshEdges(
                        ref mesh->Mesh, __reusableEdges
                    );
                    bounds = mesh->CalculateAabb();
                    break;
                default:
                    bounds = Aabb.Empty;

                    return null;
            }

            return __reusableEdges.ToArray();
        }
    }
}