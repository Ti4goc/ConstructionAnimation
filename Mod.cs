using Game;
using Game.Modding;
using ConstructionAnimation.Systems;

namespace ConstructionAnimation
{
    public sealed class Mod : IMod
    {
        public void OnLoad(UpdateSystem updateSystem)
        {
            ModLog.Initialize();

            ModLog.Info(
                "Construction Animation v0.1 loaded."
            );

            ModLog.Checkpoint(
                "MOD OnLoad; diagnostic logging active; version=V1.43.47.4.3.10"
            );

            // V1.43.47.4.3.10: suppress the vanilla construction Sand Surface
            // during Modification2 so the game's own SubAreaReferencesSystem,
            // which runs in Modification2B, can detach the deleted Area before
            // AreaBatchSystem reaches PreCulling.
            updateSystem.UpdateAt<ConstructionSandSuppressionSystem>(
                SystemUpdatePhase.Modification2
            );

            updateSystem.UpdateAt<ConstructionDetectionSystem>(
                SystemUpdatePhase.ModificationEnd
            );
        }

        public void OnDispose()
        {
            ModLog.Checkpoint(
                "MOD OnDispose begin"
            );

            ModLog.Info(
                "Construction Animation disposed."
            );

            ModLog.Shutdown();
        }
    }
}
