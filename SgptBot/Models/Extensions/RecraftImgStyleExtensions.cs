namespace SgptBot.Models.Extensions;

public static class RecraftImgStyleExtensions
{
    private static readonly Dictionary<RecraftImgStyle, HashSet<RecraftImgSubStyle>> StyleToSubStyles = new()
    {
        {
            RecraftImgStyle.Realistic, [
                RecraftImgSubStyle.BAndW,
                RecraftImgSubStyle.HardFlash,
                RecraftImgSubStyle.Hdr,
                RecraftImgSubStyle.NaturalLight,
                RecraftImgSubStyle.StudioPortrait,
                RecraftImgSubStyle.Enterprise,
                RecraftImgSubStyle.MotionBlur
            ]
        },
        {
            RecraftImgStyle.DigitalIllustration, [
                RecraftImgSubStyle.PixelArt,
                RecraftImgSubStyle.HandDrawn,
                RecraftImgSubStyle.Grain,
                RecraftImgSubStyle.InfantileSketch,
                RecraftImgSubStyle.ArtPoster,
                RecraftImgSubStyle.Handmade3D,
                RecraftImgSubStyle.HandDrawnOutline,
                RecraftImgSubStyle.EngravingColor
            ]
        }
    };

    public static IEnumerable<RecraftImgSubStyle> GetSubStyles(this RecraftImgStyle style)
    {
        return StyleToSubStyles.TryGetValue(style, out var subStyles) 
            ? subStyles 
            : Enumerable.Empty<RecraftImgSubStyle>();
    }

    public static bool IsValidSubStyle(this RecraftImgStyle style, RecraftImgSubStyle subStyle)
    {
        return StyleToSubStyles.TryGetValue(style, out var subStyles) && subStyles.Contains(subStyle);
    }
} 