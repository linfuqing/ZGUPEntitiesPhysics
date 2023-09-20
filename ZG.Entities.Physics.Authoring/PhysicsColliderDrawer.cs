using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Physics;
using Unity.Physics.Authoring;
using UnityEngine;

namespace ZG
{
#pragma warning disable 618
    public class PhysicsColliderDrawer : DisplayBodyColliders.DrawComponent
#pragma warning restore 618
    {
        public struct Node
        {
            public bool isDrawVertices;
            public Color color;
            public RigidBody rigidbody;
        }

        public List<Node> nodes;

        private static PhysicsColliderDrawer __instance;

        public static PhysicsColliderDrawer instance
        {
            get
            {
                if(__instance == null)
                {
                    GameObject gameObject = new GameObject("PhysicsColliderDrawer");
                    DontDestroyOnLoad(gameObject);
                    __instance = gameObject.AddComponent<PhysicsColliderDrawer>();
                    //__instance.EnableColliders = 1;
                }

                return __instance;
            }
        }

        public void Draw(bool isDrawVertices, Color color, RigidBody rigidbody)
        {
            Node node;
            node.isDrawVertices = isDrawVertices;
            node.color = color;
            node.rigidbody = rigidbody;

            if (nodes == null)
                nodes = new List<Node>();

            nodes.Add(node);
        }

        public new unsafe void OnDrawGizmos()
        {
            //base.OnDrawGizmos();

            if (EnableColliders == 0 && EnableEdges == 0)
                return;

            int numNodes = nodes == null ? 0 : nodes.Count;
            if (numNodes > 0)
            {
                for (int i = 0; i < numNodes; ++i)
                {
                    var node = nodes[i];
                    if (!node.rigidbody.Collider.IsCreated)
                        continue;

                    var displayResults = BuildDebugDisplayMesh(node.rigidbody.Collider);
                    if (displayResults.Count == 0)
                        continue;

                    Gizmos.color = node.color;

                    foreach (DisplayResult dr in displayResults)
                    {
                        if (EnableColliders != 0)
                        {
                            Vector3 position = math.transform(node.rigidbody.WorldFromBody, dr.Position);
                            Quaternion orientation = math.mul(node.rigidbody.WorldFromBody.rot, dr.Orientation);
                            Gizmos.DrawMesh(dr.Mesh, position, math.normalize(orientation), dr.Scale);
                            if (dr.Mesh != CachedReferenceCylinder && dr.Mesh != CachedReferenceSphere)
                            {
                                // Cleanup any meshes that are not our cached ones
                                Destroy(dr.Mesh);
                            }
                        }

                        if (EnableEdges != 0)
                            DrawConnectivity(node.rigidbody, node.isDrawVertices);
                    }
                }
                
                nodes.Clear();
            }
        }
    }
}