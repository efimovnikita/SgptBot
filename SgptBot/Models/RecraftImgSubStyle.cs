using System.ComponentModel;

namespace SgptBot.Models;

public enum RecraftImgSubStyle
{
    [Description("none")]
    None,
    
    [Description("b_and_w")]
    BAndW,
    
    [Description("hard_flash")]
    HardFlash,
    
    [Description("hdr")]
    Hdr,
    
    [Description("natural_light")]
    NaturalLight,
    
    [Description("studio_portrait")]
    StudioPortrait,
    
    [Description("enterprise")]
    Enterprise,
    
    [Description("motion_blur")]
    MotionBlur,
    
    [Description("pixel_art")]
    PixelArt,
    
    [Description("hand_drawn")]
    HandDrawn,
    
    [Description("grain")]
    Grain,
    
    [Description("infantile_sketch")]
    InfantileSketch,
    
    [Description("2d_art_poster")]
    ArtPoster,
    
    [Description("handmade_3d")]
    Handmade3D,
    
    [Description("hand_drawn_outline")]
    HandDrawnOutline,
    
    [Description("engraving_color")]
    EngravingColor,
}