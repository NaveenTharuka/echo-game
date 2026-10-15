using UnityEditor;
using UnityEngine;

namespace Echo.EditorTools
{
    /// Import settings for everything under Assets/Audio (guide §5.9), applied automatically on import:
    ///   SFX/ and Agents/ one-shots -> mono (3D), Decompress On Load, Vorbis 70 %
    ///   *_Loop clips, Ambience/, Music/ -> Streaming (Music stays stereo, everything else mono)
    /// To re-apply after changing this file: right-click Assets/Audio > Reimport.
    public class EchoAudioImporter : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith("Assets/Audio/")) return;
            var importer = (AudioImporter)assetImporter;
            bool music = assetPath.StartsWith("Assets/Audio/Music/");
            bool loop = music || assetPath.StartsWith("Assets/Audio/Ambience/") || assetPath.Contains("_Loop");

            importer.forceToMono = !music;
            importer.loadInBackground = loop;
            var s = importer.defaultSampleSettings;
            s.loadType = loop ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 0.7f;
            s.preloadAudioData = !loop;
            importer.defaultSampleSettings = s;
        }
    }
}
