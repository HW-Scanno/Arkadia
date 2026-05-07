using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Arkadia.Library;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arkadia;

/// <summary>Returned by EditMetadataDialog on a successful save.</summary>
public sealed class EditMetadataResult
{
    public required ReleaseMetadataRecord Metadata     { get; init; }
    /// <summary>The (possibly edited) region string from the releases table.</summary>
    public required string                Region       { get; init; }
    /// <summary>Canonical metadata field names whose value was changed by the user.</summary>
    public required HashSet<string>       ChangedFields { get; init; }
}

public partial class EditMetadataDialog : Window
{
    private readonly LibraryEntry          _entry;
    private readonly ReleaseMetadataRecord _original;
    private readonly string                _originalRegion;
    private readonly Dictionary<string, MetadataFieldStateRecord> _originalStates;
    private readonly IReadOnlyList<MetadataValueMappingRecord>    _mappings;

    public EditMetadataDialog()
    {
        InitializeComponent();
        _entry          = null!;
        _original       = null!;
        _originalRegion = null!;
        _originalStates = new(StringComparer.Ordinal);
        _mappings       = [];
    }

    public EditMetadataDialog(
        LibraryEntry entry,
        IReadOnlyList<MetadataValueMappingRecord>? mappings = null)
    {
        InitializeComponent();

        _entry          = entry;
        _original       = entry.Metadata ?? new ReleaseMetadataRecord { ReleaseId = entry.ReleaseId };
        _originalRegion = entry.Region;
        _mappings       = mappings ?? [];

        var store = new DatLineStore(entry.DbPath);
        _originalStates = store.LoadMetadataFieldStates(entry.ReleaseId)
                               .ToDictionary(s => s.Field, StringComparer.Ordinal);

        SubtitleLabel.Text = entry.Name;

        FieldTitle.Text           = _original.Title;
        FieldOriginalTitle.Text   = _original.OriginalTitle;
        FieldSortTitle.Text       = _original.SortTitle;
        FieldDeveloper.Text       = _original.Developer;
        FieldPublisher.Text       = _original.Publisher;
        FieldYear.Text            = _original.Year;
        FieldRegion.Text          = _originalRegion;
        FieldLanguages.Text       = _original.Languages;
        FieldAlternateTitles.Text = _original.AlternateTitles;
        FieldGenre.Text           = _original.Genre;
        FieldSubgenre.Text        = _original.Subgenre;
        FieldPlayers.Text         = _original.Players;
        FieldReleaseType.Text     = _original.ReleaseType;
        FieldRating.Text          = _original.Rating;
        FieldDescription.Text     = _original.Description;
        FieldNotes.Text           = _original.Notes;

        // Read-only DAT fields
        FieldFormat.Text = entry.Format;
        FieldSize.Text   = entry.Size;

        // Initialise lock checkboxes from persisted field state
        _originalStates.TryGetValue("title",            out var ts);  LockTitle.IsChecked           = ts?.Locked  ?? false;
        _originalStates.TryGetValue("original_title",   out var ots); LockOriginalTitle.IsChecked   = ots?.Locked ?? false;
        _originalStates.TryGetValue("sort_title",       out var sts); LockSortTitle.IsChecked       = sts?.Locked ?? false;
        _originalStates.TryGetValue("developer",        out var ds);  LockDeveloper.IsChecked       = ds?.Locked  ?? false;
        _originalStates.TryGetValue("publisher",        out var pbs); LockPublisher.IsChecked       = pbs?.Locked ?? false;
        _originalStates.TryGetValue("year",             out var ys);  LockYear.IsChecked            = ys?.Locked  ?? false;
        _originalStates.TryGetValue("region",           out var rs);  LockRegion.IsChecked          = rs?.Locked  ?? false;
        _originalStates.TryGetValue("languages",        out var ls);  LockLanguages.IsChecked       = ls?.Locked  ?? false;
        _originalStates.TryGetValue("alternate_titles", out var ats); LockAlternateTitles.IsChecked = ats?.Locked ?? false;
        _originalStates.TryGetValue("genre",            out var gs);  LockGenre.IsChecked           = gs?.Locked  ?? false;
        _originalStates.TryGetValue("subgenre",         out var sgs); LockSubgenre.IsChecked        = sgs?.Locked ?? false;
        _originalStates.TryGetValue("players",          out var pls); LockPlayers.IsChecked         = pls?.Locked ?? false;
        _originalStates.TryGetValue("release_type",     out var rts); LockReleaseType.IsChecked     = rts?.Locked ?? false;
        _originalStates.TryGetValue("rating",           out var ras); LockRating.IsChecked          = ras?.Locked ?? false;
        _originalStates.TryGetValue("description",      out var dss); LockDescription.IsChecked     = dss?.Locked ?? false;
        _originalStates.TryGetValue("notes",            out var ns);  LockNotes.IsChecked           = ns?.Locked  ?? false;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // Normalise controlled-vocabulary fields before saving
        string N(string field, TextBox box) =>
            MetadataValueNormalizer.Normalize(field, Trim(box), _mappings);

        var newMeta = new ReleaseMetadataRecord
        {
            ReleaseId       = _entry.ReleaseId,
            Title           = Trim(FieldTitle),
            OriginalTitle   = Trim(FieldOriginalTitle),
            SortTitle       = Trim(FieldSortTitle),
            Developer       = Trim(FieldDeveloper),
            Publisher       = Trim(FieldPublisher),
            Year            = Trim(FieldYear),
            Languages       = Trim(FieldLanguages),
            AlternateTitles = Trim(FieldAlternateTitles),
            Description     = Trim(FieldDescription),
            Genre           = N("genre",        FieldGenre),
            Subgenre        = N("subgenre",      FieldSubgenre),
            Players         = N("players",       FieldPlayers),
            ReleaseType     = N("release_type",  FieldReleaseType),
            Rating          = N("rating",        FieldRating),
            Notes           = Trim(FieldNotes),
            ScrapedAtUtc    = _original.ScrapedAtUtc,
        };

        var newRegion = N("region", FieldRegion);
        var lockMap   = BuildLockMap();

        var store = new DatLineStore(_entry.DbPath);
        store.SaveReleaseMetadata(newMeta);

        if (!string.Equals(newRegion, _originalRegion, StringComparison.Ordinal))
            store.UpdateReleaseRegion(_entry.ReleaseId, newRegion);

        // Record field state for all changed fields using per-field lock choice
        var changed = BuildChangedFields(newMeta, newRegion);
        foreach (var field in changed)
            store.SaveMetadataFieldState(_entry.ReleaseId, field, "manual", "", locked: lockMap[field]);

        // Handle lock-only changes (value unchanged but lock checkbox was toggled)
        foreach (var (field, locked) in lockMap)
        {
            if (changed.Contains(field)) continue;

            _originalStates.TryGetValue(field, out var existing);
            var wasLocked = existing?.Locked ?? false;
            if (locked == wasLocked) continue;

            if (existing is not null)
            {
                store.SaveMetadataFieldState(_entry.ReleaseId, field, existing.Source, existing.Provider, locked: locked);
            }
            else
            {
                var val = GetFieldValue(field, newMeta, newRegion);
                store.SaveMetadataFieldState(_entry.ReleaseId, field, val.Length > 0 ? "manual" : "", "", locked: locked);
            }
        }

        Close(new EditMetadataResult
        {
            Metadata      = newMeta,
            Region        = newRegion,
            ChangedFields = changed,
        });
    }

