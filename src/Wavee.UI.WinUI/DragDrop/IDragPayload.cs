namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Generic drag payload contract. Implement for each draggable content type.
/// </summary>
public interface IDragPayload
{
    /// <summary>
    /// Custom data format key for DataPackage (e.g., "WaveeTrackIds").
    /// </summary>
    string DataFormat { get; }

    /// <summary>
    /// Serialized data to store in DataPackage.
    /// </summary>
    string SerializedData { get; }

    /// <summary>
    /// Number of items being dragged.
    /// </summary>
    int ItemCount { get; }
}
