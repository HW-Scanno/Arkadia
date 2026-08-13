using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>Which transform processor path a leaf's configuration selects for a promoted release.</summary>
public enum LeafTransformDispatch
{
    /// <summary>release_folder + folder-oriented transform whose output is a file (e.g. ZIP).</summary>
    FolderFile,
    /// <summary>release_folder + folder-oriented transform whose output is a folder.</summary>
    FolderFolder,
    /// <summary>release_folder configured with a missing/non-folder-oriented transform (config error).</summary>
    FolderConfigError,
    /// <summary>release_shape strategy (disc-image → CHD dispatch).</summary>
    ReleaseShape,
    /// <summary>Default file-oriented path (none / file_extension).</summary>
    FileOriented,
}

/// <summary>
/// Pure resolution of the transform-dispatch decision, extracted so the strategy → processor mapping is
/// unit-testable without instantiating the UI or the processors. This reproduces the historical decision
/// tree exactly; the caller invokes the matching processor.
/// </summary>
public static class LeafTransformDispatchResolver
{
    public static LeafTransformDispatch Decide(string strategyType, TransformRecord? folderTransform)
    {
        if (strategyType == "release_folder")
        {
            if (folderTransform is { IsFolderOriented: true, OutputIsFile: true })   return LeafTransformDispatch.FolderFile;
            if (folderTransform is { IsFolderOriented: true, OutputIsFolder: true }) return LeafTransformDispatch.FolderFolder;
            return LeafTransformDispatch.FolderConfigError;
        }
        if (strategyType == "release_shape") return LeafTransformDispatch.ReleaseShape;
        return LeafTransformDispatch.FileOriented;
    }
}