    private Dictionary<string, bool> BuildLockMap() => new(StringComparer.Ordinal)
    {
        ["title"]            = LockTitle.IsChecked           == true,
        ["original_title"]   = LockOriginalTitle.IsChecked   == true,
        ["sort_title"]       = LockSortTitle.IsChecked       == true,
        ["developer"]        = LockDeveloper.IsChecked       == true,
        ["publisher"]        = LockPublisher.IsChecked       == true,
        ["year"]             = LockYear.IsChecked            == true,
        ["region"]           = LockRegion.IsChecked          == true,
        ["languages"]        = LockLanguages.IsChecked       == true,
        ["alternate_titles"] = LockAlternateTitles.IsChecked == true,
        ["genre"]            = LockGenre.IsChecked           == true,
        ["subgenre"]         = LockSubgenre.IsChecked        == true,
        ["players"]          = LockPlayers.IsChecked         == true,
        ["release_type"]     = LockReleaseType.IsChecked     == true,
        ["rating"]           = LockRating.IsChecked          == true,
        ["description"]      = LockDescription.IsChecked     == true,
        ["notes"]            = LockNotes.IsChecked           == true,
    };

    private HashSet<string> BuildChangedFields(ReleaseMetadataRecord newMeta, string newRegion)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);

        Check("title",            _original.Title,           newMeta.Title);
        Check("original_title",   _original.OriginalTitle,   newMeta.OriginalTitle);
        Check("sort_title",       _original.SortTitle,       newMeta.SortTitle);
        Check("developer",        _original.Developer,       newMeta.Developer);
        Check("publisher",        _original.Publisher,       newMeta.Publisher);
        Check("year",             _original.Year,            newMeta.Year);
        Check("languages",        _original.Languages,       newMeta.Languages);
        Check("alternate_titles", _original.AlternateTitles, newMeta.AlternateTitles);
        Check("description",      _original.Description,     newMeta.Description);
        Check("genre",            _original.Genre,           newMeta.Genre);
        Check("subgenre",         _original.Subgenre,        newMeta.Subgenre);
        Check("players",          _original.Players,         newMeta.Players);
        Check("release_type",     _original.ReleaseType,     newMeta.ReleaseType);
        Check("rating",           _original.Rating,          newMeta.Rating);
        Check("notes",            _original.Notes,           newMeta.Notes);

        if (!string.Equals(newRegion, _originalRegion, StringComparison.Ordinal))
            changed.Add("region");

        return changed;

        void Check(string name, string oldVal, string newVal)
        {
            if (!string.Equals(oldVal, newVal, StringComparison.Ordinal))
                changed.Add(name);
        }
    }

    internal static string GetFieldValue(string field, ReleaseMetadataRecord meta, string region) => field switch
    {
        "title"            => meta.Title,
        "original_title"   => meta.OriginalTitle,
        "sort_title"       => meta.SortTitle,
        "developer"        => meta.Developer,
        "publisher"        => meta.Publisher,
        "year"             => meta.Year,
        "languages"        => meta.Languages,
        "alternate_titles" => meta.AlternateTitles,
        "description"      => meta.Description,
        "genre"            => meta.Genre,
        "subgenre"         => meta.Subgenre,
        "players"          => meta.Players,
        "release_type"     => meta.ReleaseType,
        "rating"           => meta.Rating,
        "notes"            => meta.Notes,
        "region"           => region,
        _                  => "",
    };

    private static string Trim(TextBox box) => box.Text?.Trim() ?? "";
}
