using System.Text.Json.Serialization;

namespace Portal.Common.Models;

/// <summary>
/// Preset or custom icon selection for the application and the Credential Provider tile.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppIconPreset
{
    /// <summary>
    /// Default primary icon (Officer with blue tie brooch & glowing lock, Image 4).
    /// </summary>
    Primary,

    /// <summary>
    /// Special Ops / Tactical with high collar (Image 1).
    /// </summary>
    Tactical,

    /// <summary>
    /// Casual off-shoulder frills (Image 2).
    /// </summary>
    Casual,

    /// <summary>
    /// Elegance off-shoulder frills with blush (Image 3).
    /// </summary>
    Elegance,

    /// <summary>
    /// Tactical hoodie variant (photo 15).
    /// </summary>
    Hoodie,

    /// <summary>
    /// Alternative tactical compact variant (photo 18).
    /// </summary>
    Alternative,

    /// <summary>
    /// Original classic shield icon.
    /// </summary>
    Classic,

    /// <summary>
    /// Custom image file selected by user from local disk.
    /// </summary>
    Custom
}
