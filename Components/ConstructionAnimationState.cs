using Unity.Entities;

namespace ConstructionAnimation.Components
{
    /// <summary>
    /// Reserved for the visual prototype. This component belongs to the mod,
    /// not to the vanilla simulation.
    /// </summary>
    public struct ConstructionAnimationState : IComponentData
    {
        public float Progress;
        public ConstructionStage Stage;
    }

    public enum ConstructionStage : byte
    {
        Groundworks,
        Foundation,
        Structure,
        Exterior,
        Finishing,
        Completed
    }
}
