using System;
using System.Reflection;
using Lithnet.CredentialProvider;

namespace Portal.CredentialProvider;

/// <summary>
/// Helper extensions for styling Credential Provider controls.
/// </summary>
public static class CredentialControlExtensions
{
    /// <summary>
    /// CPFG_STYLE_LINK_AS_BUTTON ({E15F1D41-1180-4E86-8E8E-32386E8D6242})
    /// Instructs Windows LogonUI to style a CPFT_COMMAND_LINK field as a native push button
    /// instead of a default blue text hyperlink.
    /// </summary>
    public static readonly Guid StyleLinkAsButton = new("E15F1D41-1180-4E86-8E8E-32386E8D6242");

    private static readonly FieldInfo? FieldTypeGuidField = typeof(ControlBase).GetField("<FieldTypeGuid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Applies CPFG_STYLE_LINK_AS_BUTTON to the specified control so LogonUI renders it as a button.
    /// </summary>
    public static T AsPushButton<T>(this T control) where T : ControlBase
    {
        FieldTypeGuidField?.SetValue(control, StyleLinkAsButton);
        return control;
    }
}
