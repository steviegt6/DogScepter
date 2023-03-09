using GameBreaker.Models;
using GameBreaker.Project.Assets;

namespace GameBreaker.Project.Extensions; 

public static class PhysicsPropertiesExtensions
{
    /// <summary>
    /// An explicit cast from a <see cref="AssetObject"/>.<see cref="AssetObject.PhysicsProperties"/>
    /// struct to a <see cref="PhysicsProperties"/>.
    /// </summary>
    /// <param name="physicsProperties">The physics properties as
    /// <see cref="AssetObject"/>.<see cref="AssetObject.PhysicsProperties"/>.</param>
    /// <returns>Physics properties as <see cref="PhysicsProperties"/>.</returns>
    public static GMObject.PhysicsProperties AsObjectProperties(this AssetObject.PhysicsProperties physicsProperties)
    {
        GMObject.PhysicsProperties newPhysics = new GMObject.PhysicsProperties
        {
            IsEnabled = physicsProperties.IsEnabled,
            Sensor = physicsProperties.Sensor,
            Shape = physicsProperties.Shape,
            Density = physicsProperties.Density,
            Restitution = physicsProperties.Restitution,
            Group = physicsProperties.Group,
            LinearDamping = physicsProperties.LinearDamping,
            AngularDamping = physicsProperties.AngularDamping,
            Friction = physicsProperties.Friction,
            IsAwake = physicsProperties.IsAwake,
            IsKinematic = physicsProperties.IsKinematic,
            Vertices = new()
        };
        foreach (AssetObject.PhysicsVertex v in physicsProperties.Vertices)
            newPhysics.Vertices.Add(new GMObject.PhysicsVertex { X = v.X, Y = v.Y });

        return newPhysics;
    }
}
