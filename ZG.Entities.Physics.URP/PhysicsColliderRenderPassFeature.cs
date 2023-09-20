using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Physics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ZG
{
    public class PhysicsColliderRenderPassFeature : ScriptableRendererFeature
    {
        class RenderPass : ScriptableRenderPass
        {
            private UnityEngine.Material __material;
            private ProfilingSampler __profilingSampler = new ProfilingSampler("Physics Colliders");

            public RenderPass(UnityEngine.Material material)
            {
                __material = material;
            }

            // This method is called before executing the render pass.
            // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
            // When empty this render pass will render to the active camera render target.
            // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
            // The render pipeline will ensure target setup and clearing happens in a performant manner.
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
            }

            // Here you can implement the rendering logic.
            // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
            // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
            // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!Application.isPlaying)
                    return;

                var drawer = PhysicsColliderDrawer.instance;
                var nodes = drawer.nodes;
                int numNodes = nodes == null ? 0 : nodes.Count;
                if (numNodes > 0)
                {
                    var cmd = CommandBufferPool.Get();
                    using (new ProfilingScope(cmd, __profilingSampler))
                    {
                        for (int i = 0; i < numNodes; ++i)
                        {
                            var node = nodes[i];
                            if (!node.rigidbody.Collider.IsCreated)
                                continue;

                            var displayResults = PhysicsColliderDrawer.BuildDebugDisplayMesh(node.rigidbody.Collider);
                            if (displayResults.Count == 0)
                                continue;

                            foreach (var dr in displayResults)
                            {
                                Vector3 position = math.transform(node.rigidbody.WorldFromBody, dr.Position);
                                Quaternion orientation = math.mul(node.rigidbody.WorldFromBody.rot, dr.Orientation);
                                Matrix4x4 matrix = Matrix4x4.TRS(position, orientation, dr.Scale);
                                cmd.DrawMesh(dr.Mesh, matrix, __material);
                                if (dr.Mesh != PhysicsColliderDrawer.CachedReferenceCylinder && dr.Mesh != PhysicsColliderDrawer.CachedReferenceSphere)
                                {
                                    // Cleanup any meshes that are not our cached ones
                                    Destroy(dr.Mesh);
                                }
                            }
                        }
                    }

                    context.ExecuteCommandBuffer(cmd);

                    CommandBufferPool.Release(cmd);

                    nodes.Clear();
                }
            }

            // Cleanup any allocated resources that were created during the execution of this render pass.
            public override void OnCameraCleanup(CommandBuffer cmd)
            {
            }
        }

        public UnityEngine.Material material;

        private RenderPass __renderPass;

        /// <inheritdoc/>
        public override void Create()
        {
            __renderPass = new RenderPass(material);

            // Configures where the render pass should be injected.
            __renderPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        }

        // Here you can inject one or multiple render passes in the renderer.
        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(__renderPass);
        }
    }
}