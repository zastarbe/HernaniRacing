using UnityEditor;
using UnityEngine;

namespace HernaniRacing.EditorTools
{
    /// <summary>
    /// Ajustes de importación de la ortofoto (Resources/HernaniOrtho/orto_X_Z.jpg, 2048 x 2048 px = 1 km):
    /// compresión «crunch» (ocupa poco en el build y en la descarga web, y se descomprime muy rápido al cargar),
    /// mipmaps, bordes sin repetir (Clamp) y sin copia legible en memoria.
    /// La primera importación de las 70 imágenes tarda unos minutos (y otra vez al cambiar a la plataforma Web).
    /// </summary>
    public class HernaniOrthoImporter : AssetPostprocessor
    {
        public override uint GetVersion() { return 1; }

        void OnPreprocessTexture()
        {
            string p = assetPath.Replace('\\', '/');
            if (!p.Contains("/Resources/HernaniOrtho/")) return;
            var ti = assetImporter as TextureImporter;
            if (ti == null) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.isReadable = false;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.crunchedCompression = true;
            ti.compressionQuality = 50;
        }
    }
}
