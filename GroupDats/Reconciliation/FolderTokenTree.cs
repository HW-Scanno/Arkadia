using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data.Identifiers;

namespace Arkadia.GroupDats;

/// <summary>
/// One folder node in the source hierarchy with an editable technical <see cref="Token"/> that
/// contributes to proposed new-leaf ids. An empty token excludes the folder from id proposals but
/// never removes the node or hides its DATs.
/// </summary>
public sealed class FolderTokenNode
{
    /// <summary>Display segment ("" for the synthetic root).</summary>
    public string Name { get; }
    /// <summary>Normalized relative folder path ("" for the root, else "a/b").</summary>
    public string RelativeFolderPath { get; }
    public bool   IsRoot { get; }
    /// <summary>Editable token (root starts empty; subfolders start suggested).</summary>
    public string Token { get; set; }
    public List<FolderTokenNode> Children { get; } = new();

    internal FolderTokenNode(string name, string relativeFolderPath, bool isRoot, string token)
    {
        Name = name; RelativeFolderPath = relativeFolderPath; IsRoot = isRoot; Token = token;
    }
}

/// <summary>
/// In-memory folder tree built from the Phase-3A relative paths (no DB table). Provides the ordered
/// non-empty folder tokens for any DAT so ids can be proposed. Editing a folder token affects only
/// the proposals of descendant new leaves; existing leaves and relative paths are never touched.
/// </summary>
public sealed class FolderTokenTree
{
    public FolderTokenNode Root { get; }

    // Fast lookup of a node by its normalized folder path.
    private readonly Dictionary<string, FolderTokenNode> _byPath = new(StringComparer.Ordinal);

    private FolderTokenTree(FolderTokenNode root) { Root = root; }

    /// <summary>Builds the tree from candidate relative file paths (e.g. "Commodore/C64/Games.dat").</summary>
    public static FolderTokenTree Build(IEnumerable<string> relativeFilePaths, string rootDisplayName = "(root)")
    {
        var root = new FolderTokenNode(rootDisplayName, "", isRoot: true, token: "");
        var tree = new FolderTokenTree(root);
        tree._byPath[""] = root;

        foreach (var filePath in relativeFilePaths)
        {
            var segments = filePath.Split('/');
            // Last segment is the file name → folders are segments[0..^1].
            var current = root;
            var pathSoFar = "";
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                pathSoFar = pathSoFar.Length == 0 ? seg : pathSoFar + "/" + seg;
                if (!tree._byPath.TryGetValue(pathSoFar, out var node))
                {
                    node = new FolderTokenNode(seg, pathSoFar, isRoot: false,
                        token: SuggestFolderToken(seg));
                    tree._byPath[pathSoFar] = node;
                    current.Children.Add(node);
                }
                current = node;
            }
        }
        return tree;
    }

    /// <summary>The node for a folder path, or null.</summary>
    public FolderTokenNode? NodeForFolder(string relativeFolderPath) =>
        _byPath.GetValueOrDefault(relativeFolderPath);

    /// <summary>Ordered non-empty folder tokens from root down to the folder containing this file.</summary>
    public IReadOnlyList<string> FolderTokensForFile(string relativeFilePath)
    {
        var segments = relativeFilePath.Split('/');
        var tokens = new List<string>();
        var pathSoFar = "";
        // include root token first if present
        if (!string.IsNullOrEmpty(Root.Token)) tokens.Add(Root.Token);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            pathSoFar = pathSoFar.Length == 0 ? segments[i] : pathSoFar + "/" + segments[i];
            if (_byPath.TryGetValue(pathSoFar, out var node) && !string.IsNullOrEmpty(node.Token))
                tokens.Add(node.Token);
        }
        return tokens;
    }

    /// <summary>True when <paramref name="folderPath"/> is the given node's path or a descendant of it.</summary>
    public static bool IsSelfOrDescendant(string folderPath, string ancestorFolderPath)
    {
        if (ancestorFolderPath.Length == 0) return true;   // root is ancestor of everything
        return string.Equals(folderPath, ancestorFolderPath, StringComparison.Ordinal)
            || folderPath.StartsWith(ancestorFolderPath + "/", StringComparison.Ordinal);
    }

    /// <summary>The folder path containing a file ("" when the file sits at the root).</summary>
    public static string FolderOf(string relativeFilePath)
    {
        var idx = relativeFilePath.LastIndexOf('/');
        return idx < 0 ? "" : relativeFilePath[..idx];
    }

    /// <summary>
    /// The suggested technical token for a <b>single</b> folder segment. A folder name is one level,
    /// so its token must be one <b>atomic</b> component: the hyphen is already the separator between
    /// the id's components, and a single composite folder name must not look like several levels
    /// ("Test Disks" → <c>testdisks</c>, not <c>test-disks</c>). Reuses the shared
    /// <see cref="DatTechnicalIdPolicy.NormalizeSuggestion"/> (which lowercases, strips accents, and
    /// maps spaces/underscores/punctuation to hyphens) and then <b>compacts the internal separators</b>
    /// of that one segment. Only the Group-DAT folder-token suggestion behaves this way; the global
    /// policy and final <c>DatLineId</c> validation are unchanged. The token remains fully editable.
    /// </summary>
    public static string SuggestFolderToken(string folderSegment) =>
        DatTechnicalIdPolicy.NormalizeSuggestion(folderSegment).Replace("-", "");
}
