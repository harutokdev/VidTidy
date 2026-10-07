using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

public sealed class FileSystemMediaScanner
{
    public static bool IsSupportedImageExtension(string? extension) => MediaFormats.IsImage(extension);
    private const int SampleSizeBytes = 64 * 1024;
    private const string FingerprintVersion = "qf-v1-64k-head-middle-tail";

    public async IAsyncEnumerable<ScannedMediaFile> EnumerateAsync(LibraryRoot root, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var observations = new ScanObservationCollector();
        await foreach (var file in EnumerateAsync(root, observations, cancellationToken)) yield return file;
    }

    public async IAsyncEnumerable<ScannedMediaFile> EnumerateAsync(LibraryRoot root, ScanObservationCollector observations, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root.ConfiguredPath)) yield break;
        var pending = new Stack<string>();
        pending.Push(root.ConfiguredPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                var info = new DirectoryInfo(directory);
                if (PathPolicy.IsReparsePoint(info)) continue;
                var identity = WindowsFileIdentityReader.TryGetDirectory(directory);
                observations.AddAccessibleDirectory(new ScannedDirectory(info.FullName, PathPolicy.NormalizeForIdentity(info.FullName), identity?.VolumeIdentity, identity?.FileIdentity));
                entries = info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToArray();
            }
            catch (UnauthorizedAccessException) { observations.AddUnknownDirectory(directory); continue; }
            catch (IOException) { observations.AddUnknownDirectory(directory); continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PathPolicy.IsReparsePoint(entry)) continue;
                if (entry is DirectoryInfo child) { pending.Push(child.FullName); continue; }
                if (entry is not FileInfo fileInfo || !MediaFormats.IsMediaExtension(fileInfo.Extension)) continue;
                var identity = WindowsFileIdentityReader.TryGet(fileInfo.FullName);
                string? fingerprint = null;
                try
                {
                    if (MediaFormats.NeedsContentCheck(fileInfo.Extension) && !await IsTransportStreamAsync(fileInfo.FullName, cancellationToken)) continue;
                    fingerprint = await QuickFingerprintAsync(fileInfo.FullName, fileInfo.Length, cancellationToken);
                }
                catch (UnauthorizedAccessException) { observations.AddUnknownDirectory(directory); continue; }
                catch (IOException) { observations.AddUnknownDirectory(directory); continue; }
                yield return new ScannedMediaFile(root.LibraryRootId, fileInfo.FullName, PathPolicy.NormalizeForIdentity(fileInfo.FullName), fileInfo.Name, fileInfo.Extension.ToLowerInvariant(), fileInfo.Length, new DateTimeOffset(fileInfo.CreationTimeUtc), new DateTimeOffset(fileInfo.LastWriteTimeUtc), identity?.VolumeIdentity, identity?.FileIdentity, fingerprint);
            }
        }
    }

    private static async Task<bool> IsTransportStreamAsync(string path, CancellationToken cancellationToken)
    {
        var head = new byte[MediaFormats.TransportStreamProbeBytes];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, head.Length, FileOptions.Asynchronous);
        var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        return MediaFormats.LooksLikeTransportStream(head.AsSpan(0, read));
    }

    public RootProbeResult Probe(LibraryRoot root)
    {
        var configured = root.ConfiguredPath;
        if (!string.IsNullOrWhiteSpace(root.VolumeIdentity))
        {
            var currentVolume = WindowsVolumeIdentityReader.TryGetForPath(configured);
            if (!string.Equals(currentVolume, root.VolumeIdentity, StringComparison.Ordinal))
            {
                var oldDrive = Path.GetPathRoot(Path.GetFullPath(configured)) ?? string.Empty;
                var relative = Path.GetRelativePath(oldDrive, configured);
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady || WindowsVolumeIdentityReader.TryGetForPath(drive.RootDirectory.FullName) != root.VolumeIdentity) continue;
                        var relocated = Path.GetFullPath(Path.Combine(drive.RootDirectory.FullName, relative));
                        if (Directory.Exists(relocated)) configured = relocated;
                        break;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        try
        {
            if (Directory.Exists(configured))
            {
                _ = Directory.EnumerateFileSystemEntries(configured).Take(1).ToArray();
                var identity = WindowsFileIdentityReader.TryGetDirectory(configured);
                if (!string.IsNullOrWhiteSpace(root.RootIdentity) && identity is not null &&
                    (!string.Equals(root.VolumeIdentity, identity.VolumeIdentity, StringComparison.Ordinal) || !string.Equals(root.RootIdentity, identity.FileIdentity, StringComparison.Ordinal)))
                    return new RootProbeResult(root.RootKind == LibraryRootKind.Drive ? RootObservationKind.DriveOffline : RootObservationKind.RootFolderMissing, configured, identity.VolumeIdentity, identity.FileIdentity);
                return new RootProbeResult(RootObservationKind.Online, configured, identity?.VolumeIdentity, identity?.FileIdentity);
            }
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(configured));
            if (string.IsNullOrWhiteSpace(driveRoot)) return new RootProbeResult(RootObservationKind.Unknown, configured, null, null);
            var drive = new DriveInfo(driveRoot);
            return !drive.IsReady || root.RootKind == LibraryRootKind.Drive
                ? new RootProbeResult(RootObservationKind.DriveOffline, configured, null, null)
                : new RootProbeResult(RootObservationKind.RootFolderMissing, configured, WindowsVolumeIdentityReader.TryGetForPath(driveRoot), null);
        }
        catch (UnauthorizedAccessException) { return new RootProbeResult(RootObservationKind.Unknown, configured, null, null); }
        catch (IOException) { return new RootProbeResult(RootObservationKind.Unknown, configured, null, null); }
    }

    public static async Task<string> QuickFingerprintAsync(string path, long length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, SampleSizeBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SampleSizeBytes];
        async Task ReadAtAsync(long offset)
        {
            stream.Position = Math.Clamp(offset, 0, Math.Max(0, stream.Length - 1));
            var read = await stream.ReadAsync(buffer, cancellationToken);
            sha.AppendData(buffer, 0, read);
        }
        sha.AppendData(Encoding.UTF8.GetBytes($"{FingerprintVersion}|{length}|"));
        await ReadAtAsync(0);
        if (length > SampleSizeBytes) await ReadAtAsync(Math.Max(0, (length / 2) - (SampleSizeBytes / 2)));
        if (length > SampleSizeBytes * 2) await ReadAtAsync(Math.Max(0, length - SampleSizeBytes));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

