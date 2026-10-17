using System;
using Avalonia.Controls;

namespace Arkadia.Ingestion;

/// <summary>
/// The Import DAT dialog's identity wiring, kept apart from the Window so it can be exercised with real
/// <see cref="TextBox"/> instances (a Window needs a windowing platform; a TextBox does not).
///
/// <para><b>Why programmatic writes are not user edits.</b> Avalonia 11.3 delivers <c>TextBox.TextChanged</c>
/// <i>deferred</i> through the dispatcher, i.e. AFTER the code that assigned <c>Text</c> has returned. A
/// "currently syncing" boolean around the assignment is therefore already false when the event arrives, and the
/// programmatic suggestion used to be recorded as a manual edit (freezing the Display Name at its first value).
/// The rule here does not depend on timing: a change notification is a user edit only if the box's text differs
/// from what the form currently proposes. A programmatic write always makes the box equal the form, so its
/// (late) notification is a no-op — however many writes were queued.</para>
/// </summary>
public sealed class SingleDatImportIdentityBinding
{
    private readonly SingleDatImportForm _form;
    private readonly TextBox             _variantBox;
    private readonly TextBox             _idBox;
    private readonly TextBox             _displayNameBox;

    public SingleDatImportIdentityBinding(
        SingleDatImportForm form, TextBox variantBox, TextBox idBox, TextBox displayNameBox)
    {
        _form           = form           ?? throw new ArgumentNullException(nameof(form));
        _variantBox     = variantBox     ?? throw new ArgumentNullException(nameof(variantBox));
        _idBox          = idBox          ?? throw new ArgumentNullException(nameof(idBox));
        _displayNameBox = displayNameBox ?? throw new ArgumentNullException(nameof(displayNameBox));
    }

    /// <summary>Pushes the CURRENT combo selections into the form and re-renders the proposed id / display name.</summary>
    public void OnSelectionChanged(string? systemId, string? authorityId, string? authorityName, string? mediaTypeId)
    {
        _form.SelectSystem(systemId);
        _form.SelectAuthority(authorityId, authorityName);
        _form.SelectMediaType(mediaTypeId);
        Render();
    }

    /// <summary>Variant box changed (user typing OR the late notification of a programmatic write): verbatim into the form.</summary>
    public void OnVariantTextChanged()
    {
        _form.SetVariant(_variantBox.Text);
        Render();
    }

    /// <summary>Display-name box changed: a user edit only when the text differs from the form's current proposal.</summary>
    public void OnDisplayNameTextChanged() => _form.NotifyDisplayNameText(_displayNameBox.Text);

    /// <summary>Writes the form's outputs into the boxes. Never marks anything as user-edited.</summary>
    public void Render()
    {
        _idBox.Text = _form.PreviewId;
        // The display-name box follows the suggestion only until the user has edited it.
        if (!_form.DisplayNameEdited && _displayNameBox.Text != _form.DisplayName)
            _displayNameBox.Text = _form.DisplayName;
    }
}
