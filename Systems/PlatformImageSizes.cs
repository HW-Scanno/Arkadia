namespace Arkadia.Systems;

public static class PlatformImageSizes
{
    /// <summary>Logo displayed in the platform list row. MaxWidth=56, MaxHeight=22.</summary>
    public static readonly (int Width, int Height) Logo   = (56, 22);

    /// <summary>Image displayed in the platform detail panel. Width=300, Height=300.</summary>
    public static readonly (int Width, int Height) Detail = (300, 300);

    /// <summary>Width-constrained logo for the detail pane header. MaxWidth=300, height proportional.</summary>
    public static readonly int DetailLogoWidth = 300;

    /// <summary>All fixed-size targets — used by cache generation to enumerate WxH variants.</summary>
    public static (int Width, int Height)[] All => [Logo, Detail];

    /// <summary>All width-constrained targets — used by cache generation to enumerate w{W} variants.</summary>
    public static int[] AllWidthConstrained => [DetailLogoWidth];
}
