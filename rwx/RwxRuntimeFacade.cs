using System;
using System.Collections.Generic;
using UnityEngine;

namespace RWXLoader
{
    /// <summary>
    /// Runtime entrypoint for loading RWX models and applying VP actions.
    /// </summary>
    /// <remarks>
    /// <para>Typical usage order:</para>
    /// <list type="number">
    /// <item><description>Create the facade from an existing loader component (for example, <see cref="RWXLoaderAdvanced"/>).</description></item>
    /// <item><description>Call <see cref="LoadFromRemote"/> or <see cref="LoadFromZip"/>.</description></item>
    /// <item><description>Optionally call <see cref="ApplyActions"/> to process VP create/activate actions.</description></item>
    /// <item><description>Call <see cref="ClearCaches"/> when caches need to be reset.</description></item>
    /// </list>
    /// <para>
    /// This facade delegates to existing runtime systems and does not replace parser/material logic.
    /// </para>
    /// </remarks>
    public class RwxRuntimeFacade
    {
        private readonly RWXLoaderAdvanced advancedLoader;
        private readonly RWXLoaderMain mainLoader;

        /// <summary>
        /// Creates a facade backed by an <see cref="RWXLoaderAdvanced"/> instance.
        /// </summary>
        public RwxRuntimeFacade(RWXLoaderAdvanced loader)
        {
            advancedLoader = loader;
        }

        /// <summary>
        /// Creates a facade backed by an <see cref="RWXLoaderMain"/> instance.
        /// </summary>
        public RwxRuntimeFacade(RWXLoaderMain loader)
        {
            mainLoader = loader;
        }

        /// <summary>
        /// Loads a model from a remote VP object path and invokes a completion callback.
        /// </summary>
        /// <param name="modelName">Model name without extension (for example, "couch1a").</param>
        /// <param name="objectPath">Base object path URL.</param>
        /// <param name="password">Optional object path password.</param>
        /// <param name="onComplete">Callback invoked with the loaded model and a status/error message.</param>
        public void LoadFromRemote(string modelName, string objectPath, string password, Action<GameObject, string> onComplete)
        {
            if (advancedLoader == null)
            {
                onComplete?.Invoke(null, "RWXLoaderAdvanced backing instance is required for remote loading.");
                return;
            }

            advancedLoader.LoadModelFromRemoteCore(modelName, objectPath, onComplete, password, activateOnInstantiate: true);
        }

        /// <summary>
        /// Loads a model from a local ZIP archive.
        /// </summary>
        /// <param name="zipPath">Absolute or relative path to the ZIP file.</param>
        /// <param name="modelName">Model name without extension inside the ZIP.</param>
        /// <returns>The loaded model root GameObject, or null when loading fails.</returns>
        public GameObject LoadFromZip(string zipPath, string modelName)
        {
            if (advancedLoader != null)
            {
                return advancedLoader.LoadModelFromZipCore(zipPath, modelName);
            }

            Debug.LogError("LoadFromZip requires RWXLoaderAdvanced backing instance.");
            return null;
        }

        /// <summary>
        /// Parses and applies VP action strings to an existing target.
        /// </summary>
        /// <param name="target">Target GameObject that receives actions.</param>
        /// <param name="action">VP action string containing create/activate commands.</param>
        /// <param name="objectPath">Object path used by texture/material actions.</param>
        /// <param name="password">Object path password used by remote texture/material actions.</param>
        /// <param name="host">Coroutine host used by asynchronous action handlers.</param>
        public void ApplyActions(GameObject target, string action, string objectPath, string password, MonoBehaviour host)
        {
            if (target == null || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            VpActionParser.Parse(action, out List<VpActionCommand> createActions, out List<VpActionCommand> activateActions);

            foreach (var create in createActions)
            {
                VpActionExecutor.ExecuteCreate(target, create, objectPath, password, host);
            }

            if (activateActions.Count > 0)
            {
                var activateComponent = target.GetComponent<VpActivateActions>() ?? target.AddComponent<VpActivateActions>();
                activateComponent.actions.Clear();
                activateComponent.actions.AddRange(activateActions);
            }
        }

        /// <summary>
        /// Clears loader-managed runtime caches (model and texture caches).
        /// </summary>
        public void ClearCaches()
        {
            advancedLoader?.ClearCacheCore();
            mainLoader?.ClearCache();
        }
    }
}