public sealed record RootProbeResult(RootObservationKind Observation, string EffectivePath, string? VolumeIdentity, string? RootDirectoryIdentity);

public sealed class ScanObservationCollector
{
    private readonly List<ScannedDirectory> _accessible = [];
    private readonly HashSet<string> _unknown = new(StringComparer.OrdinalIgnoreCase);
    public void AddAccessibleDirectory(ScannedDirectory directory) => _accessible.Add(directory);
    public void AddUnknownDirectory(string path) => _unknown.Add(Path.GetFullPath(path));
    public ScanAuthoritySnapshot Snapshot(string? volumeIdentity, string? rootIdentity, bool completed) => new(_accessible.ToArray(), _unknown.ToArray(), volumeIdentity, rootIdentity, completed);
}

public sealed record WindowsFileIdentity(string VolumeIdentity, string FileIdentity);

internal static class WindowsVolumeIdentityReader
{
    public static string? TryGetForPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return null;
            var volumeName = new char[128];
            if (GetVolumeNameForVolumeMountPoint(root, volumeName, (uint)volumeName.Length))
                return new string(volumeName).TrimEnd('\0', '\\').ToUpperInvariant();
            return GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0) ? $"SERIAL:{serial:X8}" : null;
        }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string lpRootPathName, [Out] char[]? lpVolumeNameBuffer, uint nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, [Out] char[]? lpFileSystemNameBuffer, uint nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, uint bufferLength);
}

