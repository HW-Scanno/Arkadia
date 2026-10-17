using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;

namespace Arkadia.Ingestion;

/// <summary>
/// UI-free state and validation of the Import DAT dialog's identity section. The window only forwards
/// selections/keystrokes here and renders the outputs, so the id rules live exclusively in
/// <see cref="SingleDatIdBuilder"/> (no duplicated validation in the UI) and the behaviour is unit-testable.
///
/// <para>The proposed id is always <c>{system}-{authority}-{media}-{variant}</c> from the CURRENT selections.
/// The Display Name is independent of the id: it is auto-suggested from authority + variant until the user edits
/// it, after which selection/variant changes never overwrite it. Invalid input is reported, never rewritten.</para>
/// </summary>
public sealed class SingleDatImportForm
{
    private readonly IReadOnlyCollection<string> _existingIds;
    private string? _systemId, _authorityId, _authorityName, _mediaTypeId;

    public SingleDatImportForm(IEnumerable<string> existingIds)
    {
        _existingIds = (existingIds ?? throw new ArgumentNullException(nameof(existingIds))).ToList();
        Variant      = SingleDatIdBuilder.DefaultVariant;
        DisplayName  = DatLineDisplayName.Suggest(null, Variant);
    }

    /// <summary>The variant exactly as typed (default <c>main</c>). Never normalized.</summary>
    public string Variant { get; private set; }

    /// <summary>The display name currently shown (suggested or user-entered).</summary>
    public string DisplayName { get; private set; }

    /// <summary>True once the user edited the display name; from then on it is never auto-replaced.</summary>
    public bool DisplayNameEdited { get; private set; }

    public string? SystemId    => _systemId;
    public string? AuthorityId => _authorityId;
    public string? MediaTypeId => _mediaTypeId;

    public void SelectSystem(string? systemId)                        { _systemId = systemId;       Refresh(); }
    public void SelectAuthority(string? authorityId, string? name)    { _authorityId = authorityId; _authorityName = name; Refresh(); }
    public void SelectMediaType(string? mediaTypeId)                  { _mediaTypeId = mediaTypeId; Refresh(); }

    public void SetVariant(string? variant)
    {
        Variant = variant ?? "";
        Refresh();
    }

    /// <summary>Records a user edit of the display name; it is preserved across later selection/variant changes.</summary>
    public void SetDisplayNameFromUser(string? text)
    {
        DisplayName       = text ?? "";
        DisplayNameEdited = true;
    }

    /// <summary>
    /// Handles a display-name text-change notification from the UI. The text is a USER edit only if it differs from
    /// what the form currently holds; identical text is the echo of a programmatic write (UI frameworks may deliver
    /// it late) and is ignored. Returns true when it was recorded as a user edit.
    /// </summary>
    public bool NotifyDisplayNameText(string? text)
    {
        if (string.Equals(text ?? "", DisplayName, StringComparison.Ordinal))
            return false;
        SetDisplayNameFromUser(text);
        return true;
    }

    /// <summary>True when the system, authority and media type are all selected.</summary>
    public bool SelectionComplete => !string.IsNullOrEmpty(_systemId) && !string.IsNullOrEmpty(_authorityId) && !string.IsNullOrEmpty(_mediaTypeId);

    /// <summary>Result of the shared builder, or null while the selection is incomplete.</summary>
    public SingleDatIdResult? IdResult => SelectionComplete
        ? SingleDatIdBuilder.Build(_systemId, _authorityId, _mediaTypeId, Variant, _existingIds)
        : null;

    /// <summary>Read-only preview of the full id: the valid id, else the attempted composition, else empty.</summary>
    public string PreviewId
    {
        get
        {
            if (!SelectionComplete) return "";
            var r = IdResult!;
            return r.IsValid ? r.Id! : $"{_systemId}-{_authorityId}-{_mediaTypeId}-{Variant}";
        }
    }

    /// <summary>The valid id to import, or null when anything is invalid.</summary>
    public string? FinalId => IdResult is { IsValid: true } r ? r.Id : null;

    public string? DisplayNameError => DatLineDisplayName.Validate(DisplayName);

    /// <summary>The first blocking problem to show the user, or null when the identity section is valid.</summary>
    public string? FirstError
    {
        get
        {
            if (!SelectionComplete) return "Select a system, an authority and a media type.";
            var r = IdResult!;
            if (!r.IsValid) return r.Message;
            return DisplayNameError;
        }
    }

    public bool IsValid => FirstError is null;

    private void Refresh()
    {
        if (!DisplayNameEdited)
            DisplayName = DatLineDisplayName.Suggest(_authorityName, Variant);
    }
}
