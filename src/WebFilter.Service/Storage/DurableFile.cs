using System.Text;

namespace WebFilter.Service.Storage;

/// <summary>
/// Replaces a file so that a power cut leaves either the old or the new content, never a mix or zeros. The new content
/// goes to <c>&lt;path&gt;.tmp</c> and is flushed to the disk before the rename: NTFS journals the rename but not the
/// data, so renaming an unflushed file can leave a file of the right length full of zeros after a hard shutdown (found
/// in the lab, 2026-11-05: <c>managed-policies.json</c> on both VMs).
/// </summary>
public static class DurableFile
{
    /// <summary>The previous version, kept by <see cref="Write(string, byte[], bool, Action{string}?)"/> with a backup.</summary>
    public static string BackupPath(string path) => path + ".bak";

    public static void Write(string path, string content, bool keepBackup = false, Action<string>? prepare = null) =>
        Write(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content), keepBackup, prepare);

    /// <param name="keepBackup">Keep the file being replaced as <see cref="BackupPath"/> (rename, so it costs no copy).</param>
    /// <param name="prepare">Runs on the flushed temporary file before it takes the place of <paramref name="path"/>, e.g. to set its ACL.</param>
    public static void Write(string path, byte[] content, bool keepBackup = false, Action<string>? prepare = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        prepare?.Invoke(temp);
        if (keepBackup && File.Exists(path)) File.Replace(temp, path, BackupPath(path), ignoreMetadataErrors: true);
        else File.Move(temp, path, overwrite: true);
    }
}
