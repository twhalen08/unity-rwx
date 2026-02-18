using System;

namespace RWXLoader
{
    public class RWXUnityCommandAdapter
    {
        private readonly RWXMeshBuilder meshBuilder;
        private readonly RWXMaterialManager materialManager;

        public RWXUnityCommandAdapter(RWXMeshBuilder meshBuilder, RWXMaterialManager materialManager)
        {
            this.meshBuilder = meshBuilder;
            this.materialManager = materialManager;
        }

        public void Apply(RWXIntermediateCommand command, RWXParseContext context, Action<string, RWXParseContext> legacyLineApplier)
        {
            if (command == null)
            {
                return;
            }

            // Current bridge implementation: preserve behavior by routing command execution
            // through existing parser logic while transitioning to the intermediate model.
            legacyLineApplier?.Invoke(command.RawLine, context);
        }

        public void FinalizeScene(RWXParseContext context)
        {
            meshBuilder?.FinalCommit(context);
        }
    }
}
