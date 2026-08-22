namespace Arkadia;

/// <summary>
/// The exact progress surface the single-DAT "Verify" executor (<c>RunVerifyAllDatLine</c>) reports to. Extracted
/// so the SAME executor drives both the Single-selection Verify (its concrete <see cref="DatLineVerifyDialog"/>
/// implements this — behaviour byte-for-byte unchanged) and the sequential Group Verify (a per-leaf adapter that
/// forwards into the Group progress dialog). No new verification engine — only the progress sink is abstracted.
/// </summary>
public interface IDatLineVerifyProgress
{
    void SetStatus(string text);
    void AppendRow(string volume, string result, string path, string detail);
    void UpdateStats(int totalVols, int verifiedVols, int skippedVols,
                     int totalExpected, int totalVerified, int totalMissing, int totalMismatch);
    void SetCompleted(string summary);
}
