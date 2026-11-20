using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Tandem.App.ViewModels.Viewing;

/// <summary>Turns a picture file's bytes into a bitmap for the viewer. Called off the UI thread.</summary>
public interface IImageDecoder
{
    /// <summary>The picture in <paramref name="stream"/> (read from its current position), scaled down to fit
    /// <paramref name="maxSize"/> × <paramref name="maxSize"/> pixels when it is larger; never scaled up.</summary>
    /// <exception cref="Exception">The bytes are not a picture this decoder knows, or are damaged; the type of the
    /// exception depends on the decoder.</exception>
    Bitmap Decode(Stream stream, int maxSize);
}

/// <summary>
/// The app's decoder: the renderer's own (Skia) codecs. The picture's size is read from its header first, so a small
/// picture is decoded as it is and a large one straight to the reduced size (<see cref="Bitmap.DecodeToWidth"/> or
/// <see cref="Bitmap.DecodeToHeight"/>, which would also enlarge a small one) without the full-size pixels ever
/// being held.
/// </summary>
public sealed class SkiaImageDecoder : IImageDecoder
{
    public static SkiaImageDecoder Instance { get; } = new();

    public Bitmap Decode(Stream stream, int maxSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 1);
        var start = stream.Position;
        int width, height;
        using (var codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false)))
        {
            if (codec is null) throw new InvalidDataException("The bytes are not a picture this app can show.");
            (width, height) = (codec.Info.Width, codec.Info.Height);
        }
        stream.Position = start;
        if (width <= maxSize && height <= maxSize) return new Bitmap(stream);
        return width >= height
            ? Bitmap.DecodeToWidth(stream, maxSize, BitmapInterpolationMode.HighQuality)
            : Bitmap.DecodeToHeight(stream, maxSize, BitmapInterpolationMode.HighQuality);
    }
}
