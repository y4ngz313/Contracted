using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>
    /// #1197. Contracted's copy of LethalCCTV's <c>OutlineRuntimeAssets</c>: resolves the outline
    /// shaders from <c>runtime-assets/y4ngz-outlineeffect.lethalbundle</c>, which this package ships
    /// under its own file name.
    ///
    /// <para>The same bundle may already be loaded by LethalCCTV (or Y4NGZUpgrades), and Unity refuses
    /// a second <c>LoadFromFile</c> of a loaded bundle whatever the path or file name. So every
    /// loaded bundle is probed first and the file beside this assembly is only loaded when no loaded
    /// bundle has the shader. LethalCCTV probes loaded bundles the same way, so either load order
    /// works. A bundle this class did not load is never unloaded, and the one it does load stays
    /// loaded for the session, as LethalCCTV's does.</para>
    /// </summary>
    internal static class ObjectiveOutlineAssets
    {
        // NOT ".lethalbundle": LethalLevelLoader auto-loads every *.lethalbundle in the plugins
        // folder, which double-loads the bundle and hard-fails the game load. And not LethalCCTV's
        // "y4ngz-outlineeffect.bundle" either: Contracted's deploy retires that name from its own
        // folder, and a distinct name keeps the two packages' payloads apart.
        internal const string BundleName = "contracted-outlineeffect.bundle";

        internal const string BufferShaderName = "OutlineBufferShader";
        internal const string OverlayShaderName = "Y4NGZOutlineOverlayShader";

        private static readonly Dictionary<string, Shader> ShaderCache = new Dictionary<string, Shader>(StringComparer.Ordinal);
        private static AssetBundle _bundle;
        private static bool _loadAttempted;

        /// <summary>Why the bundle beside the assembly did not load, for the caller's one warning;
        /// null while it loaded or was never needed. This class logs nothing itself.</summary>
        internal static string BundleFailure { get; private set; }

        /// <summary>Both shaders the HDRP composite needs, or false. Silent: the caller logs once.</summary>
        internal static bool TryResolveShaders(out Shader buffer, out Shader overlay)
        {
            buffer = LoadShader(BufferShaderName);
            overlay = LoadShader(OverlayShaderName);
            return buffer != null && overlay != null;
        }

        internal static Shader LoadShader(string shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
                return null;

            if (ShaderCache.TryGetValue(shaderName, out Shader cached) && cached != null)
                return cached;

            Shader shader = Resources.Load<Shader>(shaderName);
            if (shader == null)
                shader = LoadShaderFromLoadedBundles(shaderName);
            if (shader == null)
                shader = LoadShaderFromOwnBundle(shaderName);

            if (shader != null)
                ShaderCache[shaderName] = shader;
            return shader;
        }

        private static Shader LoadShaderFromLoadedBundles(string shaderName)
        {
            foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
            {
                // A streamed scene bundle (every LethalLevelLoader moon) refuses LoadAsset.
                if (loaded == null || loaded.isStreamedSceneAssetBundle)
                    continue;

                Shader shader = loaded.LoadAsset<Shader>(shaderName);
                if (shader != null)
                    return shader;
            }

            return null;
        }

        private static Shader LoadShaderFromOwnBundle(string shaderName)
        {
            EnsureBundleLoaded();
            return _bundle != null ? _bundle.LoadAsset<Shader>(shaderName) : null;
        }

        private static void EnsureBundleLoaded()
        {
            if (_loadAttempted)
                return;

            _loadAttempted = true;
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bundlePath = Path.Combine(dllDir ?? string.Empty, BundleName);
            if (!File.Exists(bundlePath))
            {
                BundleFailure = $"{bundlePath} is missing";
                return;
            }

            _bundle = AssetBundle.LoadFromFile(bundlePath);
            if (_bundle == null)
                BundleFailure = $"{bundlePath} failed to load";
        }
    }
}
