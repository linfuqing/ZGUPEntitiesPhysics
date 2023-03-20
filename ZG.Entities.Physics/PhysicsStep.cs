using UnityEngine;
using Unity.Physics.Authoring;

namespace ZG
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PhysicsStepAuthoring))]
    public class PhysicsStep : EntityProxyComponent
    {
        /*protected override void _OnInit()
        {
            this.AddComponentData(GetComponent<PhysicsStepAuthoring>().AsComponent);
        }*/
    }
}
