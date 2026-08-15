using Game;
using Game.Modding;
using ConstructionAnimation.Systems;

namespace ConstructionAnimation
{
    public sealed class Mod : IMod
    {
        public void OnLoad(UpdateSystem updateSystem)
        {
            ModLog.Info(
                "Construction Animation v0.1 loaded."
            );

            updateSystem.UpdateAt<ConstructionDetectionSystem>(
                SystemUpdatePhase.ModificationEnd
            );
        }

        public void OnDispose()
        {
            ModLog.Info(
                "Construction Animation disposed."
            );
        }
    }
}