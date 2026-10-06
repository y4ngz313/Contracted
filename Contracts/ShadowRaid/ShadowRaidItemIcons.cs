using System.IO;
using System.Reflection;
using UnityEngine;

namespace Y4NGZCompany.Contracts.ShadowRaid
{
    // Pre-rendered from the authored models; cached sprites need no runtime camera or bundle.
    internal static class ShadowRaidItemIcons
    {
        private static Sprite _drill;
        private static Sprite _drive;
        internal static Sprite Drill => _drill != null ? _drill : (_drill = Load("ShadowRaidDrill"));
        internal static Sprite HardDrive => _drive != null ? _drive : (_drive = Load("BlacksiteHardDrive"));

        private static Sprite Load(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShadowRaidIcons." + name + ".png"))
            {
                if (stream == null)
                    throw new FileNotFoundException("Missing embedded Shadow Raid item icon: " + name);
                using (var bytes = new MemoryStream())
                {
                    stream.CopyTo(bytes);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    texture.name = name + "Icon";
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.filterMode = FilterMode.Bilinear;
                    if (!ImageConversion.LoadImage(texture, bytes.ToArray(), markNonReadable: true))
                    {
                        Object.Destroy(texture);
                        throw new InvalidDataException("Invalid Shadow Raid item icon: " + name);
                    }
                    return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                }
            }
        }
    }
}
