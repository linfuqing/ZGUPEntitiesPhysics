using UnityEngine;

namespace ZG
{
    public class PhysicsShapesAttribute : PropertyAttribute
    {
        public string path;

        public PhysicsShapesAttribute(string path)
        {
            this.path = path;
        }
    }
}