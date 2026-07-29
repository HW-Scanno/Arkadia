using System;
using System.Diagnostics;
using System.IO;

namespace Arkadia.Data;

/// <summary>
/// Stateless helpers for template expansion, tool resolution, and transform execution.
/// All methods are synchronous and safe to call from background threads.
/// </summary>
public static class TransformEngine
{
    // ── B) Placeholder engine ─────────────────────────────────────────────────

    /// <summary>
    /// Expands placeholders in <paramref name="template"/> using the given input/output paths.
    /// Supported placeholders:
    ///   {input}       → full input path
    ///   {output}      → full output path
    ///   {input_name}  → file name with extension
    ///   {output_name} → file name with extension
    ///   {input_stem}  → file name without extension
    ///   {output_stem} → file name without extension
    ///   {input_dir}   → input directory (no trailing separator)
    ///   {output_dir}  → output directory (no trailing separator)
    /// </summary>
    public static string BuildCommand(string template, string inputPath, string outputPath)
    {
        var inputDir   = (Path.GetDirectoryName(inputPath)  ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var outputDir  = (Path.GetDirectoryName(outputPath) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var inputName  = Path.GetFileName(inputPath);
        var outputName = Path.GetFileName(outputPath);
        var inputStem  = Path.GetFileNameWithoutExtension(inputPath);
        var outputStem = Path.GetFileNameWithoutExtension(outputPath);

        return template
            .Replace("{input}",       inputPath,  StringComparison.Ordinal)
            .Replace("{output}",      outputPath, StringComparison.Ordinal)
            .Replace("{input_name}",  inputName,  StringComparison.Ordinal)
            .Replace("{output_name}", outputName, StringComparison.Ordinal)
            .Replace("{input_stem}",  inputStem,  StringComparison.Ordinal)
            .Replace("{output_stem}", outputStem, StringComparison.Ordinal)
            .Replace("{input_dir}",   inputDir,   StringComparison.Ordinal)
            .Replace("{output_dir}",  outputDir,  StringComparison.Ordinal);
    }

    // ── C) Tool resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the absolute path to the tool executable:
    ///   AppRoot/tools/&lt;folder_name&gt;/&lt;executable_name&gt;
    /// Throws <see cref="FileNotFoundException"/> if the file is not present.
    /// </summary>
    public static string ResolveToolExecutable(string appRoot, ToolRecord tool)
    {
        var path = Path.Combine(appRoot, "tools", tool.FolderName, tool.ExecutableName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Tool executable not found: {path}", path);
        return path;
    }

    // ── D) Execution engine ───────────────────────────────────────────────────

    /// <summary>
    /// Executes a transform, producing <paramref name="outputPath"/> from <paramref name="inputPath"/>.
    /// Returns <c>true</c> on success; sets <paramref name="error"/> and returns <c>false</c> on failure.
    /// </summary>
    public static bool ExecuteTransform(
        TransformRecord transform,
        ToolRecord?     tool,
        string          appRoot,
        string          inputPath,
        string          outputPath,
        out string      error)
    {
        error = "";

        // Special case: no_compression → plain file copy
        if (transform.Id == "no_compression")
        {
            try
            {
                File.Copy(inputPath, outputPath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // External tool path
        if (tool is null)
        {
            error = $"Transform '{transform.Name}' requires a tool but none is configured.";
            return false;
        }

        string exePath;
        try
        {
            exePath = ResolveToolExecutable(appRoot, tool);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var args = BuildCommand(transform.CommandTemplate, inputPath, outputPath);

        try
        {
            var psi = new ProcessStartInfo(exePath, args)
            {
                RedirectStandardOutput = false,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                error = "Failed to start process.";
                return false;
            }

            // Read stderr before WaitForExit to avoid deadlock on large output.
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                error = $"Process exited with code {proc.ExitCode}. {stderr}".Trim();
                return false;
            }

            if (!File.Exists(outputPath))
            {
                error = $"Transform exited cleanly but output not found: {outputPath}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
