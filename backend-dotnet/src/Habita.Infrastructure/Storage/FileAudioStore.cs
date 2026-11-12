using Habita.Application;

namespace Habita.Infrastructure.Storage;

/// <summary>Audio en disco local. Sustituto de Azure Blob Storage: la clave es "id/00001.webm".</summary>
internal sealed class FileAudioStore : IAudioStore
{
    private readonly string _root;

    public FileAudioStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(string recordingId, int seq, ReadOnlyMemory<byte> data, string extension, CancellationToken ct = default)
    {
        var key = $"{SafeId(recordingId)}/{seq:00000}.{extension}";
        var path = Resolve(key);
        var directoryPath = Path.GetDirectoryName(path)!;
        if (!directoryPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid directory path.");
        if (!Directory.Exists(directoryPath))
        {
            var canonicalDirectoryPath = Path.GetFullPath(directoryPath);
            if (!canonicalDirectoryPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid directory path.");
            Directory.CreateDirectory(directoryPath);
        }
        var canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid file path.");
        // Create y no CreateNew: si se pierde la respuesta, el navegador reenvía el mismo fragmento y debe reemplazarse.
        await using var file = new FileStream(canonicalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await file.WriteAsync(data, ct);
        return key;
    }

    public Task<byte[]> ReadAsync(string audioKey, CancellationToken ct = default) => File.ReadAllBytesAsync(Resolve(audioKey), ct);

    public Task DeleteRecordingAsync(string recordingId, CancellationToken ct = default)
    {
        var dir = Resolve(SafeId(recordingId));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Evita que una clave escape de la carpeta raíz.</summary>
    private string Resolve(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Clave de audio inválida.");
        return path;
    }

    private static string SafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit) ? id : throw new InvalidOperationException("Identificador inválido.");
}
