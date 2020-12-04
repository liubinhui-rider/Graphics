using System;
using UnityEngine.Serialization;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition
{
    public class ProbeVolumeAsset : ScriptableObject
    {
        [Serializable]
        internal enum AssetVersion
        {
            First,
            AddProbeVolumesAtlasEncodingModes,
            PV2,
            Max,
            Current = Max - 1
        }

        [SerializeField] protected internal int m_Version = (int)AssetVersion.Current;
        [SerializeField] public int Version { get => m_Version; }

        [SerializeField] public List<ProbeReferenceVolume.Cell> cells = new List<ProbeReferenceVolume.Cell>();

#if UNITY_EDITOR
        internal static string GetFileName(Scene scene)
        {
            string assetName = "ProbeVolumeData";
            
            String scenePath = scene.path;
            String sceneDir = System.IO.Path.GetDirectoryName(scenePath);
            String sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);

            String assetPath = System.IO.Path.Combine(sceneDir, sceneName);

            if (!UnityEditor.AssetDatabase.IsValidFolder(assetPath))
                UnityEditor.AssetDatabase.CreateFolder(sceneDir, sceneName);

            String assetFileName = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(assetName + ".asset");

            assetFileName = System.IO.Path.Combine(assetPath, assetFileName);

            return assetFileName;
        }

        public static ProbeVolumeAsset CreateAsset(Scene scene)
        {
            ProbeVolumeAsset asset = ScriptableObject.CreateInstance<ProbeVolumeAsset>();
            string assetFileName = GetFileName(scene);

            UnityEditor.AssetDatabase.CreateAsset(asset, assetFileName);
            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.AssetDatabase.Refresh();

            return asset;
        }
#endif
    }
}
