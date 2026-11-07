using Avalonia.Media.Imaging;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.App.Services;

/// <summary>Loads <c>logo-ar.png</c> once for cashier + customer chrome.</summary>
public static class BrandLogo
{
    private static Bitmap? _bitmap;
    private static bool _attempted;

    public static Bitmap? Bitmap
    {
        get
        {
            if (_attempted)
                return _bitmap;
            _attempted = true;
            try
            {
                PosPaths.EnsureUiAssets();
                var path = PosPaths.LogoArFile();
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    return null;
                using var stream = File.OpenRead(path);
                _bitmap = new Bitmap(stream);
                return _bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static bool IsAvailable => Bitmap is not null;
}
