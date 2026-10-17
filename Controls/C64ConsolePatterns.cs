using System;

namespace Arkadia.Controls;

/// <summary>
/// Pure geometry/phase rules of the procedural console animations (no drawing types, no allocation): every pattern is
/// a deterministic function of the SHARED animation frame, so any number of controls at any moment show the same
/// picture, and each rule is unit-testable. The console only translates these numbers into rectangles.
/// Strips are the 6 px bands above and below the "screen" (the console's border).
/// </summary>
public static class C64ConsolePatterns
{
    // ── Block flow (transfer): 4 px blocks, 8 px pitch, drifting left → right at 1 px per frame ──────────────────
    public const int FlowPeriod = 8, FlowBlock = 4;

    /// <summary>X shift of the block row (0..FlowPeriod-1); the bottom strip is staggered by half a period.</summary>
    public static int FlowOffset(long frame, int strip) => (int)((frame + strip * (FlowPeriod / 2)) % FlowPeriod);

    // ── Columns (incoming data): 2 px columns on a 3 px pitch, heights 1..6 re-rolled every 3 frames ──────────────
    public const int ColumnPitch = 3, ColumnWidth = 2, ColumnMaxHeight = 6, ColumnHoldFrames = 3;

    public static int ColumnCount(int width) => Math.Max(0, width / ColumnPitch);

    public static int ColumnHeight(int index, long frame, int strip)
        => 1 + (int)(Hash(index + strip * 1009, frame / ColumnHoldFrames) % ColumnMaxHeight);

    // ── Hex stream (terminal characters): 3x5 pixel glyphs on a 4 px pitch, each re-rolled every 6 frames, staggered ─
    public const int HexPitch = 4, HexCols = 3, HexRowsPerGlyph = 5, HexHoldFrames = 6;

    public static int HexCount(int width) => Math.Max(0, width / HexPitch);

    public static int HexValue(int index, long frame, int strip)
        => (int)(Hash(index + strip * 7919, (frame + index * 5) / HexHoldFrames) % 16);

    // 16 glyphs (0-9, A-F) × 5 rows, 3 bits per row (bit 2 = leftmost pixel).
    private static readonly byte[] HexGlyphs =
    {
        7,5,5,5,7,  2,6,2,2,7,  7,1,7,4,7,  7,1,7,1,7,  5,5,7,1,1,  7,4,7,1,7,  7,4,7,5,7,  7,1,1,1,1,
        7,5,7,5,7,  7,5,7,1,7,  7,5,7,5,5,  6,5,6,5,6,  7,4,4,4,7,  6,5,5,5,6,  7,4,7,4,7,  7,4,7,4,4,
    };

    public static bool HexPixel(int value, int col, int row)
        => (HexGlyphs[(value & 15) * HexRowsPerGlyph + row] & (1 << (HexCols - 1 - col))) != 0;

    // ── Scanner (verification): a 3-cell highlight ping-pongs across a row of 4 px blocks on a 6 px pitch ─────────
    public const int ScanPitch = 6, ScanBlock = 4;

    public static int ScanCount(int width) => Math.Max(0, width / ScanPitch);

    /// <summary>Head position (0..count-1) and travel direction (+1 right, -1 left) at a frame.</summary>
    public static (int Position, int Direction) ScanHead(long frame, int count)
    {
        if (count <= 1) return (0, 1);
        long period = 2L * (count - 1);
        long t = frame % period;
        return t < count ? ((int)t, 1) : ((int)(period - t), -1);
    }

    /// <summary>0 = idle block, 1 and 2 = fading trail, 3 = head.</summary>
    public static int ScanLevel(int index, int head, int direction)
    {
        int behind = (head - index) * direction;     // 0 at the head, 1 and 2 right behind it
        return behind switch { 0 => 3, 1 => 2, 2 => 1, _ => 0 };
    }

    // ── Sweep (cleaning): 2x2 px "dirt" cells in the 6 px strip (3 rows); a 2-cell bar sweeps across and clears them ──
    public const int SweepCell = 2, SweepRows = 3, SweepStepCells = 2, SweepPauseFrames = 8, SweepDirtPercent = 55;

    public static int SweepCols(int width) => Math.Max(0, width / SweepCell);

    /// <summary>Frames of one full pass: the bar crosses all columns, then the clean strip rests briefly.</summary>
    public static long SweepPeriod(int count) => (count + SweepStepCells - 1) / SweepStepCells + 2 + SweepPauseFrames;

    /// <summary>0 = clear, 1 = dirt, 2 = the sweeping bar. Top strip sweeps left → right, bottom strip right → left.</summary>
    public static int SweepLevel(int col, int row, long frame, int count, int strip)
    {
        if (count <= 0) return 0;
        int i = strip == 0 ? col : count - 1 - col;                       // position along the sweep direction
        long period = SweepPeriod(count);
        long cycle  = frame / period;
        int  head   = (int)(frame % period) * SweepStepCells;               // leading edge of the bar, in cells
        if (i >= head - SweepStepCells && i <= head) return 2;              // the bar (2 cells wide), full height
        if (i < head) return 0;                                             // already swept
        return Hash(i * 31 + row * 7 + strip * 1013, cycle) % 100 < SweepDirtPercent ? 1 : 0;
    }

    // ── Lava (purging): 2x2 px cells; 5 heat levels from a few moving waves (flowing left → right) ─────────────────
    public const int LavaCell = 2, LavaHeight = 10, LavaRows = LavaHeight / LavaCell, LavaLevels = 5;

    public static int LavaCols(int width) => Math.Max(0, width / LavaCell);

    /// <summary>Heat 0 (dark red) … 4 (hot orange/yellow) of a cell at a frame — smooth, deterministic, flowing.</summary>
    public static int LavaLevel(int col, int row, long frame)
    {
        double t = frame;
        double v = Math.Sin(col * 0.21 - t * 0.13)
                 + Math.Sin(col * 0.09 - t * 0.07 + row * 0.9)
                 + Math.Sin(row * 1.4 + t * 0.10 + col * 0.04);
        v = v / 3.0 - (row == 0 ? 0.12 : 0) + (row == LavaRows - 1 ? 0.08 : 0);    // cooler crust on top, hotter below
        return v < -0.40 ? 0 : v < -0.05 ? 1 : v < 0.30 ? 2 : v < 0.60 ? 3 : 4;
    }

    // ── Deterministic hash ───────────────────────────────────────────────────────────────────────────────────────
    public static uint Hash(int a, long b)
    {
        unchecked
        {
            uint h = (uint)a * 2654435761u ^ (uint)b * 40503u ^ (uint)(b >> 32) * 97u;
            h ^= h >> 15; h *= 2246822519u;
            h ^= h >> 13; h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }
}