internal static class WindowsFileIdentityReader
{
    public static WindowsFileIdentity? TryGet(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var info)) return null;
            var volume = WindowsVolumeIdentityReader.TryGetForPath(path) ?? $"SERIAL:{info.VolumeSerialNumber:X8}";
            var id = ((ulong)info.FileIndexHigh << 32 | info.FileIndexLow).ToString("X16", CultureInfo.InvariantCulture);
            return new WindowsFileIdentity(volume, id);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static WindowsFileIdentity? TryGetDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            (FileAttributes)0x02000000, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            var volume = WindowsVolumeIdentityReader.TryGetForPath(path) ?? $"SERIAL:{info.VolumeSerialNumber:X8}";
            var id = ((ulong)info.FileIndexHigh << 32 | info.FileIndexLow).ToString("X16", CultureInfo.InvariantCulture);
            return new WindowsFileIdentity(volume, id);
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, FileAttributes flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint LowDateTime; public uint HighDateTime; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public sealed record FfmpegPaths(string? FfmpegPath, string? FfprobePath)
{
    public static FfmpegPaths Resolve(AppPaths paths, string? explicitFfmpeg = null, string? explicitFfprobe = null)
    {
        var ffmpeg = explicitFfmpeg ?? Path.Combine(paths.RuntimeRoot, "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        var ffprobe = explicitFfprobe ?? Path.Combine(paths.RuntimeRoot, "ffmpeg", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        return new FfmpegPaths(File.Exists(ffmpeg) ? ffmpeg : null, File.Exists(ffprobe) ? ffprobe : null);
    }
}

public sealed record ProbeResult(MediaMetadata Metadata, string ProcessorVersion);

public sealed partial class FfmpegAdapter
{
    private readonly FfmpegPaths _paths;
    private readonly AppPaths _appPaths;
    private readonly IStructuredLogger _log;
    private readonly TimeSpan _processTimeout;
    private readonly SourceTransferCache _transfers = new();

    public FfmpegAdapter(FfmpegPaths paths, AppPaths appPaths, IStructuredLogger log, TimeSpan? processTimeout = null)
    {
        _paths = paths;
        _appPaths = appPaths;
        _log = log;
        _processTimeout = processTimeout ?? TimeSpan.FromMinutes(5);
    }

    public bool IsProbeAvailable => _paths.FfprobePath is not null;
    public bool IsFfmpegAvailable => _paths.FfmpegPath is not null;
    internal AppPaths ApplicationPaths => _appPaths;

    public string ResolveVisualSamplePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_appPaths.CacheRoot, normalized));
        if (!PathPolicy.IsWithinRoot(_appPaths.CacheRoot, fullPath) || !File.Exists(fullPath))
            throw new MediaToolException(new DomainError("VISUAL_FRAME_NOT_FOUND", L.T("検索用フレームが利用できません。再解析してください。")));
        return fullPath;
    }

    /// <summary>
    /// Reads a stored near-duplicate frame image back as the 9x8 grey raster, to tell a flat frame from a smooth
    /// gradient. Only the cached image is read, never the source video. Null when the image or FFmpeg is unavailable.
    /// </summary>
    public async Task<byte[]?> ReadStoredFrameGrayAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (_paths.FfmpegPath is null || string.IsNullOrWhiteSpace(relativePath)) return null;
        string fullPath;
        try { fullPath = ResolveVisualSamplePath(relativePath); }
        catch (MediaToolException) { return null; }
        Directory.CreateDirectory(_appPaths.TempRoot);
        var rawPath = Path.Combine(_appPaths.TempRoot, "flat-check-" + Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            var result = await RunAsync(_paths.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", fullPath, "-vf", "scale=9:8:flags=area,format=gray", "-frames:v", "1", "-f", "rawvideo", rawPath],
                "ffmpeg-flat-check", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(rawPath)) return null;
            var bytes = await File.ReadAllBytesAsync(rawPath, cancellationToken).ConfigureAwait(false);
            return bytes.Length == PerceptualFrameFingerprint.InputByteCount ? bytes : null;
        }
        finally
        {
            DeleteIfExists(rawPath);
        }
    }

    /// <summary>
    /// Reads stored near-duplicate frame images back as grey rasters of one size, all in one FFmpeg run, so black bars
    /// can be found without opening the source video. Null when any image or FFmpeg is unavailable.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>?> ReadStoredFramesGrayAsync(IReadOnlyList<string?> relativePaths, int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (_paths.FfmpegPath is null || relativePaths.Count == 0) return null;
        var fullPaths = new List<string>(relativePaths.Count);
        foreach (var relativePath in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            try { fullPaths.Add(ResolveVisualSamplePath(relativePath)); }
            catch (MediaToolException) { return null; }
        }
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var fullPath in fullPaths) arguments.AddRange(["-i", fullPath]);
        // Stacked into one tall picture: concat drops frames of single-image inputs, a stack keeps each one exactly.
        var graph = fullPaths.Count == 1
            ? $"[0:v]scale={width}:{height}:flags=area,format=gray[frames]"
            : string.Concat(fullPaths.Select((_, index) => $"[{index}:v]scale={width}:{height}:flags=area,format=gray,setsar=1[f{index}];"))
                + string.Concat(fullPaths.Select((_, index) => $"[f{index}]")) + $"vstack=inputs={fullPaths.Count}[frames]";
        Directory.CreateDirectory(_appPaths.TempRoot);
        var rawPath = Path.Combine(_appPaths.TempRoot, "prepare-frames-" + Guid.NewGuid().ToString("N") + ".raw");
        arguments.AddRange(["-filter_complex", graph, "-map", "[frames]", "-frames:v", "1", "-f", "rawvideo", rawPath]);
        try
        {
            var result = await RunAsync(_paths.FfmpegPath, arguments, "ffmpeg-prepare-frames", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(rawPath)) return null;
            var bytes = await File.ReadAllBytesAsync(rawPath, cancellationToken).ConfigureAwait(false);
            var frameBytes = width * height;
            if (bytes.Length != frameBytes * fullPaths.Count) return null;
            return Enumerable.Range(0, fullPaths.Count).Select(index => bytes.AsSpan(index * frameBytes, frameBytes).ToArray()).ToArray();
        }
        finally
        {
            DeleteIfExists(rawPath);
        }
    }

    /// <summary>
    /// Crashや強制終了で残り得る未公開のthumbnail temp directoryだけを削除します。
    /// CacheRootにatomic publish済みのartifactと、他stageのTempRootは変更しません。
    /// </summary>
    public int CleanupIncompleteThumbnailArtifacts()
    {
        if (!Directory.Exists(_appPaths.TempRoot)) return 0;
        var deleted = 0;
        foreach (var directory in Directory.EnumerateDirectories(_appPaths.TempRoot, "thumbnail-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (IOException ex)
            {
                _log.Write("Warning", "ThumbnailTempCleanupFailed", new { Operation = "ThumbnailTempCleanup", TempDirectory = directory, ErrorCode = "ARTIFACT_WRITE_FAILED" }, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Write("Warning", "ThumbnailTempCleanupDenied", new { Operation = "ThumbnailTempCleanup", TempDirectory = directory, ErrorCode = "MEDIA_ACCESS_DENIED" }, ex);
            }
        }
        if (deleted > 0)
            _log.Write("Information", "IncompleteThumbnailTempCleaned", new { Operation = "ThumbnailTempCleanup", DeletedDirectories = deleted });
        return deleted;
    }

    public async Task<ProbeResult> ProbeAsync(MediaFile file, CancellationToken cancellationToken)
    {
        if (_paths.FfprobePath is null) throw new MediaToolException(DomainError.FfmpegNotFound);
        EnsureReadOnlyInput(file.Path);
        var result = await RunAsync(_paths.FfprobePath, ["-v", "error", "-show_format", "-show_streams", "-of", "json", file.Path], "ffprobe", cancellationToken);
        if (result.ExitCode != 0) throw new MediaToolException(new DomainError("FFPROBE_FAILED", L.T("メタデータを取得できませんでした。ファイルが破損または未対応の可能性があります。"), Trim(result.StandardError)));
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
            var streams = root.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array ? streamsElement.EnumerateArray().ToArray() : [];
            var video = streams.FirstOrDefault(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
            _transfers.Set(file, HdrToneMapping.IsHdrTransfer(GetString(video, "color_transfer")));
            var audio = streams.Count(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
            var subtitles = streams.Count(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "subtitle");
            var metadata = new MediaMetadata(file.MediaFileId, GetString(format, "format_name"), GetNullableDouble(format, "duration"), GetNullableInt(video, "width"), GetNullableInt(video, "height"), GetString(video, "codec_name"), FormatFps(video), GetNullableLong(format, "bit_rate"), audio, subtitles, Summarize(root), DateTimeOffset.UtcNow);
            return new ProbeResult(metadata, "ffprobe-json-v1");
        }
        catch (JsonException ex) { throw new MediaToolException(new DomainError("INVALID_MEDIA", L.T("FFprobeの応答を解析できませんでした。"), ex.Message)); }
    }

    public Task<ProbeResult> ProbeAsync(ProcessingSourceAccess source, MediaFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.EnsureMatches(file);
        return ProbeAsync(file, cancellationToken);
    }

    public async Task<GeneratedVisualFrame> ExtractFrameAsync(MediaFile file, double requestedTimestampSeconds, string cacheKey, CancellationToken cancellationToken)
    {
        var extraction = await ExtractFrameCoreAsync(file, requestedTimestampSeconds, cacheKey, includeDHash: false, cancellationToken).ConfigureAwait(false);
        return extraction.Visual;
    }

    private async Task<(GeneratedVisualFrame Visual, string? Fingerprint)> ExtractFrameCoreAsync(
        MediaFile file,
        double requestedTimestampSeconds,
        string cacheKey,
        bool includeDHash,
        CancellationToken cancellationToken)
    {
        if (_paths.FfmpegPath is null) throw new MediaToolException(DomainError.FfmpegNotFound);
        if (!double.IsFinite(requestedTimestampSeconds) || requestedTimestampSeconds < 0)
            throw new MediaToolException(new DomainError("INVALID_TIMESTAMP", L.T("フレーム抽出時刻が不正です。")));
        if (string.IsNullOrWhiteSpace(cacheKey) || cacheKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new MediaToolException(new DomainError("INVALID_CACHE_KEY", L.T("フレームキャッシュキーが不正です。")));
        EnsureReadOnlyInput(file.Path);
        var relative = Path.Combine("visual-samples", cacheKey[..Math.Min(2, cacheKey.Length)], cacheKey + ".jpg");
        var destination = Path.Combine(_appPaths.CacheRoot, relative);
        var tempDirectory = Path.Combine(_appPaths.TempRoot, "visual-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, cacheKey + ".tmp.jpg");
        var dHashPath = Path.Combine(tempDirectory, cacheKey + ".dhash.raw");
        try
        {
            var operation = includeDHash ? "ffmpeg-near-duplicate-frame" : "ffmpeg-visual-frame";
            var seekSeconds = requestedTimestampSeconds;
            var timestamp = seekSeconds.ToString("F6", CultureInfo.InvariantCulture);
            var keyframesOnly = false;
            var hdr = await IsHdrSourceAsync(file, cancellationToken).ConfigureAwait(false);
            var result = await RunAsync(_paths.FfmpegPath, BuildFrameExtractionArguments(file.Path, timestamp, tempPath, includeDHash ? dHashPath : null, keyframesOnly, hdr), operation, cancellationToken).ConfigureAwait(false);
            if (hdr && ToneMappingFailed(result, tempPath))
            {
                DeleteIfExists(tempPath);
                DeleteIfExists(dHashPath);
                hdr = false;
                _log.Write("Warning", "HdrToneMappingFailed", new { Operation = operation, Diagnostic = Trim(result.StandardError) });
                result = await RunAsync(_paths.FfmpegPath, BuildFrameExtractionArguments(file.Path, timestamp, tempPath, includeDHash ? dHashPath : null, keyframesOnly, hdr), operation, cancellationToken).ConfigureAwait(false);
            }
            // A damaged stream makes ffmpeg give up on the decode error rate. Frames decoded from damaged references are
            // broken images, so take the next intact keyframe instead.
            if (result.ExitCode != 0 && ExceededDecodeErrorRate(result.StandardError))
            {
                DeleteIfExists(tempPath);
                DeleteIfExists(dHashPath);
                keyframesOnly = true;
                _log.Write("Information", "FrameExtractionKeyframesOnly", new { Operation = operation, RequestedTimestampSeconds = requestedTimestampSeconds });
                result = await RunAsync(_paths.FfmpegPath, BuildFrameExtractionArguments(file.Path, timestamp, tempPath, includeDHash ? dHashPath : null, keyframesOnly, hdr), operation, cancellationToken).ConfigureAwait(false);
            }
            // The container duration can outlast the video stream (typically an audio tail), which puts the last
            // sample time after the final video frame. What plays at that time is the final frame, so step back to it
            // instead of failing the file: first 1 s, then just before the probed end of the video stream, then 0.
            // For full-range (phone) video ffmpeg reports the missing frame but exits 0 with no output, so the exit
            // code alone does not show it.
            for (var attempt = 0; attempt < 3 && NoFrameWritten(result, tempPath) && seekSeconds > 0 && IsPastLastVideoFrame(result.StandardError); attempt++)
            {
                DeleteIfExists(tempPath);
                DeleteIfExists(dHashPath);
                var previous = seekSeconds;
                seekSeconds = attempt switch
                {
                    0 => Math.Max(0, requestedTimestampSeconds - 1),
                    1 => await ProbeVideoStreamEndAsync(file, cancellationToken).ConfigureAwait(false) is { } end ? Math.Max(0, end - 1) : 0,
                    _ => 0
                };
                if (seekSeconds >= previous) seekSeconds = 0;
                timestamp = seekSeconds.ToString("F6", CultureInfo.InvariantCulture);
                _log.Write("Information", "FrameExtractionPastVideoEnd", new { Operation = operation, RequestedTimestampSeconds = requestedTimestampSeconds, SeekSeconds = seekSeconds });
                result = await RunAsync(_paths.FfmpegPath, BuildFrameExtractionArguments(file.Path, timestamp, tempPath, includeDHash ? dHashPath : null, keyframesOnly, hdr), operation, cancellationToken).ConfigureAwait(false);
            }
            if (NoFrameWritten(result, tempPath))
                throw new MediaToolException(new DomainError("FRAME_EXTRACTION_FAILED", L.T("検索用フレームを抽出できませんでした。"), Trim(result.StandardError)));
            byte[]? dHashBytes = null;
            if (includeDHash)
                dHashBytes = await ValidateNearOutputsAsync(tempPath, dHashPath, cancellationToken).ConfigureAwait(false);
            else
                ValidateJpegOutput(tempPath);
            var actualTimestamp = await ResolvePresentationTimestampAsync(
                result.StandardError,
                seekSeconds,
                () => ProbeFramePresentationTimestampAsync(file, timestamp, cancellationToken)).ConfigureAwait(false);
            File.Move(tempPath, destination, true);
            var info = new FileInfo(destination);
            var visual = new GeneratedVisualFrame(relative.Replace(Path.DirectorySeparatorChar, '/'), destination, await FileHash.Sha256Async(destination, cancellationToken).ConfigureAwait(false), info.Length, requestedTimestampSeconds, actualTimestamp);
            return (visual, dHashBytes is null ? null : PerceptualFrameFingerprint.ComputeDHashHex(dHashBytes));
        }
        finally
        {
            try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true); } catch (IOException) { }
        }
    }

    public Task<GeneratedVisualFrame> ExtractFrameAsync(ProcessingSourceAccess source, MediaFile file, double requestedTimestampSeconds, string cacheKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.EnsureMatches(file);
        return ExtractFrameAsync(file, requestedTimestampSeconds, cacheKey, cancellationToken);
    }

    public async Task<GeneratedNearDuplicateFrame> ExtractNearDuplicateFrameAsync(MediaFile file, int sampleIndex, double normalizedPosition, double requestedTimestampSeconds, string cacheKey, CancellationToken cancellationToken)
    {
        var extraction = await ExtractFrameCoreAsync(file, requestedTimestampSeconds, cacheKey, includeDHash: true, cancellationToken).ConfigureAwait(false);
        return new GeneratedNearDuplicateFrame(sampleIndex, normalizedPosition, extraction.Visual.RelativePath, extraction.Visual.FullPath, extraction.Visual.Sha256, extraction.Visual.ByteSize, extraction.Visual.RequestedTimestampSeconds, extraction.Visual.PresentationTimestampSeconds,
            extraction.Fingerprint ?? throw new InvalidOperationException("Near Duplicate extraction did not produce a fingerprint."));
    }

    public Task<GeneratedNearDuplicateFrame> ExtractNearDuplicateFrameAsync(ProcessingSourceAccess source, MediaFile file, int sampleIndex, double normalizedPosition, double requestedTimestampSeconds, string cacheKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.EnsureMatches(file);
        return ExtractNearDuplicateFrameAsync(file, sampleIndex, normalizedPosition, requestedTimestampSeconds, cacheKey, cancellationToken);
    }

    public async Task<GeneratedThumbnail> GenerateThumbnailAsync(MediaFile file, string cacheKey, CancellationToken cancellationToken)
    {
        if (_paths.FfmpegPath is null) throw new MediaToolException(DomainError.FfmpegNotFound);
        EnsureReadOnlyInput(file.Path);
        var relative = Path.Combine("thumbnails", cacheKey[..2], cacheKey + ".jpg");
        var destination = Path.Combine(_appPaths.CacheRoot, relative);
        var tempDirectory = Path.Combine(_appPaths.TempRoot, "thumbnail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, cacheKey + ".tmp.jpg");
        try
        {
            var hdr = await IsHdrSourceAsync(file, cancellationToken);
            async Task<ProcessResult> GenerateAsync(bool seek, bool keyframesOnly)
            {
                var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
                if (keyframesOnly) { args.Add("-skip_frame"); args.Add("nokey"); }
                if (seek) { args.Add("-ss"); args.Add("00:00:01"); }
                args.AddRange(["-i", file.Path, "-frames:v", "1", "-vf", HdrToneMapping.Apply("scale='min(480,iw)':-2", hdr), "-q:v", "4", tempPath]);
                return await RunAsync(_paths.FfmpegPath, args, "ffmpeg-thumbnail", cancellationToken);
            }
            var seek = !FileSystemMediaScanner.IsSupportedImageExtension(Path.GetExtension(file.Path));
            var keyframesOnly = false;
            var result = await GenerateAsync(seek, keyframesOnly);
            if (hdr && ToneMappingFailed(result, tempPath))
            {
                hdr = false;
                _log.Write("Warning", "HdrToneMappingFailed", new { Operation = "ffmpeg-thumbnail", Diagnostic = Trim(result.StandardError) });
                result = await GenerateAsync(seek, keyframesOnly);
            }
            // A damaged stream fails on the decode error rate; its keyframes are usually intact (see ExtractFrameCoreAsync).
            if (NoFrameWritten(result, tempPath) && ExceededDecodeErrorRate(result.StandardError))
            {
                keyframesOnly = true;
                result = await GenerateAsync(seek, keyframesOnly);
            }
            if (NoFrameWritten(result, tempPath))
                result = await GenerateAsync(false, keyframesOnly); // first frame for short videos; still images must not seek past their only frame
            if (NoFrameWritten(result, tempPath)) throw new MediaToolException(new DomainError("ARTIFACT_WRITE_FAILED", L.T("サムネイルを生成できませんでした。"), Trim(result.StandardError)));
            File.Move(tempPath, destination, true);
            var info = new FileInfo(destination);
            return new GeneratedThumbnail(relative.Replace(Path.DirectorySeparatorChar, '/'), destination, await FileHash.Sha256Async(destination, cancellationToken), info.Length);
        }
        finally
        {
            try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true); } catch (IOException) { }
        }
    }

    public Task<GeneratedThumbnail> GenerateThumbnailAsync(ProcessingSourceAccess source, MediaFile file, string cacheKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.EnsureMatches(file);
        return GenerateThumbnailAsync(file, cacheKey, cancellationToken);
    }

    private void EnsureReadOnlyInput(string inputPath)
    {
        if (!Path.IsPathFullyQualified(inputPath)) throw new MediaToolException(new DomainError("INVALID_MEDIA", L.T("メディアパスが不正です。")));
        // Single choke point before any ffmpeg/ffprobe opens an original: a test-lab instance never reads outside its media folders.
        try { _appPaths.TestLab?.EnsureMediaPathAllowed(inputPath); }
        catch (TestLabViolationException ex) { throw new MediaToolException(new DomainError("TESTLAB_MEDIA_REJECTED", L.T("テスト用モードでは、テスト用フォルダーの外のメディアは読みません。"), ex.Message)); }
        try
        {
            using var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MediaToolException(new DomainError("MEDIA_ACCESS_DENIED", L.T("メディアへアクセスできません。権限またはロック状態を確認してください。"), ex.GetType().Name));
        }
        catch (FileNotFoundException)
        {
            throw new MediaToolException(new DomainError("MEDIA_NOT_FOUND", L.T("対象メディアが見つかりません。")));
        }
        catch (DirectoryNotFoundException)
        {
            throw new MediaToolException(new DomainError("MEDIA_NOT_FOUND", L.T("対象メディアが見つかりません。")));
        }
    }

    private async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string operation, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var started = Stopwatch.StartNew();
        if (!process.Start()) throw new MediaToolException(new DomainError("FFPROBE_FAILED", L.T("メディア処理を開始できませんでした。")));
        BackgroundPriority.TryApply(process);
        var stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(_processTimeout, cancellationToken); }
        catch (TimeoutException)
        {
            var killRequested = false;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                killRequested = true;
            }

            // Timeout cleanup deliberately does not reuse the operation token. The
            // Process instance is the ownership boundary available to this adapter:
            // confirm that owned process has exited and observe both redirected-read
            // tasks before disposing it. Kill(entireProcessTree) requests descendant
            // termination, but waiting for this Process does not independently prove
            // that untracked descendants have been reaped.
            await process.WaitForExitAsync(CancellationToken.None);
            Exception? redirectedOutputFailure = null;
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception ex) { redirectedOutputFailure = ex; }
            _log.Write("Warning", "MediaProcessTimeoutCleanupCompleted", new
            {
                Operation = operation,
                KillEntireProcessTreeRequested = killRequested,
                OwnedProcessExited = process.HasExited,
                StandardOutputTaskObserved = stdout.IsCompleted,
                StandardErrorTaskObserved = stderr.IsCompleted,
                RedirectedOutputObservationError = redirectedOutputFailure?.GetType().Name
            });
            throw new MediaToolException(new DomainError("PROCESS_TIMEOUT", L.T("メディア処理が制限時間内に完了しませんでした。"), operation));
        }
        var result = new ProcessResult(process.ExitCode, await stdout, await stderr);
        _log.Write(result.ExitCode == 0 ? "Information" : "Warning", "MediaProcessFinished", new { Operation = operation, DurationMs = started.ElapsedMilliseconds, ExitCode = result.ExitCode });
        return result;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 32 * 1024;
        var buffer = new char[4096];
        var builder = new StringBuilder(maximumCharacters);
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.ToString();
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, read));
        }
    }

    private async Task<double?> ProbeFramePresentationTimestampAsync(MediaFile file, string timestamp, CancellationToken cancellationToken)
    {
        if (_paths.FfprobePath is null) return null;
        var result = await RunAsync(_paths.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-read_intervals", timestamp + "%+#1", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "default=noprint_wrappers=1:nokey=1", file.Path], "ffprobe-visual-frame-timestamp", cancellationToken);
        if (result.ExitCode != 0) return null;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0)
                return value;
        return null;
    }

    /// <summary>Whether the video is HDR; answered from the metadata probe when it ran in this session, else probed once.</summary>
    private async Task<bool> IsHdrSourceAsync(MediaFile file, CancellationToken cancellationToken)
    {
        if (_paths.FfprobePath is null || FileSystemMediaScanner.IsSupportedImageExtension(Path.GetExtension(file.Path))) return false;
        if (_transfers.TryGet(file, out var known)) return known;
        var result = await RunAsync(_paths.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_transfer", "-of", "default=noprint_wrappers=1:nokey=1", file.Path], "ffprobe-color-transfer", cancellationToken).ConfigureAwait(false);
        // A failed probe counts as SDR (the frame is still usable) and is remembered, so each sample does not retry it.
        var hdr = result.ExitCode == 0 && HdrToneMapping.IsHdrTransfer(result.StandardOutput);
        _transfers.Set(file, hdr);
        return hdr;
    }

    /// <summary>End time of the first video stream (not the container), or null when the file does not say.</summary>
    private async Task<double?> ProbeVideoStreamEndAsync(MediaFile file, CancellationToken cancellationToken)
    {
        if (_paths.FfprobePath is null) return null;
        var result = await RunAsync(_paths.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration:stream_tags=DURATION", "-of", "default=noprint_wrappers=1", file.Path], "ffprobe-video-stream-end", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? ParseVideoStreamEnd(result.StandardOutput) : null;
    }

    internal static double? ParseVideoStreamEnd(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator < 0) continue;
            var value = line[(separator + 1)..].Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0) return seconds;
            // Matroska keeps the stream length in a DURATION tag such as 00:00:02.000000000.
            if (value.Length > 16) value = value[..16];
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero) return span.TotalSeconds;
        }
        return null;
    }

    /// <summary>FFmpeg reports this when the seek landed after the last decodable video frame.</summary>
    internal static bool IsPastLastVideoFrame(string standardError) =>
        standardError.Contains("No filtered frames for output stream", StringComparison.Ordinal);

    /// <summary>
    /// The HDR run wrote nothing for a reason other than the stream itself (past its end, or damaged), typically a
    /// source tagged HDR without the colour information zscale needs. The untouched frame is still usable.
    /// </summary>
    private static bool ToneMappingFailed(ProcessResult result, string jpegPath) =>
        NoFrameWritten(result, jpegPath) && !IsPastLastVideoFrame(result.StandardError) && !ExceededDecodeErrorRate(result.StandardError);

    private static bool NoFrameWritten(ProcessResult result, string jpegPath) =>
        result.ExitCode != 0 || !File.Exists(jpegPath) || new FileInfo(jpegPath).Length == 0;

    internal static bool ExceededDecodeErrorRate(string standardError) =>
        standardError.Contains("Decode error rate", StringComparison.Ordinal);

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <param name="keyframesOnly">Decode only keyframes, for damaged streams whose other frames fail to decode.</param>
    /// <param name="hdr">Tone-map an HDR source to SDR (see <see cref="HdrToneMapping"/>); SDR arguments are unchanged.</param>
    internal static IReadOnlyList<string> BuildFrameExtractionArguments(string sourcePath, string timestamp, string jpegPath, string? dHashPath, bool keyframesOnly = false, bool hdr = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(jpegPath);
        string[] input = keyframesOnly
            ? ["-hide_banner", "-nostdin", "-y", "-skip_frame", "nokey", "-ss", timestamp, "-copyts", "-i", sourcePath]
            : ["-hide_banner", "-nostdin", "-y", "-ss", timestamp, "-copyts", "-i", sourcePath];
        var jpegChain = HdrToneMapping.Apply("scale='min(1024,iw)':-2", hdr);
        // For HDR the dHash is taken from the tone-mapped, scaled frame: it must describe what a viewer sees.
        var nearGraph = hdr
            ? "[0:v]showinfo," + jpegChain + ",split=2[jpeg][dhash-source];[dhash-source]scale=9:8:flags=area,format=gray[dhash]"
            : "[0:v]showinfo,split=2[jpeg-source][dhash-source];[jpeg-source]scale='min(1024,iw)':-2[jpeg];[dhash-source]scale=9:8:flags=area,format=gray[dhash]";
        return dHashPath is null
            ? [.. input, "-frames:v", "1", "-an", "-vf", "showinfo," + jpegChain, "-q:v", "3", jpegPath]
            : [.. input,
                "-filter_complex", nearGraph,
                "-map", "[jpeg]", "-frames:v", "1", "-an", "-q:v", "3", jpegPath,
                "-map", "[dhash]", "-frames:v", "1", "-an", "-f", "rawvideo", dHashPath];
    }

    internal static byte[] ValidateDHashBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != PerceptualFrameFingerprint.InputByteCount)
            throw new MediaToolException(new DomainError("FRAME_EXTRACTION_FAILED", L.T("近似重複用フレームを抽出できませんでした。"), $"Expected {PerceptualFrameFingerprint.InputByteCount} dHash bytes; actual {bytes.Length}."));
        return bytes;
    }

    internal static async Task<byte[]> ValidateNearOutputsAsync(string jpegPath, string dHashPath, CancellationToken cancellationToken)
    {
        ValidateJpegOutput(jpegPath);
        if (!File.Exists(dHashPath))
            throw new MediaToolException(new DomainError("FRAME_EXTRACTION_FAILED", L.T("近似重複用フレームを抽出できませんでした。"), "The dHash output is missing."));
        return ValidateDHashBytes(await File.ReadAllBytesAsync(dHashPath, cancellationToken).ConfigureAwait(false));
    }

    private static void ValidateJpegOutput(string jpegPath)
    {
        if (!File.Exists(jpegPath) || new FileInfo(jpegPath).Length == 0)
            throw new MediaToolException(new DomainError("FRAME_EXTRACTION_FAILED", L.T("検索用フレームを抽出できませんでした。"), "The JPEG output is missing or empty."));
    }

    internal static async Task<double> ResolvePresentationTimestampAsync(string standardError, double requestedTimestampSeconds, Func<Task<double?>> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (ParseSingleSelectedFramePresentationTimestamp(standardError) is { } selected) return selected;
        var probed = await fallback().ConfigureAwait(false);
        return probed is { } value && double.IsFinite(value) && value >= 0 ? value : requestedTimestampSeconds;
    }

    internal static double? ParseSingleSelectedFramePresentationTimestamp(string standardError)
    {
        var records = standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("showinfo", StringComparison.OrdinalIgnoreCase))
            .Select(line => Regex.Match(line, @"\bn:\s*(?<index>\d+)\b.*\bpts_time:(?<value>-?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant))
            .Where(match => match.Success)
            .ToArray();
        // -ss remains before the single input and -frames:v 1 remains on every output.
        // showinfo is the first filter on the selected-frame chain, so its unique n=0
        // record is the frame sent to the one-frame output. FFmpeg may pull/log n>0
        // while settling an encoder; those records did not produce the bounded output.
        var selected = records.Where(match => match.Groups["index"].Value == "0").ToArray();
        if (selected.Length != 1) return null;
        var text = selected[0].Groups["value"].Value;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;
    }

    private static string? GetString(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetNullableInt(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static long? GetNullableLong(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static double? GetNullableDouble(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static string? FormatFps(JsonElement video) => GetString(video, "avg_frame_rate") is { } fps && fps != "0/0" ? fps + " (avg_frame_rate)" : GetString(video, "r_frame_rate") is { } fallback && fallback != "0/0" ? fallback + " (r_frame_rate)" : null;
    private static string Summarize(JsonElement root) => JsonSerializer.Serialize(new { format = root.TryGetProperty("format", out var f) ? f : default, streams = root.TryGetProperty("streams", out var s) ? s : default });
    private static string Trim(string value) => value.Length <= 4000 ? value : value[..4000];
}

public sealed record GeneratedVisualFrame(string RelativePath, string FullPath, string Sha256, long ByteSize, double RequestedTimestampSeconds, double PresentationTimestampSeconds);
public sealed record GeneratedNearDuplicateFrame(int SampleIndex, double NormalizedPosition, string RelativePath, string FullPath, string Sha256, long ByteSize, double RequestedTimestampSeconds, double PresentationTimestampSeconds, string FingerprintHex);
public sealed record GeneratedThumbnail(string RelativePath, string FullPath, string Sha256, long ByteSize);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed class MediaToolException(DomainError error) : Exception(error.UserMessage) { public DomainError Error { get; } = error; }
